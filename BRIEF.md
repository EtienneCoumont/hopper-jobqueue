# Brief — HopperJobQueue

Petit service HTTP autonome qui sert de file de travaux entre des **producteurs quelconques**
et un ou plusieurs workers qui tournent sur des machines derrière NAT.

Le worker ne peut faire que des appels HTTPS **sortants** : c'est la contrainte qui dicte
toute l'architecture. Pas de socket entrant, pas d'accès direct à la base, pas de tunnel.

---

## 1. Rôle et périmètre

Le service ne fait que trois choses :

1. Accepter des jobs de **n'importe quel producteur**, de manière idempotente.
2. Les distribuer à un worker qui vient les chercher, avec une sémantique de **bail**
   (lease) : un job réservé et jamais rendu redevient disponible tout seul.
3. Conserver l'historique et l'exposer dans un petit dashboard pour le contrôle manuel.

### Le service est agnostique — point structurant

Le premier producteur sera une instance n8n, mais **n8n n'est qu'un producteur parmi
d'autres** et le service ne doit rien savoir de lui. Sont également attendus, sans que le
code change : des scripts shell ou PowerShell, des jobs planifiés, une application ASP.NET,
un webhook GitHub, un appel `curl` à la main depuis un poste.

Conséquences à respecter à la lettre :

- **Aucune mention de n8n, de Gmail ou de courrier électronique** dans le code, les noms de
  types, les colonnes, les messages d'erreur ou la documentation d'API. Le domaine métier
  est entièrement porté par `kind` et `payload`, que le service traite comme opaques.
- Les exemples de ce brief (`kind: "project-preanalysis"`, `idempotencyKey: "gmail:…"`, un
  payload contenant un sujet et un expéditeur) sont **des illustrations, pas un schéma**. Ne
  jamais typer le payload ni valider ses champs internes.
- Le seul contrat imposé au producteur est de fournir une clé d'idempotence stable dont il
  garde la maîtrise. Un préfixe par producteur (`gmail:…`, `github:…`, `cron:…`) est une
  bonne pratique à documenter, pas une règle à valider côté serveur.
- Chaque producteur a **sa propre clé API**, avec ses propres `allowed_kinds`. Deux
  producteurs ne partagent jamais une clé : c'est ce qui rend une révocation ciblée possible.

### Hors périmètre — ne pas implémenter

- Pas d'exécution de travail dans ce service. Il ne lance aucun processus, aucun agent,
  aucune analyse. Il stocke et distribue, rien d'autre.
- Pas de multi-tenant, pas de notion d'organisation ou d'utilisateur.
- Pas de priorités, pas de jobs planifiés, pas de dépendances entre jobs.
- Pas de WebSocket ni de SSE. Le worker fait du polling, c'est voulu.
- **Pas de callback HTTP sortant.** Le service n'appelle jamais rien vers l'extérieur : ni
  webhook de fin de job, ni notification. Les producteurs relisent l'état par
  `GET /jobs/{id}`. Cela évite la gestion des relances, des délais d'attente et le risque de
  SSRF, pour un bénéfice que le polling couvre déjà.
- Pas de contenu de mail en base. Le payload ne contient que des métadonnées et des
  identifiants (voir §5).
- Pas d'ORM lourd, pas de découpage en couches spéculatif, pas de médiateur type MediatR.

Si une fonctionnalité semble manquer, **demander avant de l'ajouter**.

---

## 2. Nommage — à respecter tel quel

Tous les identifiants dérivent du nom du service. Ne pas en inventer de variantes.

| Élément | Valeur |
|---|---|
| Dépôt Git | `hopper-jobqueue` |
| Solution / namespace racine | `HopperJobQueue` |
| Projets | `HopperJobQueue.Api`, `HopperJobQueue.Tests` |
| Image Docker | `hopper-jobqueue` |
| Services `compose` | `hopper` (API), `hopper-db` (PostgreSQL) |
| Volume Docker | `hopper-pgdata` |
| Réseaux Docker | `traefik-public`, `hopper-internal` |
| Base PostgreSQL | `hopper` |
| Schéma PostgreSQL | `jobqueue` |
| Préfixe des variables d'env. | `HOPPER_` |
| Préfixe des clés API | `hjq_` |
| Routeurs Traefik | `hopper-api`, `hopper-admin` |
| Hôte (exemple) | `hopper.exemple.ch` |

Base `hopper` + schéma `jobqueue` : le nom qualifié `jobqueue.jobs` reste explicite dans les
requêtes, et le schéma dédié laisse la place à d'autres schémas dans la même instance si le
besoin apparaît.

---

## 3. Stack imposée

| Choix | Valeur | Pourquoi |
|---|---|---|
| Runtime | .NET 10, minimal API, C# | environnement existant |
| Base | PostgreSQL 17 | conteneur dédié à ce service, schéma `jobqueue` |
| Accès données | Npgsql + Dapper | une table et demie, l'ORM n'apporte rien |
| Migrations | scripts SQL numérotés + DbUp | déterministe, lisible, pas de magie |
| Dashboard | Razor Pages, rendu serveur | zéro build front, zéro npm |
| Logs | `Microsoft.Extensions.Logging` + Serilog console en JSON | agrégation simple |
| Tests | xUnit + Testcontainers.PostgreSql | les tests de concurrence exigent une vraie base |

Contraintes :

- Aucune dépendance NuGet en dehors de cette liste sans validation préalable.
- Tous les timestamps sont en UTC, colonnes `timestamptz`, `DateTimeOffset` en C#.
- Configuration par variables d'environnement uniquement (pas de secrets en fichier).
- Le service tourne derrière Traefik, qui termine TLS. Ne pas gérer de certificat, ne pas
  activer `UseHttpsRedirection` ni HSTS, et configurer `ForwardedHeadersOptions` selon le
  §13 — la configuration par défaut ne marche pas en Docker.

---

## 4. Modèle de données

Schéma `jobqueue`. Migration initiale :

```sql
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
```

`job_events` est la piste d'audit : **toute** transition d'état y écrit une ligne, dans la
même transaction que la mise à jour de `jobs`. C'est ce qui rend le dashboard utile et les
incidents diagnosticables.

### Machine à états

```
pending  ──claim──▶  leased  ──complete(ok)──▶  done
   ▲                    │
   │                    ├──complete(erreur), attempts < max──▶  pending
   │                    ├──complete(erreur), attempts >= max─▶  failed
   │                    └──bail expiré, attempts < max────────▶  pending  (implicite)
   │                                                                │
   └──────────────── requeue (admin) ◀──── failed / expired ─────────┘

pending / leased  ──expires_at dépassé──▶  expired   (balayeur)
pending / leased  ──cancel (admin)─────▶  cancelled
```

Règles invariantes, à couvrir par des tests :

- Un job en `done` ou `cancelled` est terminal : aucune transition n'en sort sauf `requeue`
  explicite depuis le dashboard, et jamais depuis `done`.
- `attempts` s'incrémente **au claim**, pas au complete. Un worker qui crashe sans rien
  renvoyer consomme donc une tentative — c'est la protection contre le poison message.
- Un job dont `expires_at` est dépassé n'est **jamais** distribué, même s'il est `pending`.

### `kind` = nom de file

Le service est multi-usage : `kind` est à la fois le nom de la file et la clé de
configuration. Conséquences à respecter :

- Un `kind` doit être **déclaré dans `job_kinds` avant usage**. Une contrainte de clé
  étrangère l'impose. Un producteur qui envoie un `kind` inconnu reçoit `400` avec la liste
  des `kind` autorisés pour sa clé. Sans ça, une faute de frappe côté producteur crée une
  file fantôme dont les jobs ne sont jamais réclamés et expirent en silence.
- `job_kinds.enabled = false` met une file en pause : les jobs continuent d'être acceptés
  mais ne sont plus distribués. Contrôle d'exploitation utile, pilotable depuis le
  dashboard, sans toucher aux producteurs.
- Les défauts de TTL, tentatives et durée de bail viennent de `job_kinds`, pas de constantes
  en dur. Une OCR de facture et une pré-analyse de dépôt n'ont pas les mêmes ordres de
  grandeur. Les valeurs fournies dans la requête d'enqueue surchargent ces défauts.
- `project` est **optionnel** : c'est un simple libellé de regroupement pour le filtrage
  dans le dashboard, pas une notion structurante. Tout le contexte métier va dans `payload`.

---

## 5. API

Préfixe `/api/v1`. Corps en JSON, `camelCase`. Erreurs au format `application/problem+json`.

### `POST /jobs` — scope `producer`

```jsonc
// requête
{
  "idempotencyKey": "gmail:19a3f2c8b1d4e5f6",  // requis, <= 200 car.
  "kind": "project-preanalysis",               // requis
  "project": "mon-projet",                     // requis
  "payload": { "subject": "...", "summary": "...", "sender": "..." },
  "ttlSeconds": 86400,                         // optionnel, défaut 86400, max 604800
  "maxAttempts": 3                             // optionnel, défaut 3, max 10
}
```

Réponses :

- `201 Created` + `{ "id": 42, "status": "pending", "created": true }`
- `200 OK` + `{ "id": 42, "status": "leased", "created": false }` si la clé
  d'idempotence existe déjà. **Ne pas renvoyer 409.** Un producteur doit pouvoir rejouer un
  envoi
  sans que ça ressemble à une erreur.
- `400` si `payload` sérialisé dépasse **32 Ko**, ou si un champ requis manque.

L'idempotence se fait en base (`on conflict (idempotency_key) do nothing` puis relecture),
pas par un `select` préalable — sinon deux requêtes simultanées passent toutes les deux.

### `POST /jobs/claim` — scope `worker`

```jsonc
// requête
{ "workerId": "dev-etienne", "leaseSeconds": 1200, "kinds": ["project-preanalysis"] }
```

- `200 OK` + le job complet, **incluant `leaseToken`** (uuid) et `leaseUntil`.
- `204 No Content` si la file est vide. Pas de corps, pas d'erreur.

Requête de réservation, atomique, en une seule instruction :

```sql
update jobqueue.jobs set
  status      = 'leased',
  attempts    = attempts + 1,
  lease_token = gen_random_uuid(),
  lease_until = now() + (@leaseSeconds || ' seconds')::interval,
  worker_id   = @workerId
where id = (
  select id from jobqueue.jobs
  where (status = 'pending' or (status = 'leased' and lease_until < now()))
    and expires_at > now()
    and attempts < max_attempts
    and kind = any(@kinds)
  order by created_at
  limit 1
  for update skip locked
)
returning *;
```

Le `for update skip locked` est **obligatoire** : sans lui, deux claims concurrents peuvent
obtenir le même job. C'est le point le plus important du service.

Deux ajouts liés au multi-file :

- Les `kinds` demandés sont **intersectés avec les `allowed_kinds` de la clé** avant la
  requête. Un worker ne peut jamais réclamer un job d'une file qui ne lui est pas attribuée,
  même en le demandant explicitement. Si l'intersection est vide, `403`.
- La jointure doit écarter les `kind` dont `job_kinds.enabled = false`.
- **Équité entre files — obligatoire.** Un worker sert plusieurs `kind`, donc un
  `order by created_at` global est exclu : une file qui reçoit 500 jobs d'un coup affamerait
  toutes les autres jusqu'à écoulement. La règle est : prendre le plus vieux job de *chaque*
  file éligible, puis en choisir un au hasard.

Deux restrictions de PostgreSQL rendent l'écriture naïve impossible — les connaître évite
une demi-heure de tâtonnement :

- `select distinct on (…) … for update` est refusé (« FOR UPDATE is not allowed with
  DISTINCT clause »).
- Une clause de verrouillage ne peut pas s'appliquer au résultat d'un CTE (« FOR UPDATE
  cannot be applied to a WITH query »).

Il faut donc deux niveaux : un sous-select non verrouillant qui désigne les candidats, puis
un select verrouillant qui en retient un.

```sql
update jobqueue.jobs set
  status      = 'leased',
  attempts    = attempts + 1,
  lease_token = gen_random_uuid(),
  lease_until = now() + (@leaseSeconds || ' seconds')::interval,
  worker_id   = @workerId
where id = (
  select id from jobqueue.jobs
  where id in (
    select distinct on (kind) id
    from jobqueue.jobs j
    join jobqueue.job_kinds k on k.name = j.kind
    where j.kind = any(@kinds)
      and k.enabled
      and (j.status = 'pending' or (j.status = 'leased' and j.lease_until < now()))
      and j.expires_at > now()
      and j.attempts < j.max_attempts
    order by j.kind, j.created_at
  )
  order by random()
  limit 1
  for update skip locked
)
returning *;
```

L'ensemble des candidats compte au plus une ligne par file, donc le `order by random()`
porte sur une poignée de lignes : le coût est négligeable et il n'y a aucun état à
maintenir côté serveur pour faire tourner les files.

### `POST /jobs/{id}/heartbeat` — scope `worker`

```jsonc
{ "leaseToken": "…", "leaseSeconds": 1200 }
```

Prolonge `lease_until`. `200` avec le nouveau `leaseUntil`, `409` si le token ne correspond
pas ou si le job n'est plus `leased`. Le worker doit traiter ce 409 comme « j'ai perdu le
bail, j'abandonne » — donc le message d'erreur doit être explicite là-dessus.

### `POST /jobs/{id}/complete` — scope `worker`

```jsonc
{
  "leaseToken": "…",
  "outcome": "success",          // "success" | "failure"
  "result": { "report": "…", "costUsd": 0.42, "durationMs": 91000 },
  "error": null                  // requis si outcome = "failure"
}
```

- `200` avec le statut final calculé (`done`, `pending` si retry possible, ou `failed`).
- `409` si le `leaseToken` ne correspond pas. **Cas critique** : un worker zombie qui a
  perdu son bail ne doit pas pouvoir écraser le résultat d'un worker qui a repris le job.
- Idempotent : rejouer le même complete avec le même token renvoie `200` sans réécrire.

Le `result` sérialisé est plafonné à **256 Ko**. Au-delà, `400` : les gros livrables (rapport
complet, fichier généré) vont dans un stockage objet et seule leur référence passe ici.

### `GET /jobs/{id}` — scope `producer`

Un producteur doit pouvoir relire l'état et le résultat des jobs **qu'il a créés**, sinon il
n'a aucun moyen de récupérer le travail sans passer par un callback. Renvoie le job si son
`kind` fait partie des `allowed_kinds` de la clé, `404` sinon — pas `403`, pour ne pas
divulguer l'existence de jobs d'autres files.

Une variante par clé d'idempotence, `GET /jobs/by-key/{idempotencyKey}`, évite au producteur
de stocker l'`id` numérique : il retrouve son job avec l'identifiant qu'il connaît déjà.

### Endpoints d'administration — scope `admin`

| Méthode | Route | Effet |
|---|---|---|
| `GET` | `/jobs?status=&project=&kind=&q=&page=` | liste paginée, tri par `created_at` desc |
| `GET` | `/jobs/{id}` | détail + timeline des `job_events` |
| `POST` | `/jobs/{id}/requeue` | remet en `pending`, remet `attempts` à 0, journalise |
| `POST` | `/jobs/{id}/cancel` | passe en `cancelled` |
| `GET` | `/stats` | compte par statut, âge du plus vieux `pending`, débit 24 h |

### Santé

- `GET /healthz` — vivant, sans auth, sans toucher la base.
- `GET /readyz` — vérifie la connexion Postgres. Sans auth mais sans détail d'erreur.

---

## 6. Authentification

Trois scopes : `producer` (enqueue seul), `worker` (claim/heartbeat/complete seuls),
`admin` (tout + dashboard). Un scope ne donne accès qu'à ses propres routes — un token
worker qui appelle `/jobs` en POST reçoit `403`.

Format de clé : `hjq_{scope}_{32 caractères base62}`, par ex. `hjq_worker_7Kf2…`.
Le `prefix` stocké en clair est les 12 premiers caractères, pour identifier une clé dans le
dashboard et dans les logs sans exposer le secret.

Stockage : **SHA-256 du secret**, en `bytea`. Pas d'Argon2 ni de bcrypt ici — la clé fait
190 bits d'entropie aléatoire, il n'y a pas de dictionnaire à ralentir, et un hash lent
sur le chemin chaud du polling serait une erreur de conception.

Points de vigilance :

- Comparaison en **temps constant** (`CryptographicOperations.FixedTimeEquals`).
- La clé en clair n'existe qu'une fois, au moment de sa création : affichée dans la réponse
  puis jamais récupérable. Le dashboard doit le dire explicitement.
- Jamais de clé dans les logs, ni entière ni partiellement — uniquement le `prefix`.
- Transport par en-tête `Authorization: Bearer hjq_…`. Pas de clé en query string.
- `last_used_at` mis à jour au plus une fois par minute et par clé, en tâche de fond. Ne
  pas faire un `update` sur chaque requête : le worker poll toutes les 30 secondes, ça
  génèrerait de l'écriture inutile en continu.
- Rate limiting via `Microsoft.AspNetCore.RateLimiting`, à deux étages : fenêtre glissante
  **par clé** pour les requêtes authentifiées (généreuse sur `/jobs/claim`, le polling est
  légitime), et fenêtre **par IP client** pour les requêtes sans clé valide. Voir §12.

### Amorçage

Au premier démarrage, si la table `api_keys` est vide, le service crée une clé `admin`,
l'écrit **une fois** dans les logs au niveau `Warning` avec une consigne claire, et
n'y revient jamais. Alternative acceptée : une variable `HOPPER_BOOTSTRAP_ADMIN_KEY`.

---

## 7. Dashboard

Razor Pages, rendu serveur, sur `/admin`. Connexion par saisie d'une clé admin, échangée
contre un cookie de session (`HttpOnly`, `Secure`, `SameSite=Strict`). Antiforgery sur
toutes les actions POST.

Quatre pages suffisent :

1. **Vue d'ensemble** — compteurs par statut, âge du plus vieux `pending`, dernière activité
   par worker. C'est la page qui répond à « est-ce que ça tourne ? ».
2. **Liste** — filtres statut / projet / recherche, pagination. Actions en ligne : requeue,
   cancel.
3. **Détail** — payload et résultat en JSON formaté, timeline des `job_events`, dernière
   erreur en entier.
4. **Clés** — liste (nom, prefix, scope, dernière utilisation), création, révocation.

Contraintes de forme : pas de framework CSS externe, pas de JS de build. Un fichier CSS
écrit à la main, du JS uniquement pour le repliage des blocs JSON. La page de liste doit
être lisible à 1200 px sans défilement horizontal. Auto-rafraîchissement de la vue
d'ensemble par `<meta http-equiv="refresh">` toutes les 30 s — suffisant, et zéro code.

---

## 8. Tâche de fond

Un `BackgroundService` unique, toutes les 60 secondes, dans une transaction :

1. Les jobs `pending` ou `leased` dont `expires_at < now()` passent en `expired`.
2. Les jobs `leased` dont `lease_until < now()` et `attempts >= max_attempts` passent en
   `failed` avec `last_error = "bail expiré, tentatives épuisées"`. Ceux qui ont encore des
   tentatives sont laissés tels quels : la requête de claim les reprendra naturellement.
3. Purge des jobs terminaux de plus de 90 jours (durée configurable).

Chaque transition écrit dans `job_events` avec `actor = 'system'`.

---

## 9. Tests exigés

Les tests unitaires sur la logique triviale n'intéressent personne. Ce qui doit être couvert,
avec Testcontainers et une vraie base :

1. **Claim concurrent** — 20 claims en parallèle sur 5 jobs : exactement 5 réussissent,
   aucun job distribué deux fois, 15 réponses `204`. C'est le test qui justifie le projet.
2. **Enqueue concurrent** — 10 POST simultanés avec la même clé d'idempotence : un seul job
   créé, les 10 réponses cohérentes.
3. **Bail expiré** — un job claim puis abandonné redevient claimable après expiration,
   avec `attempts` correctement incrémenté.
4. **Token de bail périmé** — worker A claim, son bail expire, worker B claim, puis A tente
   un `complete` : il reçoit `409` et le job de B n'est pas altéré.
5. **Poison message** — un job claim et abandonné `max_attempts` fois finit en `failed` et
   n'est plus jamais distribué.
6. **Isolation des scopes** — chaque scope reçoit `403` sur les routes des deux autres.
7. **TTL** — un job dont `expires_at` est passé n'est pas distribué même en `pending`.
8. **Cloisonnement des files** — une clé worker limitée à `kind-a` ne reçoit jamais un job
   `kind-b`, y compris quand elle le demande explicitement et que la file `kind-b` est la
   seule non vide.
9. **File en pause** — `enabled = false` : l'enqueue réussit, le claim renvoie `204`.
10. **Équité** — deux files, 200 jobs dans la première et 3 dans la seconde. Un worker qui
    réclame les deux obtient les 3 jobs de la petite file en moins de 10 claims. Sans la
    sélection équitable, il en faudrait 200.

**Pas de test de charge.** Le volume cible est de quelques dizaines de jobs par jour : mesurer
un débit ne dirait rien d'utile. Ce qui casse ici n'est pas la charge mais la **concurrence**,
et le test 1 la couvre déjà — 20 claims simultanés sur 5 jobs sondent exactement le même
chemin de code que deux workers en production. Si un jour plusieurs workers tournent
réellement, porter le test 1 à deux processus distincts plutôt que deux tâches suffira.

Le signal d'exploitation qui remplace le test de charge est l'âge du plus vieux `pending`,
déjà exposé par `/stats` : s'il grimpe, le worker est mort ou saturé. C'est la seule métrique
à surveiller.

---

## 10. Ordre de construction

Livrer par étapes, chacune fonctionnelle et testée avant de passer à la suivante.

1. Squelette, `/healthz`, migrations DbUp, connexion Postgres, docker-compose pour le dev.
2. Table `jobs`, `POST /jobs` avec idempotence, `POST /jobs/claim` avec bail. Tests 1 à 3.
3. Heartbeat et complete avec vérification du `leaseToken`. Tests 4 et 5.
4. Clés API et scopes. Test 6. Amorçage de la clé admin.
5. `job_events` sur toutes les transitions, endpoints d'admin.
6. Dashboard.
7. Tâche de fond, rate limiting, test 7.
8. Dockerfile, `compose.yaml` avec Traefik et les deux réseaux, README avec les variables
   d'environnement et un exemple `curl` par endpoint.
9. Sauvegarde `pg_dump` avec rétention, et procédure de restauration testée.

---

## 11. Définition du « terminé »

- `dotnet test` passe, y compris les dix scénarios du §9.
- Aucun avertissement de compilation. `<TreatWarningsAsErrors>` activé.
- README : variables d'environnement, procédure de déploiement, un `curl` par endpoint,
  et un exemple de cycle complet enqueue → claim → heartbeat → complete.
- Un `CLAUDE.md` à la racine qui résume l'architecture, les invariants de la §4 et les
  commandes utiles, pour les sessions futures.
- Aucun secret ni chaîne de connexion dans le dépôt. `.gitignore` couvrant
  `appsettings.Development.json` et `launchSettings.json`.
- Sauvegarde `pg_dump` en place et **restauration exécutée une fois**, avec la procédure
  consignée dans le README.

---

## 12. Exposition publique

L'API est **ouverte sur l'internet public**. C'est un choix assumé : les producteurs sont
répartis et arbitraires, et les workers sont derrière NAT, donc tous les appels — production
comme consommation — sont des requêtes HTTPS entrantes depuis n'importe où.

Cela change le modèle de menace. Toutes les routes `/api` doivent tenir face à des scanners
automatisés en continu, pas seulement face à des clients coopératifs.

### Ce qui est public et ce qui ne l'est pas

| Surface | Exposition |
|---|---|
| `/api/v1/jobs` (POST, GET) | publique, clé `producer` requise |
| `/api/v1/jobs/claim`, `/heartbeat`, `/complete` | publique, clé `worker` requise |
| `/api/v1/jobs` (routes d'admin) | publique, clé `admin` requise |
| `/admin` (dashboard) | publique mais **restreinte par IP** au niveau Traefik |
| `/healthz`, `/readyz` | publiques, sans auth, sans aucun détail |
| PostgreSQL | jamais exposé, réseau interne uniquement |

### Durcissement obligatoire

- **Limite de taille de corps au niveau Kestrel** (`MaxRequestBodySize`), pas seulement une
  validation applicative : 64 Ko sur `/jobs`, 512 Ko sur `/complete`. Sinon un corps de
  plusieurs gigaoctets est lu en entier avant d'être rejeté. Doubler d'un
  `buffering.maxRequestBodyBytes` côté Traefik.
- **Deux étages de rate limiting.** Pour les requêtes authentifiées, par clé API — c'est le
  compteur qui compte. Pour les requêtes **sans clé valide**, par IP client lue dans
  `X-Forwarded-For`, avec un seuil bas : c'est la seule protection contre un balayage, et
  elle ne fonctionne que si `ForwardedHeadersOptions` est correctement configuré (§13). Un
  `ratelimit` Traefik en amont sert de filet.
- **Journalisation des échecs d'authentification** avec l'IP et le préfixe de clé tenté, mais
  au niveau `Information`, pas `Warning` : sur une IP publique le bruit de fond des scanners
  saturerait des alertes.
- **404 pour tout chemin inconnu**, sans page d'erreur, sans en-tête `Server`, sans version
  de framework. `app.UseExceptionHandler` renvoyant du `problem+json` neutre ; jamais de
  `DeveloperExceptionPage` en production, jamais de trace d'appels dans une réponse.
- **Aucune énumération.** `GET /jobs/{id}` sur un job d'une autre file renvoie `404`, jamais
  `403` — déjà spécifié au §5, mais c'est ici que la raison devient concrète.
- **Pas de CORS.** Aucun producteur n'est un navigateur. Ne pas ajouter de politique CORS,
  même permissive « pour tester ».
- En-têtes de sécurité sur `/admin` (`X-Content-Type-Options`, `Referrer-Policy`,
  `Content-Security-Policy` restrictive) : c'est la seule surface avec des sessions par
  cookie, donc la seule où le XSS a un intérêt pour un attaquant.
- Les `ipallowlist` de Traefik sur `/admin` supposent des IP stables. Si ta connexion est
  dynamique, prévoir une plage large plutôt que de désactiver la protection.

---

## 13. Déploiement — conteneur derrière Traefik

Cible arrêtée : PostgreSQL 16+, .NET 10, image Docker, Traefik en frontal sur le même hôte
que n8n. Le conteneur ne publie **aucun port sur l'hôte** — `expose` uniquement, Traefik
atteint le service par le réseau Docker.

### Base de données

Conteneur PostgreSQL **dédié à ce service**. n8n reste sur son SQLite et n'est pas touché.

- Version majeure **épinglée** : `postgres:17`, jamais `latest` ni `postgres`. Une image qui
  passe en majeure suivante refuse de démarrer sur un répertoire de données existant et
  impose un `pg_upgrade` ou un cycle dump/restore. Le README doit mentionner cette contrainte
  à côté de la version choisie.
- Volume nommé pour `/var/lib/postgresql/data`. Pas de bind mount sur l'hôte.
- Aucun port publié, uniquement sur `hopper-internal`.
- `pg_dump` quotidien via un conteneur de sauvegarde ou une tâche cron de l'hôte, compressé,
  avec rétention glissante (7 quotidiennes, 4 hebdomadaires).
- La procédure de **restauration** doit être écrite dans le README et exécutée une fois pour
  de vrai. Une sauvegarde jamais restaurée n'est pas une sauvegarde.

### Image

- Multi-stage sur `mcr.microsoft.com/dotnet/aspnet:10.0` en runtime.
- `USER $APP_UID` : pas de root dans le conteneur.
- `ASPNETCORE_URLS=http://+:8080`. Pas de TLS dans le conteneur, pas de certificat.
- **Ne pas appeler `UseHttpsRedirection()` ni `UseHsts()`.** Traefik termine TLS et gère la
  redirection ; les activer ici provoque au mieux une redirection en boucle, au pire des
  URL générées en `http` sur un port interne.
- `HEALTHCHECK` sur `/healthz`, pour que `depends_on: condition: service_healthy` fonctionne.
- Arrêt propre : le service doit terminer le job HTTP en cours sur `SIGTERM`. Prévoir une
  marge de `stop_grace_period` supérieure au `ShutdownTimeout` d'ASP.NET.

### En-têtes de proxy — le piège à ne pas manquer

`ForwardedHeadersOptions` doit traiter `XForwardedFor` et `XForwardedProto`, et il faut
**vider `KnownNetworks` et `KnownProxies`**. En Docker l'IP de Traefik est celle du réseau
bridge, elle change à chaque recréation, et la liste blanche par défaut d'ASP.NET rejette
alors silencieusement les en-têtes : les cookies `Secure` cessent de fonctionner et
l'application se croit en clair. C'est une panne classique et pénible à diagnostiquer.

Le rate limiting authentifié reste **clé par clé API**. Le limiteur par IP du §12 dépend
entièrement de cette configuration : sans en-têtes de transfert valides, toutes les requêtes
paraissent venir de Traefik et le compteur par IP plafonnerait tout le monde ensemble.

### Réseaux

Deux réseaux Docker, et c'est structurant :

- `traefik-public` — le service seul y est attaché, avec ses labels de routage.
- `hopper-internal` — le service et PostgreSQL. **Postgres n'est jamais sur le réseau
  public et ne publie aucun port.**

### Labels Traefik

Un router pour l'API, un second pour `/admin` afin de pouvoir durcir ce dernier
indépendamment :

```yaml
labels:
  - traefik.enable=true
  - traefik.docker.network=traefik-public
  - traefik.http.services.hopper.loadbalancer.server.port=8080

  - traefik.http.routers.hopper-api.rule=Host(`hopper.exemple.ch`) && PathPrefix(`/api`)
  - traefik.http.routers.hopper-api.entrypoints=websecure
  - traefik.http.routers.hopper-api.tls.certresolver=letsencrypt

  - traefik.http.routers.hopper-admin.rule=Host(`hopper.exemple.ch`) && PathPrefix(`/admin`)
  - traefik.http.routers.hopper-admin.entrypoints=websecure
  - traefik.http.routers.hopper-admin.tls.certresolver=letsencrypt
  - traefik.http.routers.hopper-admin.middlewares=hopper-admin-allow
  - traefik.http.middlewares.hopper-admin-allow.ipallowlist.sourcerange=…
```

La liste d'IP autorisées sur `/admin` est une défense supplémentaire, pas un remplacement :
l'authentification par clé admin reste requise derrière.

### Migrations

DbUp s'exécute au démarrage, avant que le service accepte du trafic. Prendre un
`pg_advisory_lock` pendant la migration : inoffensif avec une seule instance, indispensable
le jour où le conteneur est recréé avant l'arrêt complet du précédent. Si la migration
échoue, le processus sort en code non nul — pas de démarrage en base incohérente.

### Variables d'environnement

`HOPPER_` comme préfixe. Au minimum : chaîne de connexion, `HOPPER_BOOTSTRAP_ADMIN_KEY`,
niveau de log. La clé d'amorçage apparaîtra dans `docker logs` si elle est générée
automatiquement — le README doit dire de la révoquer après avoir créé les vraies clés.

### Livrables

`Dockerfile`, `compose.yaml` complet (service + Postgres + les deux réseaux + labels), et
un `compose.override.yaml` de développement qui publie le port et monte le code à chaud.

---

## 14. Décisions arrêtées

Tout est tranché. Ne pas revenir sur ces points sans en parler :

- PostgreSQL 17 en conteneur dédié. Pas de partage avec n8n, qui reste sur SQLite.
- **L'API est publique sur internet.** n8n n'est qu'un producteur parmi d'autres, et rien
  dans le code ne doit le mentionner ni supposer son existence.
- .NET 10, conteneur derrière Traefik, aucun port publié sur l'hôte.
- **Un worker sert plusieurs files.** La sélection équitable du §5 est donc obligatoire, pas
  optionnelle, et le test 10 la vérifie.
- Aucun callback HTTP sortant. Les producteurs interrogent `GET /jobs/{id}`.
- Aucun test de charge. Les tests de concurrence du §9 sont ce qui compte.
- Un seul worker en pratique au démarrage, mais le design et les tests supposent qu'il puisse
  y en avoir plusieurs : ne prendre aucun raccourci qui supposerait l'unicité du worker.


