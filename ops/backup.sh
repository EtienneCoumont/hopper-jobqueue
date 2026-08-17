#!/usr/bin/env bash
# Sauvegarde pg_dump du conteneur hopper-db, compressée (format custom), avec
# rétention glissante : 7 quotidiennes + 4 hebdomadaires (copie du dimanche).
#
# À lancer depuis le répertoire du projet (là où vit compose.yaml), typiquement en
# cron quotidien :
#   0 3 * * *  cd /opt/hopper-jobqueue && ./ops/backup.sh /var/backups/hopper-jobqueue
set -euo pipefail

# -f compose.yaml explicite : ne jamais laisser compose.override.yaml (dev) s'appliquer ici.
COMPOSE="docker compose -f compose.yaml"

BACKUP_DIR=${1:-./backups}
STAMP=$(date -u +%Y%m%d_%H%M%S)

mkdir -p "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly"

TARGET="$BACKUP_DIR/daily/hopper_${STAMP}.dump"
$COMPOSE exec -T hopper-db pg_dump -U hopper -d hopper -Fc > "$TARGET"

# Vérification minimale : le fichier n'est pas vide et pg_restore sait le lire.
test -s "$TARGET"
$COMPOSE exec -T hopper-db pg_restore --list < "$TARGET" > /dev/null

# Copie hebdomadaire le dimanche.
if [ "$(date -u +%u)" = "7" ]; then
    cp "$TARGET" "$BACKUP_DIR/weekly/"
fi

# Rétention : 7 quotidiennes, 4 hebdomadaires. (|| true : le glob peut ne rien matcher.)
{ ls -1t "$BACKUP_DIR"/daily/hopper_*.dump 2>/dev/null || true; } | tail -n +8 | xargs -r rm --
{ ls -1t "$BACKUP_DIR"/weekly/hopper_*.dump 2>/dev/null || true; } | tail -n +5 | xargs -r rm --

echo "OK: $TARGET ($(du -h "$TARGET" | cut -f1))"
