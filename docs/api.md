---
title: API reference
---

# API reference

[← Back to index](index.md)

Base prefix: `/api/v1`. Bodies are JSON in `camelCase`. Errors use
`application/problem+json`. Authentication is a bearer API key:

```
Authorization: Bearer hjq_{scope}_{32 base62 characters}
```

Three scopes: `producer` (enqueue and read back), `worker` (claim / heartbeat /
complete), `admin` (everything). A key only reaches its own routes — anything else is
`403`. A missing or invalid key is `401`. Every key carries `allowed_kinds`: the queues
it may touch.

Timestamps are UTC ISO-8601. Job `payload` and `result` are opaque JSON — the service
never inspects them.

## POST /jobs — scope `producer`

Enqueue a job, idempotently.

```bash
curl -s https://hopper.example.com/api/v1/jobs \
  -H "Authorization: Bearer $PRODUCER_KEY" -H 'Content-Type: application/json' \
  -d '{
    "idempotencyKey": "cron:2026-08-17T03:00",
    "kind": "invoice-ocr",
    "project": "accounting",
    "payload": { "documentRef": "s3://bucket/doc.pdf" },
    "ttlSeconds": 86400,
    "maxAttempts": 3
  }'
```

| Field | Notes |
|---|---|
| `idempotencyKey` | required, ≤ 200 chars, chosen and owned by the producer (a per-producer prefix like `cron:…` is good practice) |
| `kind` | required, must be a declared kind allowed for this key |
| `project` | optional grouping label for dashboard filtering |
| `payload` | required, opaque JSON, ≤ 32 KiB serialized |
| `ttlSeconds` | optional, 1–604800; default from the kind |
| `maxAttempts` | optional, 1–10; default from the kind |

Responses:

- `201` `{"id":42,"status":"pending","created":true}`
- `200` `{"id":42,"status":"…","created":false}` — the idempotency key already exists.
  Replaying a submission is **not** an error; producers can retry blindly.
- `400` — missing field, payload too large, or unknown/not-allowed kind (the response
  lists the kinds allowed for your key).

## POST /jobs/claim — scope `worker`

Fetch one job and lease it.

```bash
curl -s https://hopper.example.com/api/v1/jobs/claim \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "workerId": "shop-worker", "leaseSeconds": 1200, "kinds": ["invoice-ocr"] }'
```

- `workerId` — required; shows up in the dashboard and the audit trail.
- `leaseSeconds` — optional, 1–86400; default from the claimed job's kind.
- `kinds` — optional; intersected with the key's `allowed_kinds`. Omitted = all the
  key's kinds. Empty intersection = `403`.

Responses:

- `200` — the full job **including `leaseToken` and `leaseUntil`**. The lease token is
  returned here and nowhere else; keep it for heartbeat and complete.
- `204` — nothing eligible (empty, paused, or all leased). Poll again later; ~30 s is a
  sensible pace.

Selection is fair across queues: the oldest job of *each* eligible kind is a candidate
and one is picked at random, so a queue that just received 500 jobs cannot starve the
others. Paused kinds (`enabled = false`) are skipped. A job whose lease expired while
still `leased` is claimable again; each claim increments `attempts`.

## POST /jobs/{id}/heartbeat — scope `worker`

Extend the lease while working.

```bash
curl -s https://hopper.example.com/api/v1/jobs/42/heartbeat \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "leaseToken": "'$LEASE_TOKEN'", "leaseSeconds": 1200 }'
```

- `200` `{"id":42,"leaseUntil":"…"}` — lease extended.
- `409` — token mismatch or job no longer leased. **The lease is lost: stop working on
  this job.** Another worker may already own it.

## POST /jobs/{id}/complete — scope `worker`

Return the outcome.

```bash
curl -s https://hopper.example.com/api/v1/jobs/42/complete \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "leaseToken": "'$LEASE_TOKEN'", "outcome": "success",
        "result": { "report": "…", "costUsd": 0.42, "durationMs": 91000 } }'
```

- `outcome` — `"success"` or `"failure"`; `error` is required on failure.
- `result` — optional opaque JSON, ≤ 256 KiB serialized. Store large deliverables in
  object storage and pass a reference.

Responses:

- `200` with the computed final status: `done` on success; on failure `pending` (retry
  possible) or `failed` (attempts exhausted).
- `409` — stale lease token. A zombie worker can never overwrite a result written by
  the worker that took the job over.
- Replaying the same complete with the same token returns `200` without rewriting
  anything (idempotent).

## GET /jobs/{id} and GET /jobs/by-key/{idempotencyKey} — scope `producer`

Read state and result back — this is the return channel; there are no callbacks.

```bash
curl -s https://hopper.example.com/api/v1/jobs/42 -H "Authorization: Bearer $PRODUCER_KEY"
curl -s https://hopper.example.com/api/v1/jobs/by-key/cron:2026-08-17T03:00 \
  -H "Authorization: Bearer $PRODUCER_KEY"
```

Returns the job (status, attempts, timestamps, `payload`, `result`, `lastError`) —
never the `leaseToken`. If the job's kind is not among the key's `allowed_kinds` the
answer is `404` — not `403` — so keys cannot probe for the existence of other queues'
jobs. The by-key variant spares the producer from storing the numeric id.

## Admin endpoints — scope `admin`

```bash
# paginated list (50/page), newest first; all filters optional
curl -s 'https://hopper.example.com/api/v1/jobs?status=failed&kind=invoice-ocr&project=accounting&q=timeout&page=1' \
  -H "Authorization: Bearer $ADMIN_KEY"

# detail including the full job_events timeline
curl -s https://hopper.example.com/api/v1/jobs/42 -H "Authorization: Bearer $ADMIN_KEY"

# back to pending, attempts reset to 0 — allowed from failed/expired/cancelled, never from done
curl -s -X POST https://hopper.example.com/api/v1/jobs/42/requeue -H "Authorization: Bearer $ADMIN_KEY"

# cancel — allowed from pending/leased
curl -s -X POST https://hopper.example.com/api/v1/jobs/42/cancel -H "Authorization: Bearer $ADMIN_KEY"

# counters per status, oldest pending age, 24 h throughput, per-worker last activity
curl -s https://hopper.example.com/api/v1/stats -H "Authorization: Bearer $ADMIN_KEY"
```

Invalid transitions (requeue of a `done` job, cancel of a `failed` one…) return `409`
with an explanatory message.

## Health

```bash
curl -s https://hopper.example.com/healthz   # alive — no auth, does not touch the database
curl -s https://hopper.example.com/readyz    # 200 if PostgreSQL answers, 503 otherwise (no detail)
```

## Limits and rate limiting

| Limit | Value |
|---|---|
| Request body — all routes | 64 KiB (Kestrel) |
| Request body — `/complete` | 512 KiB |
| `payload` serialized | 32 KiB |
| `result` serialized | 256 KiB |
| Authenticated requests | 300/min per API key (sliding window) |
| Unauthenticated requests | 60/min per client IP |

Over-limit requests receive `429`. The per-key window is deliberately generous:
30-second polling is the intended usage.
