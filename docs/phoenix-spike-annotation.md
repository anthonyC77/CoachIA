# Spike — annotation sur un span pas encore ingéré

Question posée par la spec (§3.1) : que fait Phoenix quand on lui poste une
annotation sur un `span_id` qu'il ne connaît pas encore ? La réponse change le
design de la reprise du `PhoenixClient`.

## Conditions du spike

Phoenix local démarré avec `docker compose -f docker/docker-compose.yml up -d`
(image `arizephoenix/phoenix:latest`, tirée le jour du spike — le pull a réussi
après plusieurs tentatives, le réseau du poste ayant coupé les premiers essais).
UI et API REST sur `http://localhost:6006`, version serveur `20.16.0`
(en-tête `x-phoenix-server-version`).

Le harnais (`dotnet run --project src/CoachingIA.Harness`) a servi à produire
un span réel exporté en OTLP/gRPC vers Phoenix, pour disposer d'un `span_id`
connu à comparer à un `span_id` inventé (`deadbeefdeadbeef`).

## Résultat mesuré

`POST /v1/span_annotations` accepte un paramètre de requête `sync`
(`false` par défaut, documenté dans l'OpenAPI de Phoenix comme « fulfill
request synchronously »). Le comportement diffère radicalement selon sa valeur :

**Sans `sync` (défaut, traitement asynchrone) :**
- span inconnu (`deadbeefdeadbeef`) : `200 OK`, corps `{"data":[]}`.
- span réel et connu : `200 OK`, corps `{"data":[]}` **aussi**.

Le corps ne distingue donc pas le succès de l'échec dans ce mode : il est
toujours vide, que l'annotation ait été acceptée ou silencieusement perdue.
Vérification faite via `GET /v1/projects/{projet}/span_annotations?span_ids=…`
après coup : l'annotation postée sur le span réel a bien été créée ; celle
postée sur le span inventé n'existe pas, ni immédiatement ni après quelques
secondes. **Phoenix ne la met pas en attente pour la rattacher plus tard : elle
est perdue au moment de l'appel, sans le dire.**

**Avec `sync=true` (traitement synchrone) :**
- span inconnu : `404 Not Found`, corps `Spans with IDs deadbeefdeadbeef do not exist.`
- span réel et connu : `200 OK`, corps `{"data":[{"id":"…"}]}` — la ou les
  annotations effectivement créées.

Ce mode donne donc un signal exploitable : un 404 dit sans ambiguïté que le
span n'est pas encore ingéré, un 200 avec `data` non vide confirme la création.

## Conséquence retenue sur la logique de reprise

`PhoenixClient` poste avec `?sync=true`. Le coût (la requête attend que
Phoenix ait traité l'annotation avant de répondre) est sans conséquence pour
l'appelant : les envois passent par une file consommée en arrière-plan, donc
rien de bloquant ne remonte à `AnnotateAsync`, qui ne fait qu'empiler dans le
`Channel`.

Sur cette base :
- un `404` est traité comme un échec **à retenter** (span pas encore ingéré :
  c'est exactement la course visée par §3.1) ;
- toute autre réponse non-2xx, ou une exception réseau, est traitée de la même
  façon — retenue et retentée ;
- au bout d'`AnnotationMaxRetries` tentatives supplémentaires, l'annotation est
  abandonnée avec un log en avertissement, sans jamais remonter d'exception à
  l'appelant.

Le mode par défaut (`sync=false`) a été délibérément écarté pour l'usage du
client : son `200` ne certifie rien, ce qui aurait rendu toute logique de
reprise aveugle — elle n'aurait jamais pu détecter la perte silencieuse
constatée ci-dessus. `sync=true` est le seul mode qui rend le retry du §3.1
capable de reconnaître le cas qu'il doit précisément rattraper.
