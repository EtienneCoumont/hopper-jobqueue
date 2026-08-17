# hopper-jobqueue — bearings for future sessions

HTTP job-queue service: arbitrary producers enqueue, workers behind NAT come and fetch
(outbound polling only), an admin dashboard controls. This file carries the binding
invariants and decisions — read it before any evolution.

## Architecture

- `src/HopperJobQueue.Api` — single project: minimal API (`/api/v1`), Razor Pages
  (`/admin`), background task. No layers, no ORM: Dapper + explicit SQL in
  `Jobs/JobStore.cs` (jobs) and `Auth/ApiKeyStore.cs` (keys).
- `Migrations/*.sql` — numbered embedded scripts, applied by DbUp at startup under
  `pg_advisory_lock` (failed migration = non-zero exit, no starting on an inconsistent
  database). DbUp journal: `jobqueue.schemaversions`.
- `Maintenance/SweeperService.cs` — every 60 s, one transaction: exceeded TTLs →
  `expired`; expired leases out of attempts → `failed` (`last_error = "lease expired,
  attempts exhausted"`); purge of terminal jobs beyond `job_kinds.retention_days`;
  flush of the `last_used_at` buffer. `RunOnceAsync` is public for tests.
- `Program.cs` — pipeline order matters: ForwardedHeaders → ExceptionHandler →
  /complete body limit → /admin security headers → API-key auth → rate limiter
  (partition by key, else by IP) → scope enforcement (endpoint metadata) → static
  files → cookie auth → antiforgery → endpoints.
- Tests: `tests/HopperJobQueue.Tests`, one sequential xUnit collection, one shared
  PostgreSQL 17 container (Testcontainers) + `WebApplicationFactory`, tables reset per
  test. The 10 originally mandated scenarios are there, named `TestN_…`.
- Docs: `README.md` is the public landing page (overview, name pun, screenshots,
  quickstart) and keeps no procedure of its own — every detail (env vars, one curl per
  endpoint, full cycle, restore procedure, the whole dev loop in `docs/development.md`)
  lives in `docs/` — a GitHub Pages site
  (Jekyll Cayman, `docs/_config.yml`; enable via Settings → Pages → branch + `/docs`).
  Screenshots and the two name illustrations are in `docs/images/`.
- Repo layout rule: the **root** holds what you run from a checkout (`Dockerfile`,
  `compose.yaml`, the solution), **`deploy/`** what you copy onto a server. Don't move
  either into a `docker/` folder: `docker build .` and `docker compose up -d` both rely
  on root discovery (Compose walks *up* from the cwd, never down), so it would add a
  `-f`/`-f`-style flag to every documented command.
- Docker: root `compose.yaml` is DEV ONLY and now db-only — PostgreSQL on
  localhost:5432 with dev credentials, the API runs on the host via `dotnet watch`.
  The former `try` profile is gone: `deploy/standalone/` covers "run the real image
  locally" and is what the README quickstart uses.
- `deploy/` = production artifacts, one folder per reverse-proxy variant, each a
  self-contained `compose.yaml` + `.env.example` copied to a server directory (no
  checkout, no build, bare `docker compose` commands): `deploy/traefik/` (labels, no
  published port) and `deploy/standalone/` (published on `${HOPPER_BIND_ADDRESS:-127.0.0.1}:8080`,
  bring your own proxy). `deploy/maintenance/` holds `backup.sh` / `restore.sh`, which
  cd to their own directory and are copied next to the chosen compose file. There is
  deliberately no `deploy/README.md` — it duplicated `docs/deployment.md`. Images come from
  `ghcr.io/etiennecoumont/hopper-jobqueue`, published by `.github/workflows/docker.yml`
  (tests gate the publish; tags: `latest` on main, `X.Y.Z` on `v*` tags, immutable
  `sha-<commit>`). There is no compose.override.yaml and no wrapper — keep it that way.

## Invariants (covered by tests, do not break)

- `for update skip locked` in the claim: **mandatory**, it is what prevents two workers
  from getting the same job. The eligibility predicates are repeated in the locking
  select (EvalPlanQual re-check under READ COMMITTED); the handler loops as long as the
  statement returns zero rows while eligible jobs remain.
- Fairness across queues: oldest job of **each** eligible queue, then a random pick.
  Never a global `order by created_at`.
- `attempts` increments **at claim**, not at complete (poison message protection).
- `expires_at` exceeded ⇒ never distributed, even `pending`.
- `done` and `cancelled` are terminal; admin `requeue` is possible from
  `failed`/`expired`/`cancelled`, **never** from `done`.
- Every transition writes `job_events` **in the same transaction** as the update.
- Enqueue idempotency in the database (`on conflict do nothing` then re-read) — never a
  prior select. Replayed enqueue = `200 created:false`, not `409`.
- `complete`/`heartbeat` guarded by `leaseToken`; replaying an identical complete =
  `200` without rewriting; stale token = `409` (a zombie never overwrites someone
  else's work).
- The `leaseToken` only ever appears in the claim response, never in reads.
- `GET /jobs/{id}` outside `allowed_kinds` ⇒ `404`, never `403` (no enumeration).
- The service is agnostic: no mention of any particular producer (n8n, mail…) in the
  code, types, columns or errors. `payload`/`result` are opaque.

## Accepted decisions (documented, do not "fix" without thinking)

- **Stored key prefix: 16 characters, not the brief's 12.** `hjq_producer` is exactly
  12 characters: two producer keys would collide on `prefix unique`.
- **Claim: application-level loop around the brief's single statement.** Under
  contention, `skip locked` + one-row-per-queue candidates would return lying 204s
  (test 1 requires "exactly 5" successes). The statement remains the only thing that
  locks and distributes.
- **Fairness test bounded at 20 claims instead of the brief's "fewer than 10".** The
  mandated 50/50 random pick itself makes "< 10" flaky at ~5%; 20 keeps the
  demonstration (without fairness it would take ~200) without flakiness (~0.02%).
- **Antiforgery cookie set to `SameAsRequest`** (the session cookie itself is properly
  `Secure`/`HttpOnly`/`Strict`): `Always` breaks form rendering over direct HTTP (dev).
  Behind Traefik, X-Forwarded-Proto=https ⇒ Secure in production.
- **Kind management on the dashboard** (`/admin/kinds`): needed for "kind declared
  before use" + pause control; deliberately absent from the API surface.
- **Docker architecture diverges from the brief's §13** (owner's decision, 2026-08):
  the in-container `dotnet watch` dev override is gone — dev is host-run `dotnet
  watch` + a db-only compose; production is a copied deploy artifact pulling the
  CI-built GHCR image instead of `docker compose build` on the server.
- **Deployment variants are duplicated on purpose, never factored** (2026-08-17). The
  install model is "curl one compose file onto the server", so no variant may
  `include:`/`extends:` a shared base — the repeated `hopper-db` block is the price.
  A new variant (caddy, nginx…) = a new self-contained folder keeping the same project
  name, service names and `hopper-pgdata` volume, so `deploy/maintenance/` and the docs
  keep applying. `docs/deployment.md` is the single place documenting the procedure —
  don't re-document install steps in `deploy/`.
- **`deploy/standalone` publishes on loopback by default.** Without a TLS proxy in
  front, `/admin` is unusable off `localhost` (`Secure` cookie), nothing filters
  `/admin` by IP, and `KnownProxies`/`KnownIPNetworks` are cleared — a directly
  reachable port lets anyone forge `X-Forwarded-For`/`-Proto`. Do not widen that
  default or drop the warning at the top of the file.

## Commands

```bash
dotnet build                      # zero warnings required (TreatWarningsAsErrors)
dotnet test                       # Docker required (Testcontainers, postgres:17 image)
docker compose up -d              # dev: PostgreSQL only (localhost:5432, password hopper-dev)
export HOPPER_DB_CONNECTIONSTRING="Host=127.0.0.1;Port=5432;Database=hopper;Username=hopper;Password=hopper-dev"
dotnet watch --project src/HopperJobQueue.Api run --urls http://localhost:8080
cd <dir> && docker compose up -d     # run the real image: deploy/standalone/ files, no SDK
deploy/maintenance/backup.sh <dir>    # prod: custom-format pg_dump + 7d/4w retention
deploy/maintenance/restore.sh <dump>  # prod: stops the API, recreates the db, restarts
```

Config via env only, `HOPPER_` prefix (see README / `deploy/*/.env.example`; only
`DB_CONNECTIONSTRING`, `BOOTSTRAP_ADMIN_KEY`, `SWEEP_INTERVAL_SECONDS` and `LOG_LEVEL`
reach the app — the rest are consumed by compose). No new NuGet
dependency without validation. UTC timestamps everywhere
(`timestamptz` ↔ `DateTimeOffset`, Dapper handler in `Infrastructure/DapperConfig.cs`).
