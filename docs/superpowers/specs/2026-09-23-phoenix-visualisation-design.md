# Rendre visible ce que Phoenix reçoit déjà

Design validé le 23 septembre 2026.

## 1. Le problème

Le harnais exporte déjà vers Phoenix des spans OpenInference bien formés :
hiérarchie session → tâche → tour → outil, `session.id`, `user.id`, les kinds
`AGENT` / `CHAIN` / `TOOL` / `GUARDRAIL`, et la coupure dure `CaptureContent`.
Le tuyau est bon. L'écran, lui, reste à moitié vide, pour trois raisons
distinctes :

1. **Les signaux sont des attributs, pas des annotations.** `TranscriptIngestor`
   écrit `signal.{clé}` et `signal.{clé}.why` sur le span de tâche. Phoenix les
   affiche dans le détail d'un span, mais ne sait ni les trier, ni les agréger,
   ni en faire une colonne. Dix-neuf mesures existent et aucune n'est comparable
   d'une session à l'autre.
2. **Aucun span `LLM` sur la voie des hooks.** `OI.Kind.Llm` est déclaré dans
   les conventions et n'est jamais utilisé. Les compteurs de jetons n'arrivent
   que par le rejeu différé des transcripts.
3. **La campagne d'évaluation n'existe pas dans Phoenix.** `coachingia evaluer`
   produit dix-huit évaluateurs déterministes et un juge, et tout finit dans un
   terminal.

## 2. Ce que l'on ne fait pas

Décidé explicitement, pour que personne n'ait à le redemander :

- **Pas de juge LLM sur les sessions de l'apprenant.** Le README l'écrit
  (« volontairement absent du coaching »), et `Verdict` le grave (« un verdict
  d'évaluation ne parle jamais à l'apprenant »). Les annotations de coaching
  sont des **mesures déterministes**, postées en `annotator_kind: CODE`. Le juge
  garde sa place : il mesure l'outil, dans l'autre projet.
- **Pas de client Python.** Les signaux et les verdicts sont déjà en C#. Faire
  transiter du C# vers Python pour parler à un serveur local est un aller-retour
  sans contrepartie, et mettrait une frontière de langage sur le chemin chaud.
- **Pas de persistance des span ids.** Le harnais connaît `Activity.SpanId` au
  moment où il ferme le span ; il annote dans la foulée.
- **Pas de migration vers Langfuse.** Tout le code tague en OpenInference, qui
  est la convention native de Phoenix.
- **Pas de labels inventés.** Voir §4.1.

## 3. Unité 1 — `PhoenixClient`

Nouveau, dans `CoachingIA.Harness.Core`. Une seule responsabilité : parler HTTP
à Phoenix. Il ne sait rien des signaux ni des verdicts.

```csharp
Task AnnotateAsync(IReadOnlyList<SpanAnnotation> annotations, CancellationToken ct);
Task<string> UpsertDatasetAsync(string nom, IReadOnlyList<DatasetExample> exemples, CancellationToken ct);
Task<string> CreateExperimentAsync(string datasetId, string nom, CancellationToken ct);
Task<string> CreateRunAsync(string experimentId, ExperimentRun run, CancellationToken ct);
Task FlushAsync(CancellationToken ct);
```

Ces cinq méthodes sont portées par une interface `IPhoenixClient`. L'interface
n'est pas du confort : le harnais de tests est une fermeture synchrone, sans
framework ni conteneur d'injection, et sans elle les unités 2 et 3 ne peuvent
pas s'éprouver contre un faux client. `FlushAsync` existe pour la même famille
de raisons que §3.1 — vider la file avant de rendre la main, là où l'appelant
sait qu'il a fini.

`HttpClient` et rien d'autre — aucune dépendance nouvelle. Contrat confirmé sur
la documentation Phoenix :

```
POST /v1/span_annotations
{ "data": [ { "name", "annotator_kind", "span_id",
              "result": { "label", "score", "explanation" },
              "metadata": {}, "identifier": "" } ] }
```

Trois points portent le design :

**`annotator_kind` vaut `CODE`.** C'est une valeur admise de l'énuméré, à côté
de `LLM` et `HUMAN`. Elle dit exactement ce qu'on fait : du calcul, pas du
jugement.

**`identifier` fait l'upsert.** Renseigné, il met à jour l'annotation
existante au lieu d'en créer une seconde. Rejouer une ingestion ne duplique donc
rien — propriété nécessaire, puisque `TranscriptIngestor` est fait pour être
relancé.

**Une file d'attente, et la règle d'or du harnais.** Les envois passent par un
`Channel` consommé en arrière-plan, avec un nombre borné de tentatives et un
recul exponentiel. Aucune exception ne remonte à l'appelant. C'est la règle déjà
écrite dans `Program.cs` : *une trace perdue est un incident mineur ; un tour
bloqué ne l'est pas.* Elle vaut mot pour mot pour les annotations.

### 3.1 La course à lever

Une annotation postée avant que Phoenix ait ingéré le span porte un `span_id`
que le serveur ne connaît pas encore. Le comportement de Phoenix dans ce cas
n'est **pas vérifié** — il peut accepter et rattacher plus tard, ou rejeter.

Mitigation retenue : poster après le vidage de l'exporteur OTLP, et laisser le
retry absorber le reste. Un court spike sur un Phoenix local tranchera le point
avant d'écrire la logique de retry, parce que la réponse change le design : si
le serveur rejette, il faut un délai d'attente ; s'il accepte, le retry suffit.

## 4. Unité 2 — Projet `coachingia` (les sessions)

### 4.1 Les signaux deviennent des annotations

La source existe : `SignalExtractor.ForTask` est déjà appelé en
`TranscriptIngestor.cs:74`, et rend des `Signal(Key, Level, Value, Evidence)`.
Dix-neuf clés aujourd'hui, d'`autonomy_ratio` à `verification_present`.

La projection, pour chaque signal, sur le span de tâche :

| Champ | Valeur |
|---|---|
| `name` | `signal.Key` |
| `annotator_kind` | `CODE` |
| `span_id` | le span de tâche, en hexadécimal sans préfixe |
| `result.score` | `signal.Value`, **omis si NaN** |
| `result.explanation` | `signal.Evidence` |
| `result.label` | **absent** — voir ci-dessous |
| `metadata` | `{ level, source: "transcript" }` |
| `identifier` | `signal.Key` |

**Le NaN reste un NaN.** `TranscriptIngestor.cs:76-78` porte déjà le commentaire
juste : *un signal indéterminé n'est pas un zéro*. Une valeur NaN produit donc
une annotation sans score mais avec son explication, et non une annotation à
zéro qui mentirait dans toutes les moyennes.

**Pas de label, et c'est délibéré.** Le cours recommande des rails de labels
discrets plutôt qu'un score libre, et il a raison. Mais les signaux sont des
ratios continus, et aucun seuil validé n'existe pour la plupart d'entre eux.
Inventer des bandes vert/orange/rouge produirait des couleurs qui ont l'air
d'un jugement sans en avoir la base. Les défis, eux, portent déjà un seuil
déclaré : c'est de là que viendront les labels, dans une itération ultérieure,
quand ils seront adossés à quelque chose.

Les attributs `signal.{clé}` restent en place. L'annotation ne les remplace pas :
elle rend agrégeable ce qui n'était que consultable.

### 4.2 De vrais spans `LLM` sur la voie des hooks

`transcript_path` est déjà dans le contrat `HookEvent`. Au hook `Stop`, le
harnais lit la queue du transcript et émet un span enfant `OI.Kind.Llm` sous le
span de tour, portant `llm.model_name` et les `llm.token_count.*` que
`TranscriptIngestor` sait déjà calculer.

Il **réutilise `TranscriptReader` et `SessionBuilder`** ; il ne parse rien
lui-même. C'est la règle que `RunExporter` vient d'appliquer, et pour la même
raison : un second lecteur du JSONL serait un second endroit à réparer le jour
où le format bouge.

Coût : une lecture de fichier à chaque fin de tour. La lecture part du dernier
décalage connu pour la session plutôt que du début du fichier.

## 5. Unité 3 — Projet `coachingia-evals` (l'outil)

Un second projet Phoenix, alimenté par `coachingia evaluer --phoenix`. Rien ne
change sans ce drapeau.

- **Dataset** — le jeu d'épreuves. Une `DatasetExample` par `Epreuve` : `input`
  depuis `Entree`, `output` depuis `Attendu`, et en métadonnées la famille,
  l'intitulé, l'origine et les étiquettes.
- **Experiment** — une campagne.
- **Run** — une épreuve jouée ; sa sortie est `Production.Texte`. Le run se
  rattache à l'exemple par l'identifiant **que Phoenix a attribué** à cet
  exemple, pas par `Epreuve.Id`.
- **Évaluations de run** — un `Verdict` par évaluation : `label` depuis
  `Etiquette`, `score` depuis `Score` (omis si `Indecis`), `explanation` depuis
  `Explication`, et `Preuve` en métadonnée pour rester surlignable.

**`annotator_kind` suit `IEvaluateur.Deterministe`** : `CODE` pour les dix-huit
évaluateurs du code, `LLM` pour ceux qui s'adossent au juge. La distinction
reste donc lisible dans l'UI, et elle reste ce qu'elle est dans le code — le
juge n'entre pas dans le verdict approuvé, la porte demeure déterministe.

### 5.0 Correction du 23 septembre : un run n'est pas un span

La première version de cette section parlait d'« annotations sur les runs » en
supposant qu'on les poserait par `POST /v1/span_annotations`, comme les
signaux. C'était faux, et la tâche 05 du chantier l'a établi contre un Phoenix
20.16.0 réel avant d'écrire une ligne :

- **Un run d'expérience n'est pas un span.** Sa réponse de création porte
  `trace_id: null` ; poster une annotation de span sur son identifiant rend
  `404 Spans with IDs … do not exist`. L'API a une route dédiée,
  `POST /v1/experiment_evaluations`, qui exige `experiment_run_id`, `name`,
  `annotator_kind`, `start_time` et `end_time`.
- **Un run exige l'identifiant Phoenix de l'exemple**, un GlobalID encodé en
  base64. L'upload d'un dataset ne le renvoie pas — seulement `dataset_id`,
  `version_id` et des compteurs. Seul `GET /v1/datasets/{id}/examples` le
  donne, dans l'ordre d'upload. Un identifiant local comme `Epreuve.Id` rend
  un `500`.

Le socle avait implémenté fidèlement une spec fausse. Écrire la publication
par-dessus aurait donné une suite verte contre un faux client et une
fonctionnalité cassée à 100 % contre le vrai — chaque run en échec, rattrapé
en silence par la règle qui interdit à la publication de faire échouer la
campagne.

Trois méthodes s'ajoutent donc au client, **sur une interface séparée,
`IPhoenixExperiences`**, et non sur `IPhoenixClient` :

```csharp
Task<string?> TrouverDatasetAsync(string nom, CancellationToken ct);
Task<IReadOnlyList<ExemplePhoenix>> ListerExemplesAsync(string datasetId, CancellationToken ct);
Task EvaluerRunAsync(RunEvaluation evaluation, CancellationToken ct);
```

`ExemplePhoenix` porte l'identifiant Phoenix de l'exemple, ses métadonnées et
sa date de mise à jour (`updated_at`, présent dans la réponse du serveur).
`TrouverDatasetAsync` interroge `GET /v1/datasets?name=` : le filtre par nom
existe côté serveur, ce qui dispense de parcourir une liste paginée.

**Le rattachement se fait par identifiant d'épreuve, pas par rang.** Chaque
exemple porte en métadonnées `epreuve_id` et `empreinte` ; un run va à
l'exemple dont le couple correspond. L'ordre dans lequel Phoenix rend les
exemples est un comportement observé, pas documenté : rien n'en dépend.

**Une épreuve déjà présente n'est pas renvoyée.** « Déjà présente » veut dire
même identifiant *et* même empreinte — SHA-256 de la forme publiée, calculée
par la publication. Avec l'identifiant seul, une épreuve corrigée garderait son
ancien contenu dans Phoenix et ses runs iraient à un exemple faux. Avec
l'empreinte, une correction monte comme nouvelle version, et un jeu inchangé ne
fait grossir le dataset d'aucun exemple : la publication sert à chaque
campagne, pas seulement à la première.

L'interface séparée n'est pas un détail de style. `IPhoenixClient` a déjà des
implémentations factices dans les suites de tests d'autres tâches ; y ajouter
deux membres les empêcherait de compiler au moment de la fusion. Et la
séparation correspond à une vraie frontière : `IPhoenixClient` porte le chemin
chaud, qui ne lève jamais ; la publication d'une campagne est un traitement
hors ligne, dont l'appelant rattrape les échecs.

### 5.1 Une épreuve qui cite du réel ne monte pas telle quelle

`Epreuve` porte `Partageable` et `CiteDuReel`, et distingue déjà les cas qui
citent de vrais prompts. Ces cas montent dans le dataset **sans leur contenu**,
réduits à leur identifiant et à leur intitulé. Phoenix tourne en local, donc le
risque est faible — mais le dépôt a écrit cette règle, et une exception ouverte
« parce que c'est local » est une exception qu'on oublie le jour où ça ne l'est
plus.

## 6. Réglages

Trois clés nouvelles sous `Harness` dans `appsettings.json` :

| Clé | Défaut | Rôle |
|---|---|---|
| `PhoenixBaseUrl` | `http://localhost:6006` | L'API REST. Distincte d'`OtlpEndpoint`, qui reste en gRPC sur 4317. |
| `PushAnnotations` | `true` | À `false`, plus aucune annotation ne part. Les spans continuent. |
| `AnnotationMaxRetries` | `3` | Après quoi l'annotation est abandonnée, avec un log en avertissement. |

`CaptureContent` garde son autorité : à `false`, aucune `explanation` ne peut
contenir de texte de prompt. Les `Evidence` des signaux sont des constats
calculés, pas des extraits — à vérifier signal par signal au moment de
l'implémentation, et à tronquer via `MaxValueChars` en cas de doute.

## 7. Épingler l'image Phoenix

`docker-compose.yml` tire `arizephoenix/phoenix:latest`. On code désormais
contre un contrat REST précis : une image flottante peut le faire bouger sous
nos pieds sans qu'aucun test ne le dise. L'image passe donc à une version
explicite, dans le même esprit que le README qui demande déjà de figer les
versions OpenTelemetry résolues.

## 8. Tests

- **`PhoenixClient`** — un `HttpMessageHandler` de test vérifie la forme exacte
  du corps JSON, la présence de l'`identifier`, et surtout qu'une erreur
  serveur ne remonte jamais en exception.
- **Signaux** — sur un transcript de référence, le nombre d'annotations
  attendues, et l'absence de score sur un signal NaN.
- **Spans `LLM`** — les compteurs de jetons sont présents sur le span émis au
  `Stop`.
- **Évaluations** — une campagne produit dataset, experiment et runs ; un
  verdict de juge porte `LLM`, un verdict de code porte `CODE` ; une épreuve
  non partageable monte sans son contenu.
- **Intégration** — une passe contre un vrai Phoenix, ignorée si `:6006` ne
  répond pas, pour que la suite reste exécutable sans Docker.

Le tout s'ajoute au harnais de tests existant, qui est une fermeture synchrone
et le reste.

## 9. Risques

1. **La course annotation / ingestion** (§3.1). Le seul risque qui peut changer
   le design. Un spike le lève avant d'écrire le retry.
2. **La dérive de l'API Phoenix.** Levée par l'épinglage de l'image (§7).
3. **Le volume.** Dix-neuf signaux par tâche, plus les verdicts. Les envois sont
   groupés en lots — `data` est un tableau — et non postés un par un.

## 10. Ordre de réalisation

1. `PhoenixClient` et ses tests — le socle, dont tout le reste dépend.
2. Les signaux en annotations — le gain visible le plus rapide.
3. Les spans `LLM` sur la voie des hooks.
4. Datasets et experiments.

Les étapes 2, 3 et 4 sont indépendantes entre elles une fois le socle posé.
