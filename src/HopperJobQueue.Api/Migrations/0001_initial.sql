create schema if not exists jobqueue;

create table jobqueue.job_kinds (
  name                 text        primary key,
  description          text,
  enabled              boolean     not null default true,
  default_ttl_seconds  int         not null default 86400,
  default_max_attempts int         not null default 3,
  default_lease_seconds int        not null default 1200,
  retention_days       int         not null default 90,
  created_at           timestamptz not null default now()
);

create table jobqueue.jobs (
  id             bigserial     primary key,
  idempotency_key text         not null unique,
  kind           text          not null references jobqueue.job_kinds(name),
  project        text,
  payload        jsonb         not null,
  status         text          not null default 'pending',
  attempts       int           not null default 0,
  max_attempts   int           not null default 3,
  lease_token    uuid,
  lease_until    timestamptz,
  worker_id      text,
  created_at     timestamptz   not null default now(),
  expires_at     timestamptz   not null,
  finished_at    timestamptz,
  result         jsonb,
  last_error     text,
  constraint jobs_status_check check (status in
    ('pending','leased','done','failed','expired','cancelled'))
);

create index jobs_claim_idx on jobqueue.jobs (status, created_at)
  where status in ('pending','leased');

create table jobqueue.api_keys (
  id          bigserial   primary key,
  name        text        not null,
  prefix      text        not null unique,
  key_hash    bytea       not null,
  scope       text        not null,
  allowed_kinds text[]    not null default '{}',
  created_at  timestamptz not null default now(),
  last_used_at timestamptz,
  revoked_at  timestamptz,
  constraint api_keys_scope_check check (scope in ('producer','worker','admin'))
);

create table jobqueue.job_events (
  id         bigserial   primary key,
  job_id     bigint      not null references jobqueue.jobs(id) on delete cascade,
  at         timestamptz not null default now(),
  from_status text,
  to_status  text        not null,
  actor      text        not null,
  note       text
);

create index job_events_job_idx on jobqueue.job_events (job_id, at);
