# hopper-jobqueue — bearings for future sessions

HTTP job-queue service: arbitrary producers enqueue, workers behind NAT come and fetch
(outbound polling only), an admin dashboard controls. The full brief is in `BRIEF.md` —
read it before any evolution; everything is settled there (§14).

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
  test. The 10 scenarios of the brief's §9 are there, named `TestN_…`.

## Invariants (§4 of the brief — covered by tests, do not break)

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

## Accepted deviations vs the brief (documented, do not "fix" without thinking)

- **Stored key prefix: 16 characters, not 12.** `hjq_producer` is exactly
  12 characters: two producer keys would collide on `prefix unique`.
- **Claim: application-level loop around the brief's single statement.** Under
  contention, `skip locked` + one-row-per-queue candidates would return lying 204s
  (test 1 "exactly 5" of §9). The statement remains the only thing that locks and
  distributes.
- **Fairness test bounded at 20 claims instead of "fewer than 10".** The brief's own
  50/50 random pick makes "< 10" flaky at ~5%; 20 keeps the demonstration (without
  fairness it would take ~200) without flakiness (~0.02%).
- **Antiforgery cookie set to `SameAsRequest`** (the session cookie itself is properly
  `Secure`/`HttpOnly`/`Strict`): `Always` breaks form rendering over direct HTTP (dev).
  Behind Traefik, X-Forwarded-Proto=https ⇒ Secure in production.
- **Kind management on the dashboard** (`/admin/kinds`): needed for "kind declared
  before use" + pause control (§4); deliberately absent from the API (§5 unchanged).

## Commands

```bash
dotnet build                      # zero warnings required (TreatWarningsAsErrors)
dotnet test                       # Docker required (Testcontainers, postgres:17 image)
docker compose up -d              # dev: port 8080 published, dotnet watch (override)
docker compose -f compose.yaml up -d --build    # prod: Traefik, no published port
./ops/backup.sh <dir>             # custom-format pg_dump + 7d/4w retention
./ops/restore.sh <dump>           # stops the API, recreates the db, restarts
```

Config via env only, `HOPPER_` prefix (see README / `.env.example`). No new NuGet
dependency without validation (§3). UTC timestamps everywhere
(`timestamptz` ↔ `DateTimeOffset`, Dapper handler in `Infrastructure/DapperConfig.cs`).
