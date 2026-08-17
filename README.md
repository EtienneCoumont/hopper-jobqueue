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

Stack: .NET 10 minimal API + Razor Pages, PostgreSQL 17, Npgsql + Dapper (explicit SQL,
no ORM), DbUp migrations, Serilog JSON logs, xUnit + Testcontainers.

## Requirements

**Run it:** Docker with Compose v2. Nothing else — the image comes prebuilt from GHCR,
nothing is compiled on your machine or on the server.

**Expose it to the internet:** the same, plus a TLS-terminating reverse proxy in front.
A ready-made [Traefik](https://traefik.io/) setup ships in
[`deploy/traefik/`](deploy/traefik/); [`deploy/standalone/`](deploy/standalone/) suits
any other proxy.

**Develop:** .NET SDK 10.0 and Docker (the dev database and the integration tests run
in containers).

## Quickstart

### Run it on your machine

Two files and one command. No checkout, no build — the image is pulled from GHCR:

```bash
mkdir hopper-jobqueue && cd hopper-jobqueue
BASE=https://raw.githubusercontent.com/EtienneCoumont/hopper-jobqueue/main/deploy/standalone
curl -fsSO $BASE/compose.yaml
curl -fsS  $BASE/.env.example -o .env

docker compose up -d                                     # PostgreSQL + the API
docker compose logs hopper | grep -o 'hjq_admin_[A-Za-z0-9]*'   # the bootstrap admin key
```

The service answers on `http://localhost:8080`. Sign in on
[`/admin`](http://localhost:8080/admin) with that key — use `localhost`, not an IP: the
session cookie is `Secure`, and browsers only accept it there over plain HTTP. The key
is logged **once**, on the first start with an empty database; set
`HOPPER_BOOTSTRAP_ADMIN_KEY` in the `.env` beforehand if you would rather choose it.

> **Putting it online is not the same command.** Those two files do deploy to a server,
> but the `.env` still carries `change-me` as the database password, and the API must
> never face the internet directly: `/admin` needs HTTPS to work at all, and the
> application trusts `X-Forwarded-*` headers, so a TLS-terminating reverse proxy in
> front is mandatory. [`deploy/traefik/`](deploy/traefik/) ships that wired up;
> [docs/deployment.md](docs/deployment.md) covers both variants, the environment
> variables, hardening and backups.

### First steps

Declare a kind (`/admin/kinds`), create real producer and worker keys (`/admin/keys`),
then revoke the bootstrap key. The whole life of a job is four calls:

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

### Working on the code

The repo's `compose.yaml` starts PostgreSQL alone; the API runs on your machine under
`dotnet watch`, with hot reload and a debugger. Clone, build, test and conventions:
[docs/development.md](docs/development.md).

## Documentation

| Page | Content |
|---|---|
| [Getting started](docs/getting-started.md) | Requirements, first run, first key, first job |
| [API reference](docs/api.md) | Every endpoint with `curl` examples, auth, errors, limits |
| [Architecture](docs/architecture.md) | Data model, state machine, lease and fairness mechanics |
| [Deployment](docs/deployment.md) | Docker, reverse proxy variants, environment variables, hardening |
| [Operations](docs/operations.md) | Dashboard tour, keys, monitoring, backup and restore |
| [Development](docs/development.md) | Dev loop with hot reload, building, running the test suite |

[`CLAUDE.md`](CLAUDE.md) carries the design invariants and working notes for AI
coding sessions.

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
