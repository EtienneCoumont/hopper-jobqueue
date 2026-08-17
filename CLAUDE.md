# hopper-jobqueue — repères pour les sessions futures

Service de file de jobs HTTP : des producteurs quelconques déposent, des workers derrière
NAT viennent chercher (polling sortant uniquement), un dashboard admin contrôle. Le brief
complet est dans `BRIEF.md` — le lire avant toute évolution ; tout y est tranché (§14).

## Architecture

- `src/HopperJobQueue.Api` — unique projet : minimal API (`/api/v1`), Razor Pages
  (`/admin`), tâche de fond. Pas de couches, pas d'ORM : Dapper + SQL explicite dans
  `Jobs/JobStore.cs` (jobs) et `Auth/ApiKeyStore.cs` (clés).
- `Migrations/*.sql` — scripts numérotés embarqués, appliqués par DbUp au démarrage sous
  `pg_advisory_lock` (échec migration = sortie non nulle, pas de démarrage en base
  incohérente). Journal DbUp : `jobqueue.schemaversions`.
- `Maintenance/SweeperService.cs` — toutes les 60 s, une transaction : TTL dépassés →
  `expired` ; bails expirés à bout de tentatives → `failed` (`last_error = "bail expiré,
  tentatives épuisées"`) ; purge des terminaux au-delà de `job_kinds.retention_days` ;
  flush du tampon `last_used_at`. `RunOnceAsync` est public pour les tests.
- `Program.cs` — ordre du pipeline significatif : ForwardedHeaders → ExceptionHandler →
  limite de corps /complete → en-têtes sécurité /admin → auth par clé API →
  rate limiter (partition par clé sinon par IP) → enforcement des scopes (métadonnées
  d'endpoint) → static files → cookie auth → antiforgery → endpoints.
- Tests : `tests/HopperJobQueue.Tests`, une collection xUnit séquentielle, un conteneur
  PostgreSQL 17 partagé (Testcontainers) + `WebApplicationFactory`, reset des tables par
  test. Les 10 scénarios du §9 du brief y sont, nommés `TestN_…`.

## Invariants (§4 du brief — couverts par les tests, ne pas casser)

- `for update skip locked` dans le claim : **obligatoire**, c'est ce qui empêche deux
  workers d'obtenir le même job. Les prédicats d'éligibilité sont répétés dans le select
  verrouillant (re-vérification EvalPlanQual en READ COMMITTED) ; le handler boucle tant
  que l'instruction rend zéro ligne alors qu'il reste des jobs éligibles.
- Équité entre files : plus vieux job de **chaque** file éligible puis tirage aléatoire.
  Jamais de `order by created_at` global.
- `attempts` s'incrémente **au claim**, pas au complete (protection poison message).
- `expires_at` dépassé ⇒ jamais distribué, même `pending`.
- `done` et `cancelled` sont terminaux ; `requeue` admin possible depuis
  `failed`/`expired`/`cancelled`, **jamais** depuis `done`.
- Toute transition écrit `job_events` **dans la même transaction** que l'update.
- Idempotence d'enqueue en base (`on conflict do nothing` puis relecture) — jamais de
  select préalable. Rejeu d'enqueue = `200 created:false`, pas `409`.
- `complete`/`heartbeat` gardés par `leaseToken` ; rejeu d'un complete identique = `200`
  sans réécriture ; token périmé = `409` (un zombie n'écrase jamais le travail d'autrui).
- Le `leaseToken` n'apparaît que dans la réponse de claim, jamais dans les lectures.
- `GET /jobs/{id}` hors `allowed_kinds` ⇒ `404`, jamais `403` (pas d'énumération).
- Le service est agnostique : aucune mention d'un producteur particulier (n8n, mail…)
  dans le code, les types, les colonnes ou les erreurs. `payload`/`result` opaques.

## Écarts assumés vs brief (documentés, ne pas « corriger » sans réfléchir)

- **Préfixe de clé stocké : 16 caractères, pas 12.** `hjq_producer` fait exactement
  12 caractères : deux clés producer entreraient en collision sur `prefix unique`.
- **Claim : boucle applicative autour de l'instruction unique du brief.** Sous
  contention, `skip locked` + candidats à une ligne par file rendraient des 204 mensongers
  (test 1 « exactement 5 » du §9). L'instruction reste seule à verrouiller/distribuer.
- **Test d'équité borné à 20 claims au lieu de « moins de 10 ».** Le tirage 50/50 du
  brief lui-même rend « < 10 » flaky à ~5 % ; 20 garde la démonstration (sans équité il en
  faudrait ~200) sans flakiness (~0,02 %).
- **Cookie antiforgery en `SameAsRequest`** (le cookie de session, lui, est bien
  `Secure`/`HttpOnly`/`Strict`) : `Always` fait planter le rendu des formulaires en HTTP
  direct (dev). Derrière Traefik, X-Forwarded-Proto=https ⇒ Secure en production.
- **Gestion des files sur le dashboard** (`/admin/kinds`) : nécessaire pour « kind déclaré
  avant usage » + pause pilotable (§4) ; volontairement absente de l'API (§5 inchangé).

## Commandes

```bash
dotnet build                      # zéro warning exigé (TreatWarningsAsErrors)
dotnet test                       # Docker requis (Testcontainers, image postgres:17)
docker compose up -d              # dev : port 8080 publié, dotnet watch (override)
docker compose -f compose.yaml up -d --build    # prod : Traefik, aucun port publié
./ops/backup.sh <dir>             # pg_dump custom + rétention 7j/4sem
./ops/restore.sh <dump>           # stoppe l'API, recrée la base, relance
```

Config par env uniquement, préfixe `HOPPER_` (voir README / `.env.example`). Pas de
nouvelle dépendance NuGet sans validation (§3). Timestamps UTC partout
(`timestamptz` ↔ `DateTimeOffset`, handler Dapper dans `Infrastructure/DapperConfig.cs`).
