---
title: Architecture
---

# Architecture

[← Back to index](index.md)

One ASP.NET Core (.NET 10) service, one PostgreSQL 17 database, nothing else. The
service hosts three things: the minimal API (`/api/v1`), the Razor Pages dashboard
(`/admin`) and a background sweeper. Data access is Dapper with explicit SQL — no ORM,
no layering. Numbered SQL migrations are embedded in the assembly and applied by DbUp
at startup under a `pg_advisory_lock`; if a migration fails the process exits non-zero
rather than serve an inconsistent schema.

## Data model

Schema `jobqueue`, four tables:

| Table | Role |
|---|---|
| `jobs` | the queue itself — one row per job, with lease fields |
| `job_kinds` | one row per queue: enabled flag + defaults (TTL, attempts, lease, retention) |
| `api_keys` | hashed keys, scope, allowed kinds, revocation |
| `job_events` | append-only audit trail of every transition |

A `kind` must be declared in `job_kinds` before use (foreign key). This is deliberate:
without it, a typo on the producer side would create a ghost queue whose jobs are never
claimed and silently expire. An unknown kind is rejected with `400` and the list of
allowed kinds.

## State machine

```
pending  ──claim──▶  leased  ──complete(ok)──▶  done
   ▲                    │
   │                    ├──complete(error), attempts < max──▶  pending
   │                    ├──complete(error), attempts >= max─▶  failed
   │                    └──lease expired, attempts < max──────▶  pending  (implicit)
   │                                                                │
   └──────────────── requeue (admin) ◀──── failed / expired ─────────┘

pending / leased  ──expires_at exceeded──▶  expired   (sweeper)
pending / leased  ──cancel (admin)─────▶  cancelled
```

Rules that the test suite enforces:

- `done` and `cancelled` are terminal. Admin `requeue` works from `failed`, `expired`
  and `cancelled` — never from `done`.
- `attempts` increments **at claim time**, not at completion. A worker that crashes
  without reporting anything still consumes an attempt — that is the poison-message
  protection: after `max_attempts` the job lands in `failed` instead of looping forever.
- A job whose `expires_at` has passed is never distributed, even while still `pending`.
- Every transition writes a `job_events` row **in the same transaction** as the update.

## The claim — the heart of the service

Claiming must guarantee that two concurrent workers never receive the same job. The
core is a single locking statement:

- eligibility: `pending`, or `leased` with an expired lease; not expired; attempts
  left; kind enabled and allowed for the key;
- **fairness**: the oldest job of *each* eligible kind is selected as a candidate
  (`distinct on (kind)`), then one candidate is picked at random. There is deliberately
  no global `order by created_at`: a queue that just received 500 jobs would otherwise
  starve every other queue until it drained;
- **`for update skip locked`** on the final pick: concurrent claimers skip rows locked
  by their rivals instead of blocking or double-claiming;
- the eligibility predicates are repeated in the locking select so that PostgreSQL's
  READ COMMITTED re-check (EvalPlanQual) discards a job that a rival claimed and
  committed mid-statement;
- the lease is materialised as `lease_token` (a fresh UUID) + `lease_until`; the token
  is returned only in the claim response.

Under heavy contention the statement can return zero rows while eligible jobs remain
(the single candidate of a queue was snatched mid-flight). The handler retries in a
short loop as long as eligible jobs exist — so a burst of concurrent claims drains the
queue exactly, with no spurious `204`s.

## Leases, heartbeat, completion

`complete` and `heartbeat` are guarded by the lease token. A worker that lost its lease
(crash, pause, network partition) gets `409` and must abandon the job — the token
guarantees a zombie can never overwrite the work of the worker that took over.
Completion is idempotent: replaying the same complete with the same token is a `200`
that rewrites nothing.

## Enqueue idempotency

Idempotency lives in the database: `insert … on conflict (idempotency_key) do nothing`
followed by a re-read — never a prior `select`, which would let two simultaneous
submissions both pass. A replayed enqueue answers `200` with `created:false`; producers
can retry blindly.

## Background sweeper

Every 60 seconds, in one transaction: jobs past their TTL become `expired`; leased jobs
whose lease expired with no attempts left become `failed`
(`last_error = "lease expired, attempts exhausted"`); terminal jobs older than their
kind's `retention_days` are purged. Transitions are journaled with `actor = 'system'`.
The buffered `last_used_at` of API keys is flushed on the same tick — never one write
per request on the hot polling path.

## Security model

- Keys are `hjq_{scope}_{32 base62}` — 190 bits of entropy. Stored as SHA-256
  (`bytea`), compared in constant time. No slow hash: there is no dictionary to slow
  down, and Argon2 on the 30-second polling path would be a design error.
- The clear-text key exists once, at creation. Logs only ever contain the prefix.
- Producer reads outside the key's `allowed_kinds` return `404`, never `403` — no
  existence oracle across queues.
- The API is designed for the public internet: body-size caps at the server level,
  two-tier rate limiting (per key, else per IP), neutral 404s, no `Server` header, no
  CORS, cookie-and-CSP hardening confined to `/admin`.

See [Deployment](deployment.md) for the infrastructure view.
