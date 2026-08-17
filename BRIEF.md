# Brief — HopperJobQueue

Small self-contained HTTP service acting as a work queue between **arbitrary producers**
and one or more workers running on machines behind NAT.

The worker can only make **outbound** HTTPS calls: that constraint dictates the entire
architecture. No inbound socket, no direct database access, no tunnel.

---

## 1. Role and scope

The service does only three things:

1. Accept jobs from **any producer**, idempotently.
2. Hand them to a worker that comes to fetch them, with **lease** semantics: a job that
   was reserved and never returned becomes available again on its own.
3. Keep the history and expose it in a small dashboard for manual control.

### The service is agnostic — structural point

The first producer will be an n8n instance, but **n8n is just one producer among
others** and the service must know nothing about it. Also expected, without any code
change: shell or PowerShell scripts, scheduled jobs, an ASP.NET application, a GitHub
webhook, a manual `curl` call from a workstation.

Consequences to follow to the letter:

- **No mention of n8n, Gmail or e-mail** in the code, type names, columns, error
  messages or API documentation. The business domain is carried entirely by `kind` and
  `payload`, which the service treats as opaque.
- The examples in this brief (`kind: "project-preanalysis"`, `idempotencyKey: "gmail:…"`,
  a payload containing a subject and a sender) are **illustrations, not a schema**. Never
  type the payload nor validate its inner fields.
- The only contract imposed on the producer is to provide a stable idempotency key that
  it controls. A per-producer prefix (`gmail:…`, `github:…`, `cron:…`) is a good practice
  to document, not a rule to validate server-side.
- Each producer has **its own API key**, with its own `allowed_kinds`. Two producers
  never share a key: that is what makes targeted revocation possible.

### Out of scope — do not implement

- No work execution in this service. It launches no process, no agent, no analysis. It
  stores and distributes, nothing else.
- No multi-tenant, no notion of organisation or user.
- No priorities, no scheduled jobs, no dependencies between jobs.
- No WebSocket or SSE. The worker polls; that is intentional.
- **No outbound HTTP callback.** The service never calls anything external: no
  end-of-job webhook, no notification. Producers re-read state via `GET /jobs/{id}`.
  This avoids retry management, timeouts and SSRF risk, for a benefit that polling
  already covers.
- No mail content in the database. The payload contains only metadata and identifiers
  (see §5).
- No heavy ORM, no speculative layering, no MediatR-style mediator.

If a feature seems to be missing, **ask before adding it**.

---

## 2. Naming — use as-is

All identifiers derive from the service name. Do not invent variants.

| Element | Value |
|---|---|
| Git repository | `hopper-jobqueue` |
| Solution / root namespace | `HopperJobQueue` |
| Projects | `HopperJobQueue.Api`, `HopperJobQueue.Tests` |
| Docker image | `hopper-jobqueue` |
| `compose` services | `hopper` (API), `hopper-db` (PostgreSQL) |
| Docker volume | `hopper-pgdata` |
| Docker networks | `traefik-public`, `hopper-internal` |
| PostgreSQL database | `hopper` |
| PostgreSQL schema | `jobqueue` |
| Env variable prefix | `HOPPER_` |
| API key prefix | `hjq_` |
| Traefik routers | `hopper-api`, `hopper-admin` |
| Host (example) | `hopper.exemple.ch` |

Database `hopper` + schema `jobqueue`: the qualified name `jobqueue.jobs` stays explicit
in queries, and the dedicated schema leaves room for other schemas in the same instance
if the need arises.

---

## 3. Imposed stack

| Choice | Value | Why |
|---|---|---|
| Runtime | .NET 10, minimal API, C# | existing environment |
| Database | PostgreSQL 17 | container dedicated to this service, schema `jobqueue` |
| Data access | Npgsql + Dapper | one and a half tables, an ORM brings nothing |
| Migrations | numbered SQL scripts + DbUp | deterministic, readable, no magic |
| Dashboard | Razor Pages, server-rendered | zero front-end build, zero npm |
| Logs | `Microsoft.Extensions.Logging` + Serilog JSON console | simple aggregation |
| Tests | xUnit + Testcontainers.PostgreSql | concurrency tests require a real database |

Constraints:

- No NuGet dependency outside this list without prior validation.
- All timestamps in UTC, `timestamptz` columns, `DateTimeOffset` in C#.
- Configuration through environment variables only (no secrets in files).
- The service runs behind Traefik, which terminates TLS. Do not manage certificates, do
  not enable `UseHttpsRedirection` or HSTS, and configure `ForwardedHeadersOptions` as
  per §13 — the default configuration does not work in Docker.

---

## 4. Data model

Schema `jobqueue`. Initial migration:

```sql
create schema if not exists jobqueue;

create table jobqueue.job_kinds (
  name                 text        primary key,
  description          text,
  enabled              boolean     not null default true,
  default_ttl_seconds  int         not null default 86400,
  default_max_attempts int         not null default 3,
  default_lease_seconds int        not null default 1200,
  retention_days       int         not null default 90,
  created_at           timestamptz not null default now()
);

create table jobqueue.jobs (
  id             bigserial     primary key,
  idempotency_key text         not null unique,
  kind           text          not null references jobqueue.job_kinds(name),
  project        text,
  payload        jsonb         not null,
  status         text          not null default 'pending',
  attempts       int           not null default 0,
  max_attempts   int           not null default 3,
  lease_token    uuid,
  lease_until    timestamptz,
  worker_id      text,
  created_at     timestamptz   not null default now(),
  expires_at     timestamptz   not null,
  finished_at    timestamptz,
  result         jsonb,
  last_error     text,
  constraint jobs_status_check check (status in
    ('pending','leased','done','failed','expired','cancelled'))
);

create index jobs_claim_idx on jobqueue.jobs (status, created_at)
  where status in ('pending','leased');

create table jobqueue.api_keys (
  id          bigserial   primary key,
  name        text        not null,
  prefix      text        not null unique,
  key_hash    bytea       not null,
  scope       text        not null,
  allowed_kinds text[]    not null default '{}',
  created_at  timestamptz not null default now(),
  last_used_at timestamptz,
  revoked_at  timestamptz,
  constraint api_keys_scope_check check (scope in ('producer','worker','admin'))
);

create table jobqueue.job_events (
  id         bigserial   primary key,
  job_id     bigint      not null references jobqueue.jobs(id) on delete cascade,
  at         timestamptz not null default now(),
  from_status text,
  to_status  text        not null,
  actor      text        not null,
  note       text
);

create index job_events_job_idx on jobqueue.job_events (job_id, at);
```

`job_events` is the audit trail: **every** state transition writes a row there, in the
same transaction as the `jobs` update. That is what makes the dashboard useful and
incidents diagnosable.

### State machine

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

Invariant rules, to be covered by tests:

- A job in `done` or `cancelled` is terminal: no transition leaves it except an explicit
  `requeue` from the dashboard, and never from `done`.
- `attempts` is incremented **at claim time**, not at complete time. A worker that
  crashes without returning anything therefore consumes an attempt — that is the poison
  message protection.
- A job whose `expires_at` has passed is **never** distributed, even if `pending`.

### `kind` = queue name

The service is multi-purpose: `kind` is both the queue name and the configuration key.
Consequences to respect:

- A `kind` must be **declared in `job_kinds` before use**. A foreign key constraint
  enforces it. A producer sending an unknown `kind` receives `400` with the list of
  `kind`s allowed for its key. Without that, a typo on the producer side creates a ghost
  queue whose jobs are never claimed and silently expire.
- `job_kinds.enabled = false` pauses a queue: jobs keep being accepted but are no longer
  distributed. Useful operational control, driven from the dashboard, without touching
  producers.
- TTL, attempts and lease duration defaults come from `job_kinds`, not hard-coded
  constants. An invoice OCR and a repository pre-analysis do not have the same orders of
  magnitude. Values provided in the enqueue request override these defaults.
- `project` is **optional**: it is a simple grouping label for dashboard filtering, not
  a structural notion. All business context goes in `payload`.

---

## 5. API

Prefix `/api/v1`. JSON bodies, `camelCase`. Errors in `application/problem+json` format.

### `POST /jobs` — scope `producer`

```jsonc
// request
{
  "idempotencyKey": "gmail:19a3f2c8b1d4e5f6",  // required, <= 200 chars
  "kind": "project-preanalysis",               // required
  "project": "my-project",                     // required
  "payload": { "subject": "...", "summary": "...", "sender": "..." },
  "ttlSeconds": 86400,                         // optional, default 86400, max 604800
  "maxAttempts": 3                             // optional, default 3, max 10
}
```

Responses:

- `201 Created` + `{ "id": 42, "status": "pending", "created": true }`
- `200 OK` + `{ "id": 42, "status": "leased", "created": false }` if the idempotency key
  already exists. **Do not return 409.** A producer must be able to replay a submission
  without it looking like an error.
- `400` if the serialized `payload` exceeds **32 KiB**, or if a required field is
  missing.

Idempotency is done in the database (`on conflict (idempotency_key) do nothing` then
re-read), not with a prior `select` — otherwise two simultaneous requests both get
through.

### `POST /jobs/claim` — scope `worker`

```jsonc
// request
{ "workerId": "dev-etienne", "leaseSeconds": 1200, "kinds": ["project-preanalysis"] }
```

- `200 OK` + the full job, **including `leaseToken`** (uuid) and `leaseUntil`.
- `204 No Content` if the queue is empty. No body, no error.

Reservation query, atomic, in a single statement:

```sql
update jobqueue.jobs set
  status      = 'leased',
  attempts    = attempts + 1,
  lease_token = gen_random_uuid(),
  lease_until = now() + (@leaseSeconds || ' seconds')::interval,
  worker_id   = @workerId
where id = (
  select id from jobqueue.jobs
  where (status = 'pending' or (status = 'leased' and lease_until < now()))
    and expires_at > now()
    and attempts < max_attempts
    and kind = any(@kinds)
  order by created_at
  limit 1
  for update skip locked
)
returning *;
```

The `for update skip locked` is **mandatory**: without it, two concurrent claims can get
the same job. This is the single most important point of the service.

Two additions related to multi-queue:

- The requested `kinds` are **intersected with the key's `allowed_kinds`** before the
  query. A worker can never claim a job from a queue it was not assigned, even by asking
  for it explicitly. If the intersection is empty, `403`.
- The join must exclude `kind`s whose `job_kinds.enabled = false`.
- **Fairness across queues — mandatory.** A worker serves several `kind`s, so a global
  `order by created_at` is ruled out: a queue receiving 500 jobs at once would starve
  all the others until it drained. The rule is: take the oldest job of *each* eligible
  queue, then pick one at random.

Two PostgreSQL restrictions make the naive version impossible — knowing them saves half
an hour of trial and error:

- `select distinct on (…) … for update` is refused ("FOR UPDATE is not allowed with
  DISTINCT clause").
- A locking clause cannot be applied to the result of a CTE ("FOR UPDATE cannot be
  applied to a WITH query").

Two levels are therefore needed: a non-locking sub-select that designates the
candidates, then a locking select that retains one.

```sql
update jobqueue.jobs set
  status      = 'leased',
  attempts    = attempts + 1,
  lease_token = gen_random_uuid(),
  lease_until = now() + (@leaseSeconds || ' seconds')::interval,
  worker_id   = @workerId
where id = (
  select id from jobqueue.jobs
  where id in (
    select distinct on (kind) id
    from jobqueue.jobs j
    join jobqueue.job_kinds k on k.name = j.kind
    where j.kind = any(@kinds)
      and k.enabled
      and (j.status = 'pending' or (j.status = 'leased' and j.lease_until < now()))
      and j.expires_at > now()
      and j.attempts < j.max_attempts
    order by j.kind, j.created_at
  )
  order by random()
  limit 1
  for update skip locked
)
returning *;
```

The candidate set holds at most one row per queue, so the `order by random()` applies to
a handful of rows: the cost is negligible and there is no server-side state to maintain
to rotate the queues.

### `POST /jobs/{id}/heartbeat` — scope `worker`

```jsonc
{ "leaseToken": "…", "leaseSeconds": 1200 }
```

Extends `lease_until`. `200` with the new `leaseUntil`, `409` if the token does not
match or if the job is no longer `leased`. The worker must treat this 409 as "I lost the
lease, I give up" — so the error message must be explicit about that.

### `POST /jobs/{id}/complete` — scope `worker`

```jsonc
{
  "leaseToken": "…",
  "outcome": "success",          // "success" | "failure"
  "result": { "report": "…", "costUsd": 0.42, "durationMs": 91000 },
  "error": null                  // required if outcome = "failure"
}
```

- `200` with the computed final status (`done`, `pending` if a retry is possible, or
  `failed`).
- `409` if the `leaseToken` does not match. **Critical case**: a zombie worker that lost
  its lease must not be able to overwrite the result of a worker that took the job over.
- Idempotent: replaying the same complete with the same token returns `200` without
  rewriting.

The serialized `result` is capped at **256 KiB**. Beyond that, `400`: large deliverables
(full report, generated file) go to object storage and only their reference passes here.

### `GET /jobs/{id}` — scope `producer`

A producer must be able to re-read the state and result of the jobs **it created**,
otherwise it has no way to retrieve the work without a callback. Returns the job if its
`kind` belongs to the key's `allowed_kinds`, `404` otherwise — not `403`, to avoid
revealing the existence of jobs in other queues.

A variant by idempotency key, `GET /jobs/by-key/{idempotencyKey}`, saves the producer
from storing the numeric `id`: it finds its job with the identifier it already knows.

### Administration endpoints — scope `admin`

| Method | Route | Effect |
|---|---|---|
| `GET` | `/jobs?status=&project=&kind=&q=&page=` | paginated list, sorted by `created_at` desc |
| `GET` | `/jobs/{id}` | detail + `job_events` timeline |
| `POST` | `/jobs/{id}/requeue` | back to `pending`, resets `attempts` to 0, journaled |
| `POST` | `/jobs/{id}/cancel` | moves to `cancelled` |
| `GET` | `/stats` | count per status, age of oldest `pending`, 24 h throughput |

### Health

- `GET /healthz` — alive, no auth, without touching the database.
- `GET /readyz` — checks the Postgres connection. No auth but no error detail.

---

## 6. Authentication

Three scopes: `producer` (enqueue only), `worker` (claim/heartbeat/complete only),
`admin` (everything + dashboard). A scope only grants access to its own routes — a
worker token calling `/jobs` in POST receives `403`.

Key format: `hjq_{scope}_{32 base62 characters}`, e.g. `hjq_worker_7Kf2…`.
The `prefix` stored in clear text is the first 12 characters, to identify a key in the
dashboard and in logs without exposing the secret.

Storage: **SHA-256 of the secret**, as `bytea`. No Argon2 or bcrypt here — the key has
190 bits of random entropy, there is no dictionary to slow down, and a slow hash on the
hot polling path would be a design mistake.

Points of vigilance:

- **Constant-time** comparison (`CryptographicOperations.FixedTimeEquals`).
- The clear-text key exists only once, at creation time: shown in the response then
  never retrievable. The dashboard must say so explicitly.
- Never a key in the logs, neither whole nor partial — only the `prefix`.
- Transport via `Authorization: Bearer hjq_…` header. No key in query strings.
- `last_used_at` updated at most once per minute per key, in a background task. Do not
  `update` on every request: the worker polls every 30 seconds, that would generate
  continuous pointless writes.
- Rate limiting via `Microsoft.AspNetCore.RateLimiting`, two-tier: sliding window **per
  key** for authenticated requests (generous on `/jobs/claim`, polling is legitimate),
  and a window **per client IP** for requests without a valid key. See §12.

### Bootstrap

On first start, if the `api_keys` table is empty, the service creates an `admin` key,
writes it **once** to the logs at `Warning` level with a clear instruction, and never
returns to it. Accepted alternative: a `HOPPER_BOOTSTRAP_ADMIN_KEY` variable.

---

## 7. Dashboard

Razor Pages, server-rendered, on `/admin`. Sign-in by entering an admin key, exchanged
for a session cookie (`HttpOnly`, `Secure`, `SameSite=Strict`). Antiforgery on all POST
actions.

Four pages are enough:

1. **Overview** — counters per status, age of the oldest `pending`, latest activity per
   worker. This is the page that answers "is it running?".
2. **List** — status / project / search filters, pagination. Inline actions: requeue,
   cancel.
3. **Detail** — payload and result as formatted JSON, `job_events` timeline, last error
   in full.
4. **Keys** — list (name, prefix, scope, last use), creation, revocation.

Form constraints: no external CSS framework, no build JS. One hand-written CSS file, JS
only for folding JSON blocks. The list page must be readable at 1200 px without
horizontal scrolling. Overview auto-refresh via `<meta http-equiv="refresh">` every
30 s — sufficient, and zero code.

---

## 8. Background task

A single `BackgroundService`, every 60 seconds, in one transaction:

1. `pending` or `leased` jobs whose `expires_at < now()` move to `expired`.
2. `leased` jobs whose `lease_until < now()` and `attempts >= max_attempts` move to
   `failed` with `last_error = "lease expired, attempts exhausted"`. Those with attempts
   left are left as-is: the claim query picks them up naturally.
3. Purge of terminal jobs older than 90 days (configurable duration).

Every transition writes to `job_events` with `actor = 'system'`.

---

## 9. Required tests

Unit tests on trivial logic interest nobody. What must be covered, with Testcontainers
and a real database:

1. **Concurrent claim** — 20 parallel claims over 5 jobs: exactly 5 succeed, no job
   distributed twice, 15 `204` responses. This is the test that justifies the project.
2. **Concurrent enqueue** — 10 simultaneous POSTs with the same idempotency key: a
   single job created, all 10 responses consistent.
3. **Expired lease** — a claimed then abandoned job becomes claimable again after
   expiry, with `attempts` correctly incremented.
4. **Stale lease token** — worker A claims, its lease expires, worker B claims, then A
   attempts a `complete`: it receives `409` and B's job is not altered.
5. **Poison message** — a job claimed and abandoned `max_attempts` times ends in
   `failed` and is never distributed again.
6. **Scope isolation** — each scope receives `403` on the routes of the two others.
7. **TTL** — a job whose `expires_at` has passed is not distributed even when `pending`.
8. **Queue isolation** — a worker key limited to `kind-a` never receives a `kind-b`
   job, including when it asks for it explicitly and the `kind-b` queue is the only
   non-empty one.
9. **Paused queue** — `enabled = false`: enqueue succeeds, claim returns `204`.
10. **Fairness** — two queues, 200 jobs in the first and 3 in the second. A worker
    claiming both gets the 3 jobs of the small queue in fewer than 10 claims. Without
    fair selection, it would take ~200.

**No load test.** The target volume is a few dozen jobs per day: measuring throughput
would say nothing useful. What breaks here is not load but **concurrency**, and test 1
already covers it — 20 simultaneous claims over 5 jobs probe exactly the same code path
as two workers in production. If several workers ever really run, upgrading test 1 to
two separate processes rather than two tasks will be enough.

The operational signal that replaces the load test is the age of the oldest `pending`,
already exposed by `/stats`: if it climbs, the worker is dead or saturated. It is the
only metric to watch.

---

## 10. Build order

Deliver in stages, each functional and tested before moving to the next.

1. Skeleton, `/healthz`, DbUp migrations, Postgres connection, docker-compose for dev.
2. `jobs` table, `POST /jobs` with idempotency, `POST /jobs/claim` with lease. Tests 1
   to 3.
3. Heartbeat and complete with `leaseToken` verification. Tests 4 and 5.
4. API keys and scopes. Test 6. Admin key bootstrap.
5. `job_events` on all transitions, admin endpoints.
6. Dashboard.
7. Background task, rate limiting, test 7.
8. Dockerfile, `compose.yaml` with Traefik and the two networks, README with environment
   variables and one `curl` example per endpoint.
9. `pg_dump` backup with retention, and a tested restore procedure.

---

## 11. Definition of "done"

- `dotnet test` passes, including the ten scenarios of §9.
- No compilation warnings. `<TreatWarningsAsErrors>` enabled.
- README: environment variables, deployment procedure, one `curl` per endpoint, and a
  full cycle example enqueue → claim → heartbeat → complete.
- A `CLAUDE.md` at the root summarising the architecture, the §4 invariants and useful
  commands, for future sessions.
- No secret or connection string in the repository. `.gitignore` covering
  `appsettings.Development.json` and `launchSettings.json`.
- `pg_dump` backup in place and **restore performed once**, with the procedure recorded
  in the README.

---

## 12. Public exposure

The API is **open to the public internet**. This is a deliberate choice: producers are
distributed and arbitrary, and workers are behind NAT, so all calls — production and
consumption alike — are inbound HTTPS requests from anywhere.

That changes the threat model. All `/api` routes must hold up against continuous
automated scanners, not only cooperative clients.

### What is public and what is not

| Surface | Exposure |
|---|---|
| `/api/v1/jobs` (POST, GET) | public, `producer` key required |
| `/api/v1/jobs/claim`, `/heartbeat`, `/complete` | public, `worker` key required |
| `/api/v1/jobs` (admin routes) | public, `admin` key required |
| `/admin` (dashboard) | public but **IP-restricted** at the Traefik level |
| `/healthz`, `/readyz` | public, no auth, no detail |
| PostgreSQL | never exposed, internal network only |

### Mandatory hardening

- **Body size limit at the Kestrel level** (`MaxRequestBodySize`), not just application
  validation: 64 KiB on `/jobs`, 512 KiB on `/complete`. Otherwise a multi-gigabyte body
  is read in full before being rejected. Double it with a
  `buffering.maxRequestBodyBytes` on the Traefik side.
- **Two-tier rate limiting.** For authenticated requests, per API key — that is the
  counter that matters. For requests **without a valid key**, per client IP read from
  `X-Forwarded-For`, with a low threshold: it is the only protection against sweeps, and
  it only works if `ForwardedHeadersOptions` is configured correctly (§13). A Traefik
  `ratelimit` upstream acts as a safety net.
- **Log authentication failures** with the IP and the attempted key prefix, but at
  `Information` level, not `Warning`: on a public IP the background noise of scanners
  would saturate alerts.
- **404 for any unknown path**, no error page, no `Server` header, no framework
  version. `app.UseExceptionHandler` returning neutral `problem+json`; never
  `DeveloperExceptionPage` in production, never a stack trace in a response.
- **No enumeration.** `GET /jobs/{id}` on a job from another queue returns `404`, never
  `403` — already specified in §5, but this is where the reason becomes concrete.
- **No CORS.** No producer is a browser. Do not add a CORS policy, even a permissive one
  "for testing".
- Security headers on `/admin` (`X-Content-Type-Options`, `Referrer-Policy`, restrictive
  `Content-Security-Policy`): it is the only surface with cookie sessions, hence the
  only one where XSS is of interest to an attacker.
- Traefik's `ipallowlist` on `/admin` assumes stable IPs. If your connection is dynamic,
  plan a wide range rather than disabling the protection.

---

## 13. Deployment — container behind Traefik

Settled target: PostgreSQL 16+, .NET 10, Docker image, Traefik front on the same host as
n8n. The container publishes **no port on the host** — `expose` only, Traefik reaches
the service via the Docker network.

### Database

PostgreSQL container **dedicated to this service**. n8n stays on its SQLite and is not
touched.

- Major version **pinned**: `postgres:17`, never `latest` nor `postgres`. An image that
  moves to the next major refuses to start on an existing data directory and forces a
  `pg_upgrade` or a dump/restore cycle. The README must mention this constraint next to
  the chosen version.
- Named volume for `/var/lib/postgresql/data`. No bind mount on the host.
- No published port, only on `hopper-internal`.
- Daily `pg_dump` via a backup container or a host cron task, compressed, with rolling
  retention (7 daily, 4 weekly).
- The **restore** procedure must be written in the README and actually performed once. A
  backup that was never restored is not a backup.

### Image

- Multi-stage on `mcr.microsoft.com/dotnet/aspnet:10.0` for runtime.
- `USER $APP_UID`: no root in the container.
- `ASPNETCORE_URLS=http://+:8080`. No TLS in the container, no certificate.
- **Do not call `UseHttpsRedirection()` or `UseHsts()`.** Traefik terminates TLS and
  handles redirection; enabling them here causes at best a redirect loop, at worst URLs
  generated as `http` on an internal port.
- `HEALTHCHECK` on `/healthz`, so that `depends_on: condition: service_healthy` works.
- Clean shutdown: the service must finish the in-flight HTTP job on `SIGTERM`. Plan a
  `stop_grace_period` margin above ASP.NET's `ShutdownTimeout`.

### Proxy headers — the trap not to miss

`ForwardedHeadersOptions` must handle `XForwardedFor` and `XForwardedProto`, and
**`KnownNetworks` and `KnownProxies` must be emptied**. In Docker, Traefik's IP is that
of the bridge network, it changes on every recreation, and ASP.NET's default allowlist
then silently rejects the headers: `Secure` cookies stop working and the application
believes it is on plain HTTP. It is a classic and painful failure to diagnose.

Authenticated rate limiting stays **key by API key**. The per-IP limiter of §12 depends
entirely on this configuration: without valid forwarded headers, all requests appear to
come from Traefik and the per-IP counter would throttle everyone together.

### Networks

Two Docker networks, and this is structural:

- `traefik-public` — the service alone is attached to it, with its routing labels.
- `hopper-internal` — the service and PostgreSQL. **Postgres is never on the public
  network and publishes no port.**

### Traefik labels

One router for the API, a second for `/admin` so the latter can be hardened
independently:

```yaml
labels:
  - traefik.enable=true
  - traefik.docker.network=traefik-public
  - traefik.http.services.hopper.loadbalancer.server.port=8080

  - traefik.http.routers.hopper-api.rule=Host(`hopper.exemple.ch`) && PathPrefix(`/api`)
  - traefik.http.routers.hopper-api.entrypoints=websecure
  - traefik.http.routers.hopper-api.tls.certresolver=letsencrypt

  - traefik.http.routers.hopper-admin.rule=Host(`hopper.exemple.ch`) && PathPrefix(`/admin`)
  - traefik.http.routers.hopper-admin.entrypoints=websecure
  - traefik.http.routers.hopper-admin.tls.certresolver=letsencrypt
  - traefik.http.routers.hopper-admin.middlewares=hopper-admin-allow
  - traefik.http.middlewares.hopper-admin-allow.ipallowlist.sourcerange=…
```

The IP allowlist on `/admin` is an additional defence, not a replacement: admin key
authentication remains required behind it.

### Migrations

DbUp runs at startup, before the service accepts traffic. Take a `pg_advisory_lock`
during migration: harmless with a single instance, indispensable the day the container
is recreated before the previous one has fully stopped. If the migration fails, the
process exits non-zero — no starting on an inconsistent database.

### Environment variables

`HOPPER_` as prefix. At minimum: connection string, `HOPPER_BOOTSTRAP_ADMIN_KEY`, log
level. The bootstrap key will appear in `docker logs` if it is generated automatically —
the README must say to revoke it after creating the real keys.

### Deliverables

`Dockerfile`, complete `compose.yaml` (service + Postgres + both networks + labels), and
a development `compose.override.yaml` that publishes the port and hot-mounts the code.

---

## 14. Settled decisions

Everything is decided. Do not revisit these points without discussing first:

- PostgreSQL 17 in a dedicated container. No sharing with n8n, which stays on SQLite.
- **The API is public on the internet.** n8n is just one producer among others, and
  nothing in the code must mention it or assume its existence.
- .NET 10, container behind Traefik, no port published on the host.
- **A worker serves several queues.** The fair selection of §5 is therefore mandatory,
  not optional, and test 10 verifies it.
- No outbound HTTP callback. Producers poll `GET /jobs/{id}`.
- No load test. The §9 concurrency tests are what matters.
- A single worker in practice at the start, but the design and the tests assume there
  may be several: take no shortcut that would assume worker uniqueness.
