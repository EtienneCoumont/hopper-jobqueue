# hopper-jobqueue

[![docker](https://github.com/EtienneCoumont/hopper-jobqueue/actions/workflows/docker.yml/badge.svg)](https://github.com/EtienneCoumont/hopper-jobqueue/actions/workflows/docker.yml)
[![License: WTFPL](https://img.shields.io/badge/License-WTFPL-brightgreen.svg)](LICENSE)

**A small, self-contained HTTP job queue for workers behind NAT.**

Arbitrary producers — scripts, applications, webhooks, plain `curl` — drop jobs in.
Workers that can only make *outbound* HTTPS calls come and fetch them, with lease
semantics: a job that is claimed and never returned becomes available again on its own.
The service stores and distributes; it executes nothing and never calls anything
external. Producers poll their results back.

Documentation: **[etiennecoumont.github.io/hopper-jobqueue](https://etiennecoumont.github.io/hopper-jobqueue/)**
(also browsable in [`docs/`](docs/index.md)).

## Screenshots

The admin dashboard — server-rendered Razor Pages, one hand-written CSS file, no
front-end build. Click any screenshot for full size:

<p align="center">
  <a href="docs/images/dashboard-overview.png"><img src="docs/images/dashboard-overview.png" width="49%" alt="Overview: counters per status, oldest pending age, per-worker activity"></a>
  <a href="docs/images/dashboard-jobs.png"><img src="docs/images/dashboard-jobs.png" width="49%" alt="Job list with filters and inline requeue/cancel actions"></a>
</p>
<p align="center">
  <a href="docs/images/dashboard-job-detail.png"><img src="docs/images/dashboard-job-detail.png" width="49%" alt="Job detail: last error, payload, full audit timeline"></a>
  <a href="docs/images/dashboard-keys.png"><img src="docs/images/dashboard-keys.png" width="49%" alt="API keys: creation with allowed kinds, one-time display, revocation"></a>
</p>

More pages (kinds, sign-in, a successful job with its result) in the
[operations guide](docs/operations.md).

## Features

- **Idempotent enqueue** — producers retry blindly; replaying a submission is a `200`,
  never an error. Enforced in the database, not with a racy pre-check.
- **Leases with heartbeat** — a claimed job returns to the queue by itself if the
  worker vanishes; a zombie worker can never overwrite the work of the one that took
  over (lease tokens, `409`).
- **Poison-message protection** — attempts are counted at claim time; a job that keeps
  killing its worker ends up `failed`, not looping forever.
- **Fair multi-queue scheduling** — oldest job of each eligible queue, then a random
  pick; a queue that receives 500 jobs at once cannot starve the others. Concurrent
  claims are safe (`for update skip locked`) and exact: no double delivery, no lying
  "empty" responses.
- **Scoped API keys** — `producer` / `worker` / `admin`, one key per client, each
  restricted to its allowed queues; SHA-256 at rest, constant-time comparison,
  targeted revocation.
- **Pausable queues** — pause distribution without touching producers; TTLs, per-queue
  defaults and retention-based purge.
- **Full audit trail** — every state transition journaled in the same transaction,
  visible as a timeline in the dashboard.
- **Built for the public internet** — body-size caps, two-tier rate limiting, neutral
  errors, no enumeration oracles, no CORS.
- **Tested against a real database** — the concurrency suite (20-way claim races,
  lease takeovers, fairness) runs on PostgreSQL 17 via Testcontainers.

## Requirements

**Run (production):** Docker with Compose v2, and a [Traefik](https://traefik.io/)
instance for TLS termination and the `/admin` IP allowlist. Nothing else — the image
comes prebuilt from GHCR.

**Develop:** .NET SDK 10.0 and Docker (the dev database and the integration tests run
in containers).

## Quickstart

### Package installation

The deployment artifact is `deploy/compose.yaml` + a `.env`. Copy them into a
directory (`curl` from the repo, or `scp`) and run
`docker compose` commands from there:

```bash
mkdir hopper-jobqueue && cd hopper-jobqueue
BASE=https://raw.githubusercontent.com/EtienneCoumont/hopper-jobqueue/main/deploy
curl -fsSO $BASE/compose.yaml
curl -fsS  $BASE/.env.example -o .env
nano .env    # password, public host, admin IP allowlist — and, if your Traefik's
             # network/entrypoint/resolver aren't the defaults, the three HOPPER_TRAEFIK_* vars

docker network create traefik-public   # skip if your Traefik already provides the network
docker compose up -d                   # pulls ghcr.io/etiennecoumont/hopper-jobqueue
docker compose logs hopper | grep "bootstrap admin key"
```

Updating is two commands — `docker compose pull && docker compose up -d` — and
`HOPPER_IMAGE_TAG` in `.env` pins a version or rolls one back. Full server guide
(existing Traefik, hardening, backups): [docs/deployment.md](docs/deployment.md).

### First steps, dev or prod

Sign in on `/admin` with the bootstrap key, declare a kind (`/admin/kinds`), create
real producer and worker keys (`/admin/keys`), then revoke the bootstrap key. The
whole life of a job is four calls:

```bash
H=http://localhost:8080/api/v1    # or https://your-host/api/v1
curl -s $H/jobs -H "Authorization: Bearer $PRODUCER_KEY" -H 'Content-Type: application/json' \
  -d '{"idempotencyKey":"demo:1","kind":"invoice-ocr","payload":{"ref":"doc-123"}}'   # enqueue
curl -s $H/jobs/claim -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"workerId":"w1"}'                                                              # claim -> leaseToken
curl -s $H/jobs/1/complete -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"leaseToken":"…","outcome":"success","result":{"report":"ok"}}'                # complete
curl -s $H/jobs/by-key/demo:1 -H "Authorization: Bearer $PRODUCER_KEY"                # read back
```

### Development

The dev compose file starts PostgreSQL only; the API runs on your machine, with hot
reload and a working debugger:

```bash
git clone https://github.com/EtienneCoumont/hopper-jobqueue.git
cd hopper-jobqueue
docker compose up -d      # PostgreSQL on localhost:5432, dev credentials
export HOPPER_DB_CONNECTIONSTRING="Host=127.0.0.1;Port=5432;Database=hopper;Username=hopper;Password=hopper-dev"
dotnet watch --project src/HopperJobQueue.Api run --urls http://localhost:8080
```

The bootstrap **admin key** appears right in the console on first start. Use
`http://localhost:8080/admin` (`localhost`, not an IP: the session cookie is `Secure`
and browsers only accept it there over plain HTTP).

No .NET SDK at hand? Run the CI-published image instead — the full service, nothing
to build:

```bash
docker compose --profile try up -d          # database + API on http://localhost:8080
docker compose logs hopper | grep "bootstrap admin key"
```

## Documentation

| Page | Content |
|---|---|
| [Getting started](docs/getting-started.md) | Requirements, first run, first key, first job |
| [API reference](docs/api.md) | Every endpoint with `curl` examples, auth, errors, limits |
| [Architecture](docs/architecture.md) | Data model, state machine, lease and fairness mechanics |
| [Deployment](docs/deployment.md) | Docker, Traefik, environment variables, hardening |
| [Operations](docs/operations.md) | Dashboard tour, keys, monitoring, backup and restore |

[`CLAUDE.md`](CLAUDE.md) carries the design invariants and working notes for AI
coding sessions.

## Development

```bash
dotnet build    # zero-warning policy (TreatWarningsAsErrors)
dotnet test     # 16 integration tests against a real PostgreSQL 17 (Docker required)
```

Stack: .NET 10 minimal API + Razor Pages, PostgreSQL 17, Npgsql + Dapper (explicit
SQL, no ORM), DbUp migrations, Serilog JSON logs, xUnit + Testcontainers.

On every push to `main`, CI runs the full test suite and publishes the Docker image
to [GHCR](https://github.com/EtienneCoumont/hopper-jobqueue/pkgs/container/hopper-jobqueue)
(`latest`, plus immutable `sha-<commit>` tags; `v*` git tags publish versions).

## Why "hopper"?

The name is a double pun.

<table>
<tr>
<td width="50%" valign="top">
<a href="docs/images/hopper-industrial.svg"><img src="docs/images/hopper-industrial.svg" width="100%" alt="An industrial hopper: bulk in at the top, one job at a time out onto a conveyor"></a>
An <b>industrial hopper</b> takes bulk loads dumped in at the top and feeds them out of the bottom in a steady, measured stream. That is precisely this service: producers tip jobs in as they come; workers draw them out one at a time.
</td>
<td width="50%" valign="top">
<a href="docs/images/hopper-painter.svg"><img src="docs/images/hopper-painter.svg" width="100%" alt="A night scene in the mood of an Edward Hopper painting: a lone figure in a lit diner window"></a>
<b>Edward Hopper</b> painted lone figures waiting under artificial light in the middle of the night. Anyone who has watched a worker poll an empty queue at 3 a.m. knows the mood.
</td>
</tr>
</table>

*Both illustrations are original vector art drawn for this project and dedicated to the
public domain — no rights reserved.*

## License

[WTFPL](LICENSE) — Do What The Fuck You Want To Public License, version 2.

This work is free: you can redistribute it and/or modify it under the terms of the
WTFPL. It comes without any warranty, to the extent permitted by applicable law.
