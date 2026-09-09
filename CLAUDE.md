# CLAUDE.md

> La spécification du produit, c'est **`README.md`** : les commandes, les règles de
> conception du bilan, le système de lentilles, les garanties de vie privée. Long,
> en français, à lire en premier. Ce fichier n'en répète rien — il n'ajoute que ce
> qu'un agent doit savoir et qui n'y est pas.

CoachingIA lit les transcripts JSONL de Claude Code (`~/.claude/projects/`) et, en option,
des hooks HTTP temps réel, puis en tire des signaux de coaching : découpe en sessions et en
tâches, signaux par tâche, bilan hebdomadaire, rétrospective. Tout est local — rien ne quitte
le poste sauf les spans OTLP vers un Phoenix (Arize) local. L'outil **observe, il ne coache
pas encore** : pas de juge LLM, pas de persistance au-delà des fichiers. Les paliers 1-5
(`coaching.level`, `SignalSpec.Level`) sont décrits dans `docs/architecture-v0.*.html`.

## Commandes

.NET SDK 10. Docker seulement pour la voie OpenTelemetry/Phoenix.

- Générer : `dotnet build` — `TreatWarningsAsErrors` est actif pour toute la solution (`Directory.Build.props`), un avertissement fait échouer.
- Vérifier : `dotnet run --project tests/CoachingIA.Harness.Tests` — code 1 si une vérification échoue, `FAIL <libellé>` par échec. La campagne d'évaluation est une section de ce même harnais (bannière `évaluation`).
- Lancer : `dotnet run --project src/CoachingIA.Cli -- <commande>`. Sans argument, l'aide complète ; `probe --root <dir>` est le contrôle de fumée le plus rapide.
- Évaluer seul : `dotnet run --project src/CoachingIA.Cli -- evaluer` — même composition que la porte du harnais, codes 0/1/2 (2 = rien mesuré, jamais à lire comme un vert). `--juge` y ajoute l'avis de `claude -p`, qui informe sans rien garder.
- Bout en bout : Phoenix (`docker compose -f docker/docker-compose.yml up -d`), le harnais (`dotnet run --project src/CoachingIA.Harness`), puis `pwsh scripts/smoke-test.ps1`.
- Approuver un nouvel état de référence d'évaluation : `$env:COACHINGIA_APPROUVER_EVALS = "true"` puis relancer les tests. **Depuis un terminal humain seulement** — un hook le bloque depuis un agent.

## Architecture

Trois projets. `CoachingIA.Harness` désigne l'hôte web, pas un « harnais » au sens générique.

- **`Harness.Core`** — toute la logique, **zéro dépendance NuGet externe** (seule la référence de framework `Microsoft.AspNetCore.App`) : le cœur doit compiler et se tester hors ligne.
  - `Transcripts/` : `TranscriptReader` (JSONL tolérant, une ligne illisible se compte et n'est jamais fatale) → `SessionBuilder` → `TaskSegmenter` → `SignalExtractor`. `TranscriptIngestor` rejoue en spans **aux horodatages d'origine**. Le segmenteur est une heuristique que le README appelle « un pari » : il journalise ses décisions et se retouche.
  - `Coaching/` : tout ce qui suit les signaux — `SignalSpec`, les lentilles, `WeeklyReview`/`Retrospective` et leurs rendus, `ReviewArchive`, `PromptRubric`/`PromptCritic` (`IPromptCritic` bascule entre heuristique hors ligne et juge `claude -p`).
  - `Evaluation/` : la brique qui mesure l'outil (`Bareme`, `Verdict`, `Epreuve`, `Campagne`). Elle parle à qui maintient CoachingIA, **jamais à l'apprenant**. `CampagneStandard` est la composition — producteurs, porte, juges — prise au même endroit par le harnais et par `coachingia evaluer` ; `Porte.Juger` en tire le code de sortie (0 rien n'a bougé, 1 un écart, **2 rien mesuré**). `JugeReecriture` mesure l'accord entre un évaluateur du code et `claude -p` : `Deterministe = false`, donc hors de la porte et hors de `verdict.json`, et sur demande seulement.
  - `ClaudeCli.cs` : le seul endroit qui lance `claude -p`. Les deux tubes se lisent **en parallèle** de l'attente — les lire après coup bloquait l'enfant dès qu'il dépassait la taille d'un tube. Tout ce qui s'en sert reçoit un `IClaudeCli`, ce qui rend la chose éprouvable hors ligne.
- **`Harness`** — la voie temps réel : `POST /hooks/{eventName}`, `POST /ingest` (le rejeu, pour que les deux voies convergent sur le même modèle de span), `/health`, `/status`. `Routes.Map` est la table explicite segment d'URL → nom canonique ; certains noms divergent exprès (`post-tool-fail` → `PostToolUseFailure`), ne pas la dériver. **Un hook répond vite et répond 200** : il bloque le tour de l'utilisateur, une panne côté coaching ne doit jamais faire tomber sa session.
- **`Cli`** — l'exécutable `coachingia`, un script à une fonction locale `int Xxx()` par sous-commande. `WebConsole.cs` sert la console locale sous des garanties **à préserver, pas seulement à faire marcher** : liaison `127.0.0.1` seule, commandes et options en liste blanche, arguments passés en tableau (aucune chaîne shell à injecter), chemin servi réduit à son nom de fichier puis rejoint sous le dossier de sortie, jeton de session vérifié à chaque appel.

Hors code : `lenses/*.json`, `evals/` (le jeu d'épreuves et l'état approuvé), `skills/coach-starcraft2.md` (registre d'écriture des scènes). `_a_supprimer/` est une zone d'attente, pas de la source vivante.

## Règles de ce dépôt

- **Français** dans les commentaires, les chaînes visibles et les libellés. Les noms de types sont un mélange assumé (`SegmentedTask`, `Problematique`, `SceneIssueLevel { Erreur, Doute }`) : suivre le voisinage. Les commentaires disent **pourquoi**, pas quoi.
- **Il n'y a pas de framework de test.** `Program.cs` construit `Check(bool, string)` et appelle les suites l'une après l'autre ; une suite est `public static class XxxTests { public static void Run(Action<bool,string> check[, string lensDir]) }`, enregistrée par un appel littéral. **Le libellé est le critère** : une phrase française qui énonce la promesse et interpole la valeur obtenue. Lancer un test isolé demande de commenter les autres — c'est attendu, pas une lacune.
- **Aucune vérification ne lit `~/.claude/projects/`.** Les sessions se fabriquent : lire les transcripts réels rendrait la suite lente, non reproductible et différente sur chaque poste.
- **Une vérification rouge ne se supprime ni ne se contourne.** Elle se conteste par écrit : `TEST_CONTESTÉ: <libellé> — <raison>`. C'est un résultat valide.
- **`evals/cas/` et `evals/verdict.json` sont l'étalon** : en `deny`, plus un hook sur Bash. On ne les modifie pas pour faire passer une campagne ; un écart s'approuve, il ne s'efface pas.
- **`CaptureContent: false` est une coupure dure** : aucun texte de prompt ni de sortie d'outil ne part vers OpenTelemetry. Tout changement sur `SpanFactory` ou la lecture des payloads doit la préserver.
- **`ReviewArchive.Write` ne s'écrase pas silencieusement** : identique → aucun fichier touché ; changé → l'ancien part dans `bilans/archives/`, horodaté par **sa propre** date de modification. Ne pas « simplifier » en écriture directe.
- **Une lentille n'est qu'un habillage** : elle ne change jamais une mesure ni un seuil, la phrase factuelle précède l'image et reste vraie sans elle, et la lentille neutre reste complète puisque toutes les autres retombent dessus, clé par clé.
- **Les attributs `OI.*` (OpenInference) et `Coach.*` sont additifs** : on ne détourne jamais un nom OpenInference pour y mettre une donnée de coaching.
- `InvariantGlobalization` est actif : pas de `CultureInfo("fr-FR")` — les noms de mois et de jours sont des tableaux littéraux, et c'est pour ça.
- OpenTelemetry est épinglé en `1.*` flottant exprès. Après un `dotnet restore`, le README demande de figer les versions résolues : le signaler plutôt que le changer en silence.

## Agents

`testeur` pose l'oracle dans `tests/` et n'écrit aucune ligne de production ; `codeur` fait
passer les vérifications sans toucher `tests/` ni `evals/` ; `relecteur` relit le diff contre
les critères et ne modifie rien ; `evaluateur` joue la campagne et rapporte les écarts sans
rien corriger. Le découpage d'un objectif en tâches et son exécution en worktrees parallèles
sont déjà couverts par la commande **`chantier`**, installée au niveau utilisateur : on ne
double pas ce workflow ici. Résultat de sous-agent : **≤ 20 lignes** — fichiers touchés,
commande de vérification lancée, verdict.

## Ce qui n'est pas encore là

Côté coaching : le calcul des scores de palier, le contenu des skills pédagogiques, la
persistance SQLite, la boucle d'auto-apprentissage. Aucun juge ne parle à l'apprenant —
le seul qui existe mesure l'outil, pas la personne.
