# `_horus/` — la configuration d'origine, en quarantaine

Ces fichiers viennent d'un autre dépôt (**Horus** : RAG hybride, front Angular,
éval Python). Ils sont conservés pour référence et pour une réouverture
possible, **hors des dossiers que Claude Code inspecte** : rien de ce qui vit
ici n'est chargé.

```
_horus/
  claude/              agents, hooks (.sh), règles et skills d'origine
  eval/                la chaîne d'évaluation Python (oracles, sondes, tâches)
  src/Nexus.Tools/     les contrats d'outils typés, source des schémas générés
```

La disposition interne reproduit celle du dépôt d'origine — `eval/` et
`src/Nexus.Tools/` côte à côte sous une même racine. Ce n'est pas cosmétique :
`eval/tests/test_eval.py` et `gen_tool_schemas.py` résolvent leurs chemins
relativement à cette racine. Déplacer une pièce sans l'autre casse la suite.

Une seule adaptation a été faite au code importé : les sondes de garde-fou
cherchaient les hooks sous `PROJECT/.claude/hooks/`, elles les cherchent
maintenant sous `PROJECT/claude/`. Recréer un `.claude/` à l'intérieur de la
quarantaine aurait annulé la raison d'être de la quarantaine.

---

## Ce qui a été écarté, et pourquoi

| Fichier | Pourquoi |
|---|---|
| `claude/rules/angular.md` | il n'y a pas de front. Le dépôt est trois projets .NET plus un harnais de tests console. |
| `claude/rules/outils-types.md` | `RunPolicy`, quotas, clés d'idempotence, schémas d'outils générés depuis les types : rien de tout cela n'existe. Les `ToolSpec` de `CorpusTools` sont écrites à la main. |
| `claude/agents/juge-taches.md` | il n'y a ni orchestrateur, ni contrat de run public, ni runs à juger. Le juge LLM est prévu à l'étape 5 du plan d'évaluation, pas avant. |
| `claude/agents/eval-retrieval.md` | il n'y a pas de retrieval. Remplacé par `.claude/agents/evaluateur.md`, qui joue la vraie campagne C#. |
| `claude/skills/eval-retrieval`, `claude/skills/eval-taches` | mêmes raisons. |
| `claude/skills/cadrer`, `claude/skills/livrer` | doublons de la commande `chantier` déjà installée au niveau utilisateur (`~/.claude/commands/chantier.md` + les agents `chantier-planificateur`, `tache-executeur`, `spec-relecteur`, `branche-pousseur`). Deux workflows concurrents valent moins qu'un seul. |
| `claude/skills/demo`, `claude/skills/largeur`, `claude/skills/noeuds` | cadrage d'adoption : sponsor, mandat, mesures de largeur, quatre nœuds. CoachingIA est un outil local mono-utilisateur ; il n'y a personne à convaincre. |
| `claude/*.sh` | réécrits en PowerShell sous `.claude/hooks/` : `jq` n'est pas installé sur ce poste, et sans lui les trois gardes sortaient en code 0 — elles autorisaient tout, en silence. |

Ce qui a été gardé et adapté vit dans `.claude/` : la discipline
testeur/codeur/relecteur, les gardes de périmètre, et surtout la ligne rouge
sur l'étalon.

---

## `eval/` — l'angle mort de l'import, soldé le 12 septembre 2026

Ce dossier n'était pas mentionné ci-dessus, et c'était le problème. Il était
resté **à la racine du dépôt et branché sur la CI**, où quatre de ses cinq
étapes passaient au vert grâce à des fixtures, sans rien mesurer de CoachingIA.
Un chiffre qui monte devant témoin sans rien mesurer est exactement la faute
que ce harnais existe pour empêcher.

Deux autres raisons de le déplacer :

- `eval/` et `evals/` différaient d'une lettre et avaient un sens opposé.
  `evals/` est **l'étalon réel** de CoachingIA, en `permissions.deny` plus un
  hook ; `eval/` était inerte et protégé par rien. Cette confusion serait
  arrivée un jour de fatigue, ou par un agent complétant un chemin.
- La suite unitaire elle-même était rouge sans que personne le voie : les
  sondes de garde-fou invoquaient des scripts `.sh` qui n'existaient plus,
  puisqu'ils avaient été réécrits en `.ps1`. Elle est verte depuis que les
  hooks d'origine et les sondes se retrouvent dans la même quarantaine.

**Ce dossier n'est pas mort, il attend son entrée.** `eval/run_tasks_eval.py`
lit un *contrat de run public* (`eval/schemas/run_contract.schema.json`) — et
c'est précisément ce que `src/CoachingIA.Harness.Core/Transcripts/RunExporter.cs`
produit désormais, à partir de vraies sessions Claude Code.

La condition de réouverture est écrite dans `.github/workflows/eval.yml`, à
l'endroit où elle sera lue : le jour où `coachingia exporter-run` produit un
fichier conforme au contrat, l'étape « eval de tâches » retrouve une entrée
réelle, et on la rouvre avec des runs plutôt qu'avec des fixtures.
