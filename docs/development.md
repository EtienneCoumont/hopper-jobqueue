---
title: Development
---

# Development

[← Back to index](index.md)

Working *on* the code. To simply run the service, no SDK and no checkout are needed —
see [Deployment](deployment.md).

## Requirements

.NET SDK 10.0 and Docker. The dev database runs in a container, and the integration
tests spin up a real PostgreSQL 17 through Testcontainers.

## The dev loop

The repository's root `compose.yaml` starts PostgreSQL **only**: the API runs on your
machine, with hot reload and a working debugger.

```bash
git clone https://github.com/EtienneCoumont/hopper-jobqueue.git
cd hopper-jobqueue
docker compose up -d      # PostgreSQL on localhost:5432, dev credentials
export HOPPER_DB_CONNECTIONSTRING="Host=127.0.0.1;Port=5432;Database=hopper;Username=hopper;Password=hopper-dev"
dotnet watch --project src/HopperJobQueue.Api run --urls http://localhost:8080
curl http://localhost:8080/healthz     # -> ok
```

Migrations are applied at startup, so the schema builds itself on the first run against
the empty container. The bootstrap admin key goes straight to your console — sign in
at [http://localhost:8080/admin](http://localhost:8080/admin) with it, on `localhost`
rather than a bare IP: the session cookie is `Secure`, which browsers only accept there
over plain HTTP.

`docker compose down -v` throws the database away and gives you a clean first start,
new bootstrap key included.

## Building

```bash
dotnet build
```

Warnings are errors (`TreatWarningsAsErrors`): a build that is not clean does not
compile.

## Running the tests

```bash
dotnet test
```

16 integration tests, and Docker is required: the suite starts a real PostgreSQL 17
(Testcontainers) and runs the whole application against it — including 20-way concurrent
claim races, lease-expiry takeovers and fairness across queues. Your dev database is
left alone, the tests get their own container.

## Continuous integration

Every push to `main` runs that same suite, and the Docker image is published to
[GHCR](https://github.com/EtienneCoumont/hopper-jobqueue/pkgs/container/hopper-jobqueue)
only if it passes — a red suite means no new image. Tagging (`latest`, `X.Y.Z`,
`sha-<commit>`) is covered in [Deployment](deployment.md).

Continue with [Architecture](architecture.md).
