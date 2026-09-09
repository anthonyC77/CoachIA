# Chantier : quatre agents pour paralléliser une tâche sur des branches

Date : 2026-08-30
Statut : design validé, prêt pour plan d'implémentation

## Objectif

Prendre un objectif de développement, le découper en tâches fines et indépendantes,
les traiter en parallèle chacune dans son propre worktree git, vérifier chaque
résultat contre sa spec, et pousser une branche par tâche validée. Les fusions sur
la branche principale restent manuelles et à la charge de l'utilisateur.

Le système est **générique** : il vit dans `~/.claude/` et ne suppose ni .NET ni
CoachingIA. La détection de la stack est faite au moment du découpage.

## Principe directeur

Le découpage se fait **par frontière de fichiers**, pas par étape logique. Chaque
tâche déclare les fichiers qu'elle possède ; deux tâches dont les fichiers se
croisent ne sont jamais exécutées dans la même vague. C'est cette contrainte qui
rend les fusions manuelles tenables : des branches qui ne se recouvrent pas.

## Topologie

L'orchestrateur est **la session principale**, pas un agent. Un sous-agent ne peut
pas piloter fiablement d'autres sous-agents ; le planificateur produit donc un plan,
il ne lance pas les exécuteurs.

La session principale crée elle-même les worktrees et passe le **chemin absolu** aux
trois agents d'exécution. Elle connaît à tout moment la branche, le chemin et l'état
de chaque tâche.

## Artefacts

Tout vit sous `<repo>/.chantiers/`, exclu du versionnement via `.git/info/exclude`
(fichier local, non versionné) : **aucun fichier versionné du repo cible n'est
modifié par le système**.

```
.chantiers/<slug>/plan.json      contrat machine, lu par l'orchestrateur
.chantiers/<slug>/plan.md        même contenu, lisible par l'utilisateur
.chantiers/<slug>/rapports/      verdicts de relecture, un fichier par tâche et par tour
.chantiers/wt/<slug>/<id>/       les worktrees, un par tâche
```

Les worktrees sont **dans** le repo plutôt que dans un répertoire frère : cela
garantit qu'ils restent sous un répertoire de travail autorisé, sans quoi chaque
appel d'agent déclencherait une demande de permission.

Contrepartie documentée : un `git clean -xdf` dans l'arbre principal effacerait des
worktrees non fusionnés. La commande `/chantier` l'affiche en garde-fou.

### Schéma de `plan.json`

```json
{
  "chantier": "<slug>",
  "objectif": "<l'objectif tel que formulé par l'utilisateur>",
  "racine": "<chemin absolu du repo>",
  "base": "<branche de base, main par défaut>",
  "verification": "<commande de vérification du projet, détectée>",
  "vagues": [
    {
      "numero": 1,
      "taches": [
        {
          "id": "01-nom-court",
          "titre": "<une ligne>",
          "branche": "chantier/<slug>/01-nom-court",
          "worktree": "<racine>/.chantiers/wt/<slug>/01-nom-court",
          "objectif": "<ce qu'il faut obtenir, autoportant>",
          "contexte": "<ce qu'il faut savoir du repo pour le faire, autoportant>",
          "fichiers": ["<chemins relatifs, créés ou modifiés, exhaustif>"],
          "criteres": ["<critères d'acceptation vérifiables>"],
          "verification": "<commande à lancer pour prouver la tâche>",
          "hors_perimetre": ["<ce qu'il ne faut surtout pas faire ici>"]
        }
      ]
    }
  ]
}
```

`fichiers` est le champ porteur : c'est lui qui définit la composition des vagues.

Les deux champs `verification` ne sont pas redondants : celui de la racine est la
commande de vérification du projet entier, détectée une fois ; celui d'une tâche est
la commande que l'exécuteur et le relecteur lancent pour prouver *cette* tâche. Elle
peut être plus ciblée que celle du projet, mais jamais plus permissive.

## Les quatre agents

| Agent | Modèle | Outils | Interdits |
|---|---|---|---|
| `chantier-planificateur` | Opus 5 | Read, Grep, Glob, Bash, Write | ne code pas, ne crée ni branche ni worktree |
| `tache-executeur` | Sonnet 5 | tout sauf Agent | ne commit pas, ne push pas |
| `spec-relecteur` | Sonnet 5 | Read, Grep, Glob, Bash | pas d'Edit/Write : il juge, il ne corrige pas |
| `branche-pousseur` | Haiku 4.5 | Bash | ne fusionne jamais, ne force jamais, ne touche jamais la base |

### Trois choix de contrat

**L'exécuteur laisse l'arbre sale.** Il code et lance la vérification, mais ne commit
rien. Le relecteur juge un arbre de travail ; le pousseur commit après verdict
favorable. Conséquence : une tâche refusée ne laisse aucun commit derrière elle, et
une branche poussée est par construction une branche validée.

**Le relecteur n'a pas le droit d'écrire.** S'il pouvait corriger, il jugerait son
propre travail et le signal serait perdu. Il rend un verdict et des findings ; c'est
l'exécuteur d'origine qui corrige.

**Le planificateur détecte la stack.** Les agents étant globaux, aucun ne suppose une
technologie. Le planificateur inspecte le repo et inscrit la commande de vérification
dans le plan ; les trois autres l'exécutent sans la connaître.

### `chantier-planificateur` (Opus 5)

Entrée : objectif, chemin racine du repo, slug, branche de base, chemin de sortie.

Sortie : `plan.json` et `plan.md` écrits sur disque ; rapport final = chemin du plan
plus un résumé vagues/tâches.

Règles de découpage :

1. Une tâche est un livrable testable seul, de l'ordre de quelques fichiers —
   ni un sous-système entier, ni une retouche d'une ligne.
2. `fichiers` est exhaustif : tout fichier créé ou modifié y figure.
3. Deux tâches d'une même vague ont une intersection de `fichiers` vide. Une
   dépendance entre tâches se traduit par une vague ultérieure, jamais par un ordre
   à l'intérieur d'une vague.
4. Les critères d'acceptation sont **observables** : un test passe, une commande
   produit telle sortie, un fichier contient telle chose. Sont proscrits « propre »,
   « idiomatique », « bien testé ».
5. La spec de tâche est **autoportante** : le relecteur ne verra ni le plan global,
   ni la conversation. Tout ce qui est nécessaire pour juger figure dans la tâche.
6. Pas de tâche transversale (renommage global, reformatage) : elle tuerait le
   parallélisme. Soit elle est seule dans sa vague, soit elle sort du chantier.
7. Le planificateur détecte la stack et la commande de vérification du projet, et
   les inscrit dans le plan.
8. `hors_perimetre` est renseigné dès qu'un débordement est plausible.
9. Un chantier vise 3 à 8 tâches. Au-delà, le planificateur ne découpe pas plus
   fin : il signale que l'objectif est trop large et propose de le scinder en
   plusieurs chantiers.

### `tache-executeur` (Sonnet 5)

Entrée : chemin absolu du worktree, spec de tâche complète, et — lors d'un tour de
correction — les findings du relecteur.

Règles :

1. Tous les chemins manipulés sont sous le worktree. Aucune écriture en dehors.
   Aucune commande git ciblant le repo principal.
2. Ne modifier que les fichiers listés dans `fichiers`. Si un autre fichier doit
   changer, s'arrêter et rapporter `BLOQUE` avec la raison — c'est le plan qui est
   en défaut, pas l'exécution.
3. Interdits : `commit`, `push`, `checkout` d'une autre branche, `merge`, `rebase`.
   `status` et `diff` sont autorisés.
4. Lancer la commande de vérification avant de rendre et coller sa sortie verbatim.
5. Suivre les conventions du code environnant.

Rapport final structuré : état (`TERMINE` ou `BLOQUE`), fichiers touchés, ce qui a
été fait critère par critère, sortie verbatim de la vérification.

### `spec-relecteur` (Sonnet 5)

Entrée : chemin du worktree, spec de tâche.

Procédure :

1. `git -C <wt> status --porcelain` pour établir la liste réelle des fichiers touchés.
2. Comparer à `fichiers` : tout débordement de périmètre est un `NON_CONFORME`,
   parce que c'est précisément ce qui casserait les fusions.
3. `git -C <wt> diff` et lecture des fichiers concernés.
4. Critère par critère : satisfait ou non, avec la preuve (fichier:ligne).
5. Lancer la commande de vérification et coller sa sortie verbatim.

Sortie : première ligne `VERDICT: CONFORME` ou `VERDICT: NON_CONFORME`, puis les
findings numérotés — fichier:ligne, critère visé, écart constaté, correction attendue.

Règle anti-complaisance : chaque verdict cite le critère d'acceptation qu'il applique.
Seuls trois motifs de refus sont recevables — écart à un critère, débordement de
périmètre, vérification en échec. Le relecteur n'invente pas de critère et ne produit
pas de remarque de goût.

### `branche-pousseur` (Haiku 4.5)

Séquence déterministe, aucune décision de jugement. Le message de commit est fourni
par l'orchestrateur.

1. `git -C <wt> rev-parse --abbrev-ref HEAD` doit valoir exactement la branche
   attendue, sinon `ABANDON`.
2. La branche ne doit être ni la base, ni `main`, ni `master`, sinon `ABANDON`.
3. `git -C <wt> status --porcelain` doit être non vide, sinon `ABANDON` (rien à
   pousser).
4. `git -C <wt> add -A`
5. `git -C <wt> commit` avec le message fourni, terminé par
   `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`
6. `git -C <wt> push -u origin <branche>`
7. Rapporter le SHA, le nom de branche et la sortie du push.

Interdits absolus : `--force`, `--no-verify`, `merge`, `rebase`, `reset`, `checkout`,
pousser vers une autre branche que celle attendue, toucher au repo principal.

## La commande `/chantier`

Invocation : `/chantier [--parallele N] [--base <branche>] <objectif>`

1. **Garde-fous** : on est dans un repo git, l'arbre principal est propre, `origin`
   existe. Sinon, arrêt avec le motif.
2. Dériver le slug de l'objectif, créer `.chantiers/<slug>/`, ajouter `/.chantiers/`
   à `.git/info/exclude` si absent.
3. Lancer `chantier-planificateur` → `plan.json`.
4. **Point d'arrêt** : présenter les vagues et les tâches à l'utilisateur, qui valide
   ou amende avant toute exécution.
5. **Validation mécanique du plan** : intersection des `fichiers` vide dans chaque
   vague, identifiants et branches uniques. Un plan qui échoue repart au
   planificateur avec le conflit constaté.
6. Pour chaque vague, dans l'ordre :
   - `git worktree add -b <branche> <chemin> <base>` pour chaque tâche.
   - Fan-out des `tache-executeur` en tâches de fond, plafond de **3** simultanés
     (`--parallele N` pour changer).
   - À la fin de chaque exécuteur, lancer `spec-relecteur` sur son worktree.
   - `NON_CONFORME` → renvoyer les findings à l'exécuteur d'origine via son contexte
     intact, puis refaire relire. **Deux tours de correction au maximum**, après quoi
     la tâche est marquée `BLOQUE`.
   - Un exécuteur qui rapporte `BLOQUE` n'est pas relancé : le plan est en défaut,
     la tâche remonte immédiatement à l'utilisateur.
   - `CONFORME` → `branche-pousseur`.
   - Chaque verdict est enregistré dans `.chantiers/<slug>/rapports/`.
7. **Tableau final** : tâche, verdict, branche, SHA, worktree ; puis la liste des
   tâches bloquées avec leur motif.
8. Les worktrees restent en place — ils sont nécessaires pour inspecter avant de
   fusionner. Les commandes de nettoyage sont affichées, jamais exécutées
   automatiquement.

## Modes de défaillance traités

- **Branche déjà existante** : `git worktree add -b` échoue, la vague est
  interrompue avant tout travail et le conflit est rapporté.
- **Commande de vérification absente ou en échec dès le départ** : détecté au
  moment du plan, signalé à l'utilisateur au point d'arrêt.
- **Push rejeté** : ne devrait pas survenir sur une branche neuve. Rapporté tel
  quel, sans aucune tentative de `--force`.
- **Agent qui ne rend jamais la main** : visible dans `/tasks`, l'utilisateur peut
  l'interrompre ; le worktree reste inspectable.

## Hors périmètre (v1)

Pas de fusion automatique, pas de création de PR, pas d'intégration CI, pas de
persistance au-delà des fichiers, pas de reprise automatique d'un chantier
interrompu (le `plan.json` et les worktrees restent sur disque, la reprise est
manuelle).

## Livrables

```
~/.claude/agents/chantier-planificateur.md
~/.claude/agents/tache-executeur.md
~/.claude/agents/spec-relecteur.md
~/.claude/agents/branche-pousseur.md
~/.claude/commands/chantier.md
```

Cinq fichiers Markdown, aucune ligne de code applicatif.

## Validation

Le système est validé par un chantier réel à faible enjeu sur CoachingIA, choisi
une fois les cinq fichiers écrits : le plan doit être exécutable, les branches
poussées doivent être fusionnables sans conflit entre elles, et un refus de
relecture doit être provoqué au moins une fois pour vérifier la boucle de
correction.
