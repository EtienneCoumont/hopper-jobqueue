---
title: Deployment
---

# Deployment

[← Back to index](index.md)

Target: one Docker host running Traefik. The service container publishes **no port** —
Traefik reaches it over the `traefik-public` Docker network. PostgreSQL lives on the
internal `hopper-internal` network and is never exposed anywhere.

## The deployment artifact

Production needs **no git checkout and no build**: everything the server runs is
described by `deploy/compose.yaml`, a self-contained file that pulls the CI-built
image from GHCR. Copy it into a deployment directory next to a filled-in `.env`
(plus, optionally, the backup/restore scripts), and every command is plain
`docker compose <cmd>` from that directory:

```
/opt/hopper-jobqueue/
├── compose.yaml    ← deploy/compose.yaml from the repo
├── .env            ← from deploy/.env.example
├── backup.sh       ← optional
└── restore.sh      ← optional
```

```bash
mkdir -p /opt/hopper-jobqueue && cd /opt/hopper-jobqueue
BASE=https://raw.githubusercontent.com/EtienneCoumont/hopper-jobqueue/main/deploy
curl -fsSO $BASE/compose.yaml
curl -fsS  $BASE/.env.example -o .env      # then fill in the variables below
curl -fsSO $BASE/backup.sh && curl -fsSO $BASE/restore.sh && chmod +x backup.sh restore.sh
docker network create traefik-public        # skip if your Traefik already provides one
docker compose up -d
```

The compose file pins the project name (`name: hopper-jobqueue`), so containers,
networks and the `hopper-pgdata` volume keep stable names wherever the directory
lives. The development compose file at the repo root plays no role in production —
there is no override mechanism to guard against.

## The image comes from GHCR

On every push to `main`, the
[docker workflow](https://github.com/EtienneCoumont/hopper-jobqueue/actions/workflows/docker.yml)
runs the full integration test suite, builds the multi-stage Dockerfile and publishes
`ghcr.io/etiennecoumont/hopper-jobqueue` with three kinds of tags:

| Tag | Meaning |
|---|---|
| `latest` | follows `main` (the default the compose file pulls) |
| `X.Y.Z` | published when a `vX.Y.Z` git tag is pushed |
| `sha-<commit>` | immutable, one per build — for pinning and rollbacks |

Pin a tag on the server with `HOPPER_IMAGE_TAG` in `.env`
(e.g. `HOPPER_IMAGE_TAG=sha-abc1234`), then `docker compose up -d`. Rolling back is
the same operation with the previous tag.

Updating a deployment:

```bash
cd /opt/hopper-jobqueue
docker compose pull
docker compose up -d
```

The copied `compose.yaml` itself changes rarely; when a release notes that it did,
re-`curl` it the same way as at install time.

**Package visibility**: the package is published by the workflow's `GITHUB_TOKEN`,
which links it to this repository — it inherits the repository's visibility and is
therefore **public from the first push**: anonymous `docker pull` works out of the
box, nothing to configure. (Only if you ever switch the package to private would the
server need `docker login ghcr.io -u <user>` with a `read:packages` personal access
token.)

To build the image without CI (air-gapped use):
`docker build -t ghcr.io/etiennecoumont/hopper-jobqueue:latest .` from a checkout —
the .NET SDK is only needed inside the build container.

## Plugging into an existing Traefik

If a Traefik already runs on the host, three names in its configuration must line up
with the compose labels — all three are overridable from `.env`:

| Variable | Default | Must match |
|---|---|---|
| `HOPPER_TRAEFIK_NETWORK` | `traefik-public` | the Docker network your Traefik watches |
| `HOPPER_TRAEFIK_ENTRYPOINT` | `websecure` | the HTTPS entrypoint name in its static config |
| `HOPPER_TRAEFIK_CERTRESOLVER` | `letsencrypt` | the ACME certificate resolver name |

Find the values with:

```bash
docker inspect <traefik-container> --format '{{json .NetworkSettings.Networks}}' | jq keys
docker inspect <traefik-container> --format '{{join .Args " "}}' | tr ' ' '\n' | grep -E 'entryPoints|certificatesresolvers' -i
```

Two ways to share the network:

- **Reuse Traefik's network** (recommended): set `HOPPER_TRAEFIK_NETWORK=<its-name>`
  in `.env` — done; the network is declared `external`, compose will not create it.
- **Keep `traefik-public`**: create it and attach your Traefik to it:

  ```bash
  docker network create traefik-public
  docker network connect traefik-public <traefik-container>
  ```

Finally, point the DNS record for `HOPPER_PUBLIC_HOST` at the host, and put your own
IP ranges in `HOPPER_ADMIN_IP_ALLOWLIST` — `/admin` answers `403` from anywhere else
(that check runs in Traefik, before the application).

## Environment variables

Configuration is environment-only (no config files, no secrets on disk). All variables
are prefixed `HOPPER_`; compose assembles the connection string for you.

| Variable | Purpose |
|---|---|
| `HOPPER_DB_PASSWORD` | PostgreSQL password (user `hopper`, database `hopper`), read from `.env` |
| `HOPPER_DB_CONNECTIONSTRING` | full Npgsql connection string (**required** — compose builds it from the password) |
| `HOPPER_PUBLIC_HOST` | public host for the Traefik routers, e.g. `hopper.example.com` |
| `HOPPER_ADMIN_IP_ALLOWLIST` | comma-separated IP ranges allowed on `/admin` |
| `HOPPER_BOOTSTRAP_ADMIN_KEY` | optional fixed bootstrap admin key (`hjq_admin_{32 base62}`) |
| `HOPPER_LOG_LEVEL` | `Verbose` … `Error`, default `Information` |
| `HOPPER_SWEEP_INTERVAL_SECONDS` | sweeper period, default `60` |
| `HOPPER_IMAGE_TAG` | image tag to pull from GHCR (`latest`, `X.Y.Z`, `sha-<commit>`), default `latest` |

## TLS and proxy headers

Traefik terminates TLS. The container listens on plain HTTP :8080 internally and the
application neither redirects to HTTPS nor sets HSTS — doing so behind a TLS-terminating
proxy causes redirect loops.

The application trusts `X-Forwarded-For` / `X-Forwarded-Proto` with the known-proxy
allowlists cleared. This is required in Docker: Traefik's bridge IP changes on every
recreation, and ASP.NET's default allowlist would silently drop the headers — breaking
`Secure` cookies and making the per-IP rate limiter count everyone as one client.

## Routers and the /admin allowlist

Two Traefik routers ship in the compose labels:

- `hopper-api` — `/api`, `/healthz`, `/readyz`
- `hopper-admin` — `/admin` (+ the stylesheet), wrapped in an `ipallowlist` middleware
  fed by `HOPPER_ADMIN_IP_ALLOWLIST`

The IP allowlist is an *additional* defence: the admin key sign-in remains required
behind it. If your home connection has a dynamic IP, allow a wide range rather than
disabling the middleware. A buffering middleware caps request bodies at 1 MiB upstream
of the application's own 64/512 KiB limits.

## PostgreSQL — pinned major version

The image is `postgres:17`, **never** `latest`: a major-version image refuses to start
on an existing data directory and forces a `pg_upgrade` or dump/restore cycle. To move
to a new major: back up, bump the tag, restore (see
[Operations — backup and restore](operations.md#backup-and-restore)).

Data lives in the named volume `hopper-pgdata`. No bind mounts, no published port.

## Shutdown and health

- `stop_grace_period: 30s` in compose exceeds ASP.NET's 20 s `ShutdownTimeout`, so the
  in-flight HTTP request finishes on `SIGTERM`.
- The image has a `HEALTHCHECK` on `/healthz`; compose starts the API only after
  PostgreSQL reports healthy, and Traefik only routes to a healthy container.
- Migrations run at startup before traffic is accepted, serialized across instances by
  a `pg_advisory_lock`.

## Public exposure checklist

The API is meant to face the public internet (producers are arbitrary and remote;
workers are behind NAT). What ships:

- Kestrel body caps: 64 KiB everywhere, 512 KiB on `/complete` — a multi-gigabyte body
  is rejected before being read, and Traefik buffering caps it again upstream.
- Rate limiting: 300/min per API key (sliding window), 60/min per client IP without a
  valid key. `429` beyond.
- Neutral errors: `problem+json` without stack traces, bare 404 for unknown paths, no
  `Server` header, no framework version, no CORS policy at all.
- Authentication failures are logged with source IP and attempted key prefix at
  `Information` level — scanner noise on a public IP must not flood alert channels.
- Security headers (CSP, `X-Content-Type-Options`, `Referrer-Policy`, frame denial) on
  `/admin`, the only surface with a cookie session.

Continue with [Operations](operations.md).
