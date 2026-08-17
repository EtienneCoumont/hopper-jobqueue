---
title: Getting started
---

# Getting started

[← Back to index](index.md)

## Requirements

To **run** the service:

- Docker with Docker Compose v2.24+ (the dev override uses the `!reset` YAML tag)
- For production: a [Traefik](https://traefik.io/) instance attached to a
  `traefik-public` Docker network (any TLS-terminating reverse proxy works, but the
  shipped labels target Traefik)

To **develop**:

- .NET SDK 10.0
- Docker (the integration tests spin up a real PostgreSQL 17 via Testcontainers)

No outbound network access is required at runtime: the service never calls anything.

## First run (development)

```bash
git clone https://github.com/EtienneCoumont/hopper-jobqueue.git
cd hopper-jobqueue
docker network create traefik-public   # once
cp .env.example .env                   # set HOPPER_DB_PASSWORD
docker compose up -d                   # publishes :8080, runs dotnet watch
curl http://localhost:8080/healthz     # -> ok
```

`compose.override.yaml` is picked up automatically in dev: it publishes the port and
hot-mounts the source. In production you deploy with `-f compose.yaml` only — no port
published, Traefik reaches the container over the Docker network.

## Bootstrap key

On first start, if the key table is empty, the service creates an `admin` key and
writes it **once** to the logs:

```bash
docker compose logs hopper | grep "bootstrap admin key"
```

Alternatively, set `HOPPER_BOOTSTRAP_ADMIN_KEY=hjq_admin_{32 base62 chars}` in `.env`
before the first start.

Sign in at [http://localhost:8080/admin](http://localhost:8080/admin) with that key:

![Sign-in page](images/dashboard-login.png)

Then:

1. **Declare a kind** (`/admin/kinds`) — a kind is a queue plus its defaults (TTL, max
   attempts, lease duration, retention). Jobs can only be enqueued on declared kinds.
2. **Create real keys** (`/admin/keys`) — one `producer` key per producer, one `worker`
   key per worker, each restricted to its allowed kinds. The clear-text key is shown
   exactly once.
3. **Revoke the bootstrap key** — it went through the container logs.

Use `localhost`, not a bare IP: the session cookie is `Secure`, which browsers accept
on `localhost` only.

## First job, end to end

```bash
H=http://localhost:8080/api/v1

# 1. the producer enqueues (idempotent: replaying the same key is a 200, not an error)
JOB=$(curl -s $H/jobs -H "Authorization: Bearer $PRODUCER_KEY" -H 'Content-Type: application/json' \
  -d '{"idempotencyKey":"demo:1","kind":"invoice-ocr","payload":{"ref":"doc-123"}}')
ID=$(echo $JOB | jq -r .id)

# 2. the worker claims (from anywhere — only outbound HTTPS needed)
CLAIM=$(curl -s $H/jobs/claim -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"workerId":"w1","leaseSeconds":1200}')
TOKEN=$(echo $CLAIM | jq -r .leaseToken)

# 3. while working, it extends the lease
curl -s $H/jobs/$ID/heartbeat -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"leaseToken":"'$TOKEN'"}'

# 4. it returns the result
curl -s $H/jobs/$ID/complete -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"leaseToken":"'$TOKEN'","outcome":"success","result":{"report":"ok"}}'

# 5. the producer reads it back whenever it wants
curl -s $H/jobs/by-key/demo:1 -H "Authorization: Bearer $PRODUCER_KEY"
```

A typical worker is a loop: `claim` → work (+ `heartbeat`) → `complete` → sleep 30 s
when the queue answers `204`.

## Running the tests

```bash
dotnet test
```

Docker is required: the suite starts a real PostgreSQL 17 (Testcontainers) and runs the
whole application against it — including 20-way concurrent claim races, lease-expiry
takeovers and fairness across queues.

Next: the [API reference](api.md).
