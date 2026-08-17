# hopper-jobqueue

HTTP work queue between **arbitrary producers** (scripts, applications, webhooks,
`curl`…) and one or more **workers behind NAT** that can only make outbound HTTPS
calls. The service stores and distributes jobs with **lease** semantics; it executes
nothing and never calls anything external — producers re-read state via
`GET /jobs/{id}`.

Stack: .NET 10 (minimal API + Razor Pages), PostgreSQL 17, Npgsql + Dapper, DbUp,
Serilog (JSON console), xUnit + Testcontainers.

## Quick start (dev)

```bash
docker network create traefik-public   # once (shared with Traefik in prod)
cp .env.example .env                   # set HOPPER_DB_PASSWORD
docker compose up -d                   # compose.override.yaml publishes :8080 + dotnet watch
curl http://localhost:8080/healthz
```

The dashboard is at `http://localhost:8080/admin`. On first start, if the key table is
empty, a bootstrap admin key is written **once** to the logs
(`docker compose logs hopper | grep bootstrap`). Use it to sign in, declare a kind
(`/admin/kinds`), create the real keys (`/admin/keys`), then **revoke the bootstrap
key** — it went through the logs.

Tests (real PostgreSQL via Testcontainers, Docker required):

```bash
dotnet test
```

## Environment variables

| Variable | Purpose |
|---|---|
| `HOPPER_DB_CONNECTIONSTRING` | Npgsql connection string (**required**) — assembled automatically by `compose.yaml` from `HOPPER_DB_PASSWORD` |
| `HOPPER_DB_PASSWORD` | PostgreSQL password, read from `.env` by compose |
| `HOPPER_BOOTSTRAP_ADMIN_KEY` | optional — provided bootstrap admin key (`hjq_admin_{32 base62}`) instead of a generated + logged one |
| `HOPPER_PUBLIC_HOST` | public host of the Traefik router (e.g. `hopper.exemple.ch`) |
| `HOPPER_ADMIN_IP_ALLOWLIST` | IP ranges allowed on `/admin` (Traefik middleware) |
| `HOPPER_LOG_LEVEL` | `Verbose`…`Error`, default `Information` |
| `HOPPER_SWEEP_INTERVAL_SECONDS` | background task period, default `60` |

No secrets in files: everything goes through the environment (`.env` is git-ignored).

## Model

- A job belongs to a queue (`kind`), **declared before use** (dashboard → Kinds).
  Unknown `kind` ⇒ `400` with the list of kinds allowed for the key.
- Statuses: `pending → leased → done | failed | expired | cancelled`. `attempts`
  increments **at claim** (poison message protection). A job whose `expires_at` has
  passed is never distributed. `done` and `cancelled` are terminal (admin `requeue`
  possible from `failed`/`expired`/`cancelled`, never from `done`).
- Every transition writes to `jobqueue.job_events` (the dashboard's audit trail).
- API keys: `hjq_{scope}_{32 base62}`, scopes `producer` / `worker` / `admin` (admin =
  everything). SHA-256 storage, constant-time comparison, clear-text prefix for
  identification. One key per producer and per worker, each with its own
  `allowed_kinds`: revocation stays targeted.

## API

Prefix `/api/v1`, `camelCase` JSON, errors as `application/problem+json`,
`Authorization: Bearer hjq_…`. `curl` examples (replace the key and host):

### `POST /jobs` — scope `producer`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs \
  -H "Authorization: Bearer $PRODUCER_KEY" -H 'Content-Type: application/json' \
  -d '{
    "idempotencyKey": "cron:2026-08-17T03:00",
    "kind": "invoice-ocr",
    "project": "accounting",
    "payload": { "documentRef": "s3://bucket/doc.pdf" },
    "ttlSeconds": 86400,
    "maxAttempts": 3
  }'
# 201 {"id":42,"status":"pending","created":true}
# 200 {"id":42,"status":"pending","created":false}  if the idempotency key already exists (replay without error)
# 400 if a required field is missing, payload > 32 KiB, or kind unknown/not allowed
```

`project` is a simple grouping label, optional. `ttlSeconds` (max 604800) and
`maxAttempts` (max 10) override the queue's defaults.

### `POST /jobs/claim` — scope `worker`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/claim \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "workerId": "shop-worker", "leaseSeconds": 1200, "kinds": ["invoice-ocr"] }'
# 200 full job, with leaseToken + leaseUntil — keep the leaseToken
# 204 queue empty — poll again later (30 s is a good pace)
# 403 if none of the requested kinds is allowed for the key
```

`kinds` omitted = all the key's queues. Fair selection across queues: the oldest job of
each eligible queue, then a random pick — a big queue does not starve the small ones.
Paused queues (`enabled = false`) do not distribute.

### `POST /jobs/{id}/heartbeat` — scope `worker`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/42/heartbeat \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "leaseToken": "'$LEASE_TOKEN'", "leaseSeconds": 1200 }'
# 200 {"id":42,"leaseUntil":"…"} — lease extended
# 409 lease lost: abandon the job, another worker may have taken it over
```

### `POST /jobs/{id}/complete` — scope `worker`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/42/complete \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "leaseToken": "'$LEASE_TOKEN'", "outcome": "success",
        "result": { "report": "…", "costUsd": 0.42, "durationMs": 91000 } }'
# 200 {"id":42,"status":"done",…}   computed final status: done, pending (retry) or failed
# 409 stale leaseToken — a zombie worker cannot overwrite someone else's result
# 400 if result > 256 KiB (store large deliverables elsewhere, pass a reference)
```

`outcome: "failure"` requires `error`; the job goes back to `pending` if attempts
remain, otherwise `failed`. Replaying the same complete with the same token returns
`200` without rewriting.

### `GET /jobs/{id}` and `GET /jobs/by-key/{idempotencyKey}` — scope `producer`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/42 -H "Authorization: Bearer $PRODUCER_KEY"
curl -s https://hopper.exemple.ch/api/v1/jobs/by-key/cron:2026-08-17T03:00 \
  -H "Authorization: Bearer $PRODUCER_KEY"
# 200 state + result + lastError (never the leaseToken); 404 if the kind is not in
# the key's allowed_kinds — never 403, no enumeration
```

### Administration — scope `admin`

```bash
curl -s 'https://hopper.exemple.ch/api/v1/jobs?status=failed&kind=invoice-ocr&q=timeout&page=1' \
  -H "Authorization: Bearer $ADMIN_KEY"                    # paginated list (50/page)
curl -s https://hopper.exemple.ch/api/v1/jobs/42 -H "Authorization: Bearer $ADMIN_KEY"
                                                           # detail + job_events timeline
curl -s -X POST https://hopper.exemple.ch/api/v1/jobs/42/requeue -H "Authorization: Bearer $ADMIN_KEY"
curl -s -X POST https://hopper.exemple.ch/api/v1/jobs/42/cancel  -H "Authorization: Bearer $ADMIN_KEY"
curl -s https://hopper.exemple.ch/api/v1/stats -H "Authorization: Bearer $ADMIN_KEY"
# stats: count per status, oldest pending age, 24 h throughput — if the oldest pending
# age climbs, the worker is dead or saturated; that is THE metric to watch
```

### Health

```bash
curl -s https://hopper.exemple.ch/healthz   # alive, without touching the database
curl -s https://hopper.exemple.ch/readyz    # checks the PostgreSQL connection
```

## Full cycle (end-to-end example)

```bash
H=https://hopper.exemple.ch/api/v1
# 1. the producer enqueues
JOB=$(curl -s $H/jobs -H "Authorization: Bearer $PRODUCER_KEY" -H 'Content-Type: application/json' \
  -d '{"idempotencyKey":"demo:1","kind":"invoice-ocr","payload":{"ref":"doc-123"}}')
ID=$(echo $JOB | jq -r .id)

# 2. the worker (behind NAT) claims
CLAIM=$(curl -s $H/jobs/claim -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"workerId":"w1","leaseSeconds":1200}')
TOKEN=$(echo $CLAIM | jq -r .leaseToken)

# 3. while working, it extends the lease
curl -s $H/jobs/$ID/heartbeat -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"leaseToken":"'$TOKEN'"}'

# 4. it returns the result
curl -s $H/jobs/$ID/complete -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"leaseToken":"'$TOKEN'","outcome":"success","result":{"report":"ok"}}'

# 5. the producer re-reads whenever it wants
curl -s $H/jobs/by-key/demo:1 -H "Authorization: Bearer $PRODUCER_KEY"
```

## Deployment (Traefik + Docker)

The container publishes **no port**; Traefik reaches it through the `traefik-public`
network, PostgreSQL lives on `hopper-internal` and is never exposed. TLS is terminated
by Traefik — the service enables neither HTTPS nor HSTS and trusts
`X-Forwarded-For`/`-Proto` (`KnownIPNetworks`/`KnownProxies` cleared: Traefik's IP
changes on every recreation).

```bash
docker network create traefik-public        # if Traefik has not created it already
cp .env.example .env                        # HOPPER_DB_PASSWORD, HOPPER_PUBLIC_HOST, IP allowlist
docker compose -f compose.yaml up -d --build
```

**Always `-f compose.yaml` in production**: without it, `compose.override.yaml` (dev)
would publish the port and mount the code.

`/admin` is additionally IP-restricted at the Traefik level
(`HOPPER_ADMIN_IP_ALLOWLIST`) — an extra defence, the admin key is still required
behind it. Dynamic connection: plan a wide range rather than disabling the protection.

The API is public on the internet: Kestrel body limits (64 KiB, 512 KiB on
`/complete`) doubled by Traefik buffering, rate limiting per API key (authenticated)
and per IP (unauthenticated), neutral 404s, no `Server` header, no CORS.

### PostgreSQL — pinned version

The image is **`postgres:17`, never `latest`**: an image that moves to the next major
refuses to start on an existing data directory and forces a `pg_upgrade` or a
dump/restore cycle. To change major version: back up (`ops/backup.sh`), bump the
version in `compose.yaml`, restore (`ops/restore.sh`).

### Clean shutdown

`stop_grace_period: 30s` in compose > ASP.NET's `ShutdownTimeout` (20 s): the in-flight
HTTP request finishes on `SIGTERM`.

## Backup and restore

Daily compressed `pg_dump` (custom format), retention 7 daily + 4 weekly. Host cron:

```cron
0 3 * * *  cd /opt/hopper-jobqueue && ./ops/backup.sh /var/backups/hopper-jobqueue >> /var/log/hopper-backup.log 2>&1
```

Restore (procedure **performed and verified**: volume destroyed then restored
identically — jobs, events, keys — during setup):

```bash
./ops/restore.sh /var/backups/hopper-jobqueue/daily/hopper_YYYYMMDD_HHMMSS.dump
# 1. stops the API (no connections during the operation)
# 2. pg_restore --create --clean: the hopper database is recreated from the dump
# 3. restarts the API; check /readyz then the dashboard
```

A backup that was never restored is not a backup: redo this drill after any PostgreSQL
major version change.

## Development

- `docker compose up -d`: PostgreSQL + API under `dotnet watch` on
  `http://localhost:8080` (hot reload of the mounted code).
- Dashboard in dev: use `http://localhost:8080/admin` (the session cookie is `Secure`;
  browsers accept it on `localhost`, not on a bare IP).
- `dotnet test`: the brief's 10 scenarios (§9) + extras, on a real database.
  Testcontainers needs the Docker daemon and pulls `postgres:17`.
