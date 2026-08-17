#!/usr/bin/env bash
# Restore a backup produced by backup.sh.
#
# Stops the API, recreates the `hopper` database from the dump, then restarts
# the API (DbUp migrations revalidate the schema at startup).
#
# This script lives in the deployment directory, next to compose.yaml and .env,
# and changes to its own directory itself — call it from anywhere:
#   /opt/hopper-jobqueue/restore.sh /var/backups/hopper-jobqueue/daily/hopper_20260817_030000.dump
set -euo pipefail

DUMP=$(realpath "${1:?usage: restore.sh <file.dump>}")
test -s "$DUMP"
cd "$(dirname "$0")"

echo "Stopping the API (no writes or connections during the restore)…"
docker compose stop hopper

echo "Restoring $DUMP…"
# --create --clean: the hopper database is dropped then recreated from the dump;
# the connection goes through the `postgres` database for the duration.
docker compose exec -T hopper-db pg_restore -U hopper -d postgres \
    --create --clean --if-exists --exit-on-error < "$DUMP"

echo "Restarting the API…"
docker compose up -d hopper

echo "OK — check /readyz and the dashboard."
