#!/usr/bin/env bash
# Restauration d'une sauvegarde produite par ops/backup.sh.
#
# Arrête l'API, recrée la base `hopper` depuis le dump, puis redémarre l'API
# (les migrations DbUp revalident le schéma au démarrage).
#
#   ./ops/restore.sh /var/backups/hopper-jobqueue/daily/hopper_20260817_030000.dump
set -euo pipefail

# -f compose.yaml explicite : ne jamais laisser compose.override.yaml (dev) s'appliquer ici.
COMPOSE="docker compose -f compose.yaml"

DUMP=${1:?usage: restore.sh <fichier.dump>}
test -s "$DUMP"

echo "Arrêt de l'API (aucune écriture ni connexion pendant la restauration)…"
$COMPOSE stop hopper

echo "Restauration de $DUMP…"
# --create --clean : la base hopper est supprimée puis recréée depuis le dump ;
# la connexion se fait sur la base `postgres` le temps de l'opération.
$COMPOSE exec -T hopper-db pg_restore -U hopper -d postgres \
    --create --clean --if-exists --exit-on-error < "$DUMP"

echo "Redémarrage de l'API…"
$COMPOSE up -d hopper

echo "OK — vérifier /readyz et le dashboard."
