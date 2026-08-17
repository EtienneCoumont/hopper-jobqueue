---
title: hopper-jobqueue
---

# hopper-jobqueue

A small, self-contained HTTP job queue between **arbitrary producers** (scripts,
applications, webhooks, plain `curl`) and **workers behind NAT** that can only make
outbound HTTPS calls. The service stores and distributes jobs with **lease** semantics;
it executes nothing and never calls anything external — producers read results back by
polling.

![Dashboard overview](images/dashboard-overview.png)

## Why it exists

The founding constraint: the worker sits behind NAT, with no inbound socket, no tunnel
and no direct database access. Everything therefore happens as outbound HTTPS from the
worker's side — it *comes to fetch* work. That rules out push, WebSockets and callbacks,
and it makes the queue the single meeting point between producers and workers.

A job that is claimed and never returned becomes available again on its own (the lease
expires). A worker that crashes mid-job consumes an attempt — after `max_attempts` the
job is parked as `failed` instead of poisoning the queue forever.

## Documentation

| Page | Content |
|---|---|
| [Getting started](getting-started.md) | Requirements, first run, first key, first job |
| [API reference](api.md) | Every endpoint with `curl` examples, auth, errors, limits |
| [Architecture](architecture.md) | Data model, state machine, lease and fairness mechanics |
| [Deployment](deployment.md) | Docker, reverse proxy variants, environment variables, hardening |
| [Operations](operations.md) | Dashboard tour, key management, monitoring, backup and restore |
| [Development](development.md) | Dev loop with hot reload, building, running the test suite |

## Design in one paragraph

One ASP.NET Core (.NET 10) container and one PostgreSQL 17 container. No ORM — Dapper
and explicit SQL; the claim is a single `for update skip locked` statement with fair
selection across queues. Numbered SQL migrations applied by DbUp at startup. A
server-rendered Razor Pages dashboard (`/admin`), one hand-written CSS file, zero
front-end build. Three API-key scopes (`producer`, `worker`, `admin`), SHA-256 key
storage, two-tier rate limiting. Every state transition is journaled to an audit table
in the same transaction.

The project source lives in the
[GitHub repository](https://github.com/EtienneCoumont/hopper-jobqueue).
