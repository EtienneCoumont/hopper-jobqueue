---
title: Getting started
---

# Getting started

[← Back to index](index.md)

## Requirements

Docker with Compose v2 — that is all. The image is prebuilt by CI: nothing is compiled,
on your machine or on the server. Exposing the service to the internet additionally
requires a TLS-terminating reverse proxy in front; two deployment variants ship in
`deploy/`, one wired for [Traefik](https://traefik.io/) through container labels, one
publishing a local port for any other proxy (see [Deployment](deployment.md)).

Working *on* the code is the one case that needs more — .NET SDK 10.0, see
[Development](development.md).

No outbound network access is required at runtime: the service never calls anything.

## First run

Two `curl`s and one `docker compose up -d` start PostgreSQL and the API from the
prebuilt image. The files are identical on a laptop and on a server, so everything below
applies to both — the steps are in [Deployment](deployment.md). With a checkout and the
.NET SDK, `dotnet watch` gives you the same service with hot reload
([Development](development.md)).

## Bootstrap key

On first start, if the key table is empty, the service creates an `admin` key and
writes it **once** to the logs — straight to your console under `dotnet watch`, or:

```bash
docker compose logs hopper | grep -o 'hjq_admin_[A-Za-z0-9]*'
```

Alternatively, set the `HOPPER_BOOTSTRAP_ADMIN_KEY` environment variable
(`hjq_admin_{32 base62 chars}`) before the first start.

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

Next: the [API reference](api.md).
