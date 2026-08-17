#!/usr/bin/env bash
# Restore a backup produced by ops/backup.sh.
#
# Stops the API, recreates the `hopper` database from the dump, then restarts the
# API (DbUp migrations revalidate the schema at startup).
#
#   ./ops/restore.sh /var/backups/hopper-jobqueue/daily/hopper_20260817_030000.dump
set -euo pipefail

# Explicit -f compose.yaml: never let compose.override.yaml (dev) apply here.
COMPOSE="docker compose -f compose.yaml"

DUMP=${1:?usage: restore.sh <file.dump>}
test -s "$DUMP"

echo "Stopping the API (no writes or connections during the restore)…"
$COMPOSE stop hopper

echo "Restoring $DUMP…"
# --create --clean: the hopper database is dropped then recreated from the dump;
# the connection goes through the `postgres` database for the duration.
$COMPOSE exec -T hopper-db pg_restore -U hopper -d postgres \
    --create --clean --if-exists --exit-on-error < "$DUMP"

echo "Restarting the API…"
$COMPOSE up -d hopper

echo "OK — check /readyz and the dashboard."
