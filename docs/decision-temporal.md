# Décision : Temporal comme moteur d'exécution durable

## Contexte

CoachingIA lit les transcripts JSONL de Claude Code et produit notamment un bilan
hebdomadaire (`coachingia bilan`), aujourd'hui planifié par le Planificateur de tâches
Windows via `scripts/bilan-hebdo.ps1`. Ce déclenchement est simple mais fragile : une
panne en cours d'exécution (poste éteint, process tué, erreur transitoire) n'a pas de
reprise — il faut relancer le bilan depuis le début, sans savoir précisément où il s'est
arrêté. À mesure que le produit ajoute des étapes plus longues (hooks temps réel,
campagne d'évaluation), ce manque de reprise devient plus coûteux.

## Décision

CoachingIA adopte Temporal comme moteur d'exécution durable pour ses processus de fond.
Un serveur de développement Temporal local, lié à `127.0.0.1` uniquement, tourne sur le
poste et conserve son historique dans une base SQLite persistante sous
`%LOCALAPPDATA%\CoachingIA\temporal`. Il est démarré par `scripts/temporal-local.ps1`, qui
n'accepte jamais de le lancer sans `--db-filename` : la base en mémoire proposée par défaut
par `temporal server start-dev` est explicitement exclue, puisqu'elle perdrait l'historique
à l'arrêt du process — l'inverse de ce que cette décision cherche à obtenir.

## Où Temporal sert

- **Phase 1 — le bilan hebdomadaire.** C'est le premier usage : `coachingia bilan` devient
  un workflow Temporal, dont chaque étape coûteuse ou faillible (lecture des transcripts,
  segmentation, écriture de l'archive) est une activity reprise indépendamment en cas de
  panne, au lieu de tout relancer depuis zéro.
- **Plus tard — les hooks temps réel.** Les événements reçus par `CoachingIA.Harness`
  pourront déclencher des workflows Temporal pour leur traitement différé, sans bloquer la
  réponse HTTP au hook.
- **Plus tard — la campagne d'évaluation.** L'exécution des épreuves de la campagne, plus
  longue et plus coûteuse que le bilan, bénéficie de la même reprise après panne.

## Ce que Temporal ne fait pas

Temporal n'est **ni la mémoire long terme du coaching, ni la persistance SQLite prévue par
le produit**. L'historique Temporal est un journal d'exécution — il retient qu'une étape a
eu lieu et avec quel résultat technique — et non une base de données métier interrogée par
le reste de l'application. La mémoire du coaching (sessions, signaux, bilans archivés) reste
portée par ses propres mécanismes (`ReviewArchive`, fichiers sous `bilans/`), aujourd'hui et
après l'arrivée de la persistance SQLite du produit, qui est un composant distinct.

## Vie privée de l'historique

**Aucun texte de prompt ni contenu de transcript n'entre dans l'historique Temporal** —
ni dans les entrées et sorties de workflow, ni dans celles d'activity, ni dans les messages
d'erreur. Seuls y circulent des chemins de fichiers, des identifiants (semaine ISO, id de
session, id de workflow) et des empreintes. Les contenus eux-mêmes transitent par des
fichiers intermédiaires locaux dont seul le chemin est passé aux workflows et activities :
Temporal ne voit jamais le texte, seulement où il se trouve. Cette règle prolonge la coupure
déjà en vigueur pour OpenTelemetry (`CaptureContent: false`) : elle ne se relâche jamais pour
un débogage ponctuel.

## Idempotence des activities

Temporal garantit une exécution **au-moins-une-fois** de chaque activity : en cas de panne
après l'exécution mais avant l'enregistrement du résultat, l'activity est rejouée. Chaque
activity doit donc produire le même effet qu'elle soit exécutée une fois ou plusieurs fois.
En pratique : écriture atomique d'un fichier au même chemin plutôt qu'un ajout, et
`ReviewArchive.Write`, qui ne réécrit pas un bilan identique, sert de modèle pour toute
nouvelle activity d'écriture.

## Déterminisme des workflows

Le code d'un workflow Temporal est rejoué (replay) pour reconstruire son état : il doit donc
être déterministe. Sont **interdits** dans le code d'un workflow, entre autres :
`DateTime.Now` / `DateTime.UtcNow`, `Task.Run`, `Random`, `Guid.NewGuid`, ainsi que toute
entrée-sortie fichier ou réseau directe. À la place : `Workflow.UtcNow` pour l'heure,
`Workflow.Random` pour l'aléatoire, et toute entrée-sortie déléguée à une activity, seule
autorisée à toucher le disque ou le réseau.

## Exploitation locale

Le serveur Temporal utilisé par CoachingIA est un serveur de développement local, lancé par
`scripts/temporal-local.ps1` :

- lié à `127.0.0.1` seulement (jamais exposé sur le réseau) ;
- base SQLite persistante sous `%LOCALAPPDATA%\CoachingIA\temporal` (jamais en mémoire) ;
- ports par défaut : 7233 (gRPC, utilisé par le SDK) et 8233 (interface web locale).

Ce n'est ni un service Windows, ni une tâche planifiée : il se démarre à la demande, comme
un prérequis local, au même titre que Docker pour la voie OpenTelemetry/Phoenix.

## Conséquences

- Le bilan hebdomadaire (et, plus tard, les hooks et la campagne) devient reprenable après
  panne, au prix d'une dépendance de fonctionnement supplémentaire : le serveur Temporal
  local doit tourner pour que ces workflows s'exécutent.
- Le code des workflows doit respecter la discipline du déterminisme, ce qui déplace
  davantage de logique dans des activities dédiées.
- **Alternatives écartées** :
  - **Planificateur de tâches Windows seul** (état actuel) : suffisant pour déclencher, pas
    pour reprendre après panne ni pour composer plusieurs étapes faillibles.
  - **Hangfire / Quartz** : apportent une reprise sur erreur au niveau de la tâche, mais pas
    le replay déterministe ni l'historique d'exécution complet d'un workflow Temporal ; ils
    ajouteraient en plus une dépendance à une base de données pour leur propre état.
  - **Rien (statu quo)** : écarté, puisque c'est précisément l'absence de reprise qui motive
    cette décision.
