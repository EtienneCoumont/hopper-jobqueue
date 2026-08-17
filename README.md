# hopper-jobqueue

File de travaux HTTP entre des **producteurs quelconques** (scripts, applications, webhooks,
`curl`…) et un ou plusieurs **workers derrière NAT** qui ne peuvent faire que des appels
HTTPS sortants. Le service stocke et distribue des jobs avec une sémantique de **bail**
(lease) ; il n'exécute rien et n'appelle jamais rien vers l'extérieur — les producteurs
relisent l'état par `GET /jobs/{id}`.

Stack : .NET 10 (minimal API + Razor Pages), PostgreSQL 17, Npgsql + Dapper, DbUp,
Serilog (console JSON), xUnit + Testcontainers.

## Démarrage rapide (dev)

```bash
docker network create traefik-public   # une fois (partagé avec Traefik en prod)
cp .env.example .env                   # renseigner HOPPER_DB_PASSWORD
docker compose up -d                   # compose.override.yaml publie :8080 + dotnet watch
curl http://localhost:8080/healthz
```

Le dashboard est sur `http://localhost:8080/admin`. Au premier démarrage, si la table des
clés est vide, une clé admin d'amorçage est écrite **une seule fois** dans les logs
(`docker compose logs hopper | grep bootstrap`). S'en servir pour se connecter, déclarer
une file (`/admin/kinds`), créer les vraies clés (`/admin/keys`), puis **révoquer la clé
d'amorçage** — elle est passée dans les logs.

Tests (base PostgreSQL réelle via Testcontainers, Docker requis) :

```bash
dotnet test
```

## Variables d'environnement

| Variable | Rôle |
|---|---|
| `HOPPER_DB_CONNECTIONSTRING` | chaîne de connexion Npgsql (**requise**) — composée automatiquement par `compose.yaml` à partir de `HOPPER_DB_PASSWORD` |
| `HOPPER_DB_PASSWORD` | mot de passe PostgreSQL, lu depuis `.env` par compose |
| `HOPPER_BOOTSTRAP_ADMIN_KEY` | optionnelle — clé admin d'amorçage fournie (`hjq_admin_{32 base62}`) au lieu d'une clé générée + loguée |
| `HOPPER_PUBLIC_HOST` | hôte public du routeur Traefik (ex. `hopper.exemple.ch`) |
| `HOPPER_ADMIN_IP_ALLOWLIST` | plages IP autorisées sur `/admin` (middleware Traefik) |
| `HOPPER_LOG_LEVEL` | `Verbose`…`Error`, défaut `Information` |
| `HOPPER_SWEEP_INTERVAL_SECONDS` | période de la tâche de fond, défaut `60` |

Aucun secret en fichier : tout passe par l'environnement (`.env` est ignoré par git).

## Modèle

- Un job appartient à une file (`kind`), **déclarée avant usage** (dashboard → Files).
  `kind` inconnu ⇒ `400` avec la liste des kinds autorisés pour la clé.
- Statuts : `pending → leased → done | failed | expired | cancelled`. `attempts`
  s'incrémente **au claim** (protection poison message). Un job dont `expires_at` est
  dépassé n'est jamais distribué. `done` et `cancelled` sont terminaux (`requeue` admin
  possible depuis `failed`/`expired`/`cancelled`, jamais depuis `done`).
- Chaque transition écrit dans `jobqueue.job_events` (piste d'audit du dashboard).
- Clés API : `hjq_{scope}_{32 base62}`, scopes `producer` / `worker` / `admin` (admin =
  tout). Stockage SHA-256, comparaison en temps constant, préfixe en clair pour
  l'identification. Une clé par producteur et par worker, chacune avec ses
  `allowed_kinds` : la révocation reste ciblée.

## API

Préfixe `/api/v1`, JSON `camelCase`, erreurs en `application/problem+json`,
`Authorization: Bearer hjq_…`. Exemples `curl` (remplacer la clé et l'hôte) :

### `POST /jobs` — scope `producer`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs \
  -H "Authorization: Bearer $PRODUCER_KEY" -H 'Content-Type: application/json' \
  -d '{
    "idempotencyKey": "cron:2026-08-17T03:00",
    "kind": "invoice-ocr",
    "project": "compta",
    "payload": { "documentRef": "s3://bucket/doc.pdf" },
    "ttlSeconds": 86400,
    "maxAttempts": 3
  }'
# 201 {"id":42,"status":"pending","created":true}
# 200 {"id":42,"status":"pending","created":false}  si la clé d'idempotence existe déjà (rejeu sans erreur)
# 400 si champ requis manquant, payload > 32 Ko, ou kind inconnu/non autorisé
```

`project` est un simple libellé de regroupement, optionnel. `ttlSeconds` (max 604800) et
`maxAttempts` (max 10) surchargent les défauts de la file.

### `POST /jobs/claim` — scope `worker`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/claim \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "workerId": "worker-atelier", "leaseSeconds": 1200, "kinds": ["invoice-ocr"] }'
# 200 job complet, avec leaseToken + leaseUntil — conserver le leaseToken
# 204 file vide — re-poller plus tard (30 s est un bon rythme)
# 403 si aucun des kinds demandés n'est autorisé pour la clé
```

`kinds` omis = toutes les files de la clé. Sélection équitable entre files : le plus vieux
job de chaque file éligible, puis tirage au hasard — une grosse file n'affame pas les
petites. Les files en pause (`enabled = false`) ne distribuent pas.

### `POST /jobs/{id}/heartbeat` — scope `worker`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/42/heartbeat \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "leaseToken": "'$LEASE_TOKEN'", "leaseSeconds": 1200 }'
# 200 {"id":42,"leaseUntil":"…"} — bail prolongé
# 409 bail perdu : abandonner le job, un autre worker peut l'avoir repris
```

### `POST /jobs/{id}/complete` — scope `worker`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/42/complete \
  -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{ "leaseToken": "'$LEASE_TOKEN'", "outcome": "success",
        "result": { "report": "…", "costUsd": 0.42, "durationMs": 91000 } }'
# 200 {"id":42,"status":"done",…}   statut final calculé : done, pending (retry) ou failed
# 409 leaseToken périmé — un worker zombie ne peut pas écraser le résultat d'un autre
# 400 si result > 256 Ko (mettre les gros livrables ailleurs, passer une référence)
```

`outcome: "failure"` exige `error` ; le job repart en `pending` s'il reste des tentatives,
sinon `failed`. Rejouer le même complete avec le même token renvoie `200` sans réécrire.

### `GET /jobs/{id}` et `GET /jobs/by-key/{idempotencyKey}` — scope `producer`

```bash
curl -s https://hopper.exemple.ch/api/v1/jobs/42 -H "Authorization: Bearer $PRODUCER_KEY"
curl -s https://hopper.exemple.ch/api/v1/jobs/by-key/cron:2026-08-17T03:00 \
  -H "Authorization: Bearer $PRODUCER_KEY"
# 200 état + result + lastError (jamais le leaseToken) ; 404 si le kind n'est pas
# dans les allowed_kinds de la clé — jamais 403, pas d'énumération
```

### Administration — scope `admin`

```bash
curl -s 'https://hopper.exemple.ch/api/v1/jobs?status=failed&kind=invoice-ocr&q=timeout&page=1' \
  -H "Authorization: Bearer $ADMIN_KEY"                    # liste paginée (50/page)
curl -s https://hopper.exemple.ch/api/v1/jobs/42 -H "Authorization: Bearer $ADMIN_KEY"
                                                           # détail + timeline job_events
curl -s -X POST https://hopper.exemple.ch/api/v1/jobs/42/requeue -H "Authorization: Bearer $ADMIN_KEY"
curl -s -X POST https://hopper.exemple.ch/api/v1/jobs/42/cancel  -H "Authorization: Bearer $ADMIN_KEY"
curl -s https://hopper.exemple.ch/api/v1/stats -H "Authorization: Bearer $ADMIN_KEY"
# stats : compte par statut, âge du plus vieux pending, débit 24 h — si l'âge du plus
# vieux pending grimpe, le worker est mort ou saturé ; c'est LA métrique à surveiller
```

### Santé

```bash
curl -s https://hopper.exemple.ch/healthz   # vivant, sans toucher la base
curl -s https://hopper.exemple.ch/readyz    # vérifie la connexion PostgreSQL
```

## Cycle complet (exemple bout en bout)

```bash
H=https://hopper.exemple.ch/api/v1
# 1. le producteur dépose
JOB=$(curl -s $H/jobs -H "Authorization: Bearer $PRODUCER_KEY" -H 'Content-Type: application/json' \
  -d '{"idempotencyKey":"demo:1","kind":"invoice-ocr","payload":{"ref":"doc-123"}}')
ID=$(echo $JOB | jq -r .id)

# 2. le worker (derrière NAT) réclame
CLAIM=$(curl -s $H/jobs/claim -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"workerId":"w1","leaseSeconds":1200}')
TOKEN=$(echo $CLAIM | jq -r .leaseToken)

# 3. pendant le traitement, il prolonge le bail
curl -s $H/jobs/$ID/heartbeat -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"leaseToken":"'$TOKEN'"}'

# 4. il rend le résultat
curl -s $H/jobs/$ID/complete -H "Authorization: Bearer $WORKER_KEY" -H 'Content-Type: application/json' \
  -d '{"leaseToken":"'$TOKEN'","outcome":"success","result":{"report":"ok"}}'

# 5. le producteur relit quand il veut
curl -s $H/jobs/by-key/demo:1 -H "Authorization: Bearer $PRODUCER_KEY"
```

## Déploiement (Traefik + Docker)

Le conteneur ne publie **aucun port** ; Traefik le joint par le réseau `traefik-public`,
PostgreSQL vit sur `hopper-internal` et n'est jamais exposé. TLS est terminé par Traefik —
le service n'active ni HTTPS ni HSTS et fait confiance à `X-Forwarded-For`/`-Proto`
(`KnownIPNetworks`/`KnownProxies` vidés : l'IP de Traefik change à chaque recréation).

```bash
docker network create traefik-public        # si Traefik ne l'a pas déjà créé
cp .env.example .env                        # HOPPER_DB_PASSWORD, HOPPER_PUBLIC_HOST, allowlist IP
docker compose -f compose.yaml up -d --build
```

**Toujours `-f compose.yaml` en production** : sans lui, `compose.override.yaml` (dev)
publierait le port et monterait le code.

`/admin` est en plus restreint par IP au niveau Traefik (`HOPPER_ADMIN_IP_ALLOWLIST`) —
défense supplémentaire, la clé admin reste requise derrière. Connexion dynamique :
prévoir une plage large plutôt que de désactiver la protection.

L'API est publique sur internet : limites de corps Kestrel (64 Ko, 512 Ko sur
`/complete`) doublées d'un buffering Traefik, rate limiting par clé API (authentifié) et
par IP (non authentifié), 404 neutres, pas d'en-tête `Server`, pas de CORS.

### PostgreSQL — version épinglée

L'image est **`postgres:17`, jamais `latest`** : une image qui passe en majeure suivante
refuse de démarrer sur un répertoire de données existant et impose un `pg_upgrade` ou un
cycle dump/restore. Pour changer de majeure : sauvegarde (`ops/backup.sh`), montée de
version dans `compose.yaml`, restauration (`ops/restore.sh`).

### Arrêt propre

`stop_grace_period: 30s` dans compose > `ShutdownTimeout` ASP.NET (20 s) : la requête
HTTP en cours se termine sur `SIGTERM`.

## Sauvegarde et restauration

`pg_dump` quotidien compressé (format custom), rétention 7 quotidiennes + 4
hebdomadaires. Cron sur l'hôte :

```cron
0 3 * * *  cd /opt/hopper-jobqueue && ./ops/backup.sh /var/backups/hopper-jobqueue >> /var/log/hopper-backup.log 2>&1
```

Restauration (procédure **exécutée et vérifiée** : volume détruit puis restauré à
l'identique — jobs, événements, clés — lors de la mise en place) :

```bash
./ops/restore.sh /var/backups/hopper-jobqueue/daily/hopper_YYYYMMDD_HHMMSS.dump
# 1. arrête l'API (aucune connexion pendant l'opération)
# 2. pg_restore --create --clean : la base hopper est recréée depuis le dump
# 3. relance l'API ; vérifier /readyz puis le dashboard
```

Une sauvegarde jamais restaurée n'est pas une sauvegarde : refaire ce drill après tout
changement de version majeure de PostgreSQL.

## Développement

- `docker compose up -d` : PostgreSQL + API en `dotnet watch` sur
  `http://localhost:8080` (rechargement à chaud du code monté).
- Dashboard en dev : utiliser `http://localhost:8080/admin` (le cookie de session est
  `Secure` ; les navigateurs l'acceptent sur `localhost`, pas sur une IP nue).
- `dotnet test` : les 10 scénarios du brief (§9) + compléments, sur base réelle.
  Testcontainers a besoin du démon Docker et tire `postgres:17`.
