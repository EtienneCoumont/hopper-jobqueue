#!/usr/bin/env bash
# pg_dump backup of the hopper-db container, compressed (custom format), with
# rolling retention: 7 daily + 4 weekly (Sunday copy).
#
# Run from the project directory (where compose.yaml lives), typically from a
# daily cron:
#   0 3 * * *  cd /opt/hopper-jobqueue && ./ops/backup.sh /var/backups/hopper-jobqueue
set -euo pipefail

# Explicit -f compose.yaml: never let compose.override.yaml (dev) apply here.
COMPOSE="docker compose -f compose.yaml"

BACKUP_DIR=${1:-./backups}
STAMP=$(date -u +%Y%m%d_%H%M%S)

mkdir -p "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly"

TARGET="$BACKUP_DIR/daily/hopper_${STAMP}.dump"
$COMPOSE exec -T hopper-db pg_dump -U hopper -d hopper -Fc > "$TARGET"

# Minimal verification: the file is not empty and pg_restore can read it.
test -s "$TARGET"
$COMPOSE exec -T hopper-db pg_restore --list < "$TARGET" > /dev/null

# Weekly copy on Sundays.
if [ "$(date -u +%u)" = "7" ]; then
    cp "$TARGET" "$BACKUP_DIR/weekly/"
fi

# Retention: 7 daily, 4 weekly. (|| true: the glob may match nothing.)
{ ls -1t "$BACKUP_DIR"/daily/hopper_*.dump 2>/dev/null || true; } | tail -n +8 | xargs -r rm --
{ ls -1t "$BACKUP_DIR"/weekly/hopper_*.dump 2>/dev/null || true; } | tail -n +5 | xargs -r rm --

echo "OK: $TARGET ($(du -h "$TARGET" | cut -f1))"
