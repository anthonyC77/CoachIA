# Configuration d'origine — projet « Horus »

Ces fichiers viennent d'un autre dépôt (Horus : RAG hybride, front Angular, éval
Python). Ils sont conservés ici pour référence, hors des dossiers que Claude Code
inspecte — rien de ce qui vit sous `_horus/` n'est chargé.

Écartés parce qu'ils n'ont aucun référent dans CoachingIA :

| Fichier | Pourquoi |
|---|---|
| `rules/angular.md` | il n'y a pas de front. Le dépôt est trois projets .NET plus un harnais de tests console. |
| `rules/outils-types.md` | `RunPolicy`, quotas, clés d'idempotence, schémas d'outils générés depuis les types : rien de tout cela n'existe. Les `ToolSpec` de `CorpusTools` sont écrites à la main. |
| `agents/juge-taches.md` | il n'y a ni orchestrateur, ni contrat de run public, ni runs à juger. Le juge LLM est prévu à l'étape 5 du plan d'évaluation, pas avant. |
| `agents/eval-retrieval.md` | il n'y a pas de retrieval. Remplacé par `agents/evaluateur.md`, qui joue la vraie campagne C#. |
| `skills/eval-retrieval`, `skills/eval-taches` | mêmes raisons. |
| `skills/cadrer`, `skills/livrer` | doublons de la commande `chantier` déjà installée au niveau utilisateur (`~/.claude/commands/chantier.md` + les agents `chantier-planificateur`, `tache-executeur`, `spec-relecteur`, `branche-pousseur`). Deux workflows concurrents valent moins qu'un seul. |
| `skills/demo`, `skills/largeur`, `skills/noeuds` | cadrage d'adoption : sponsor, mandat, mesures de largeur, quatre nœuds. CoachingIA est un outil local mono-utilisateur ; il n'y a personne à convaincre et aucun `eval/check_nodes.py`. |
| `hooks/*.sh` | réécrits en PowerShell : `jq` n'est pas installé sur ce poste, et sans lui les trois gardes sortaient en code 0 — elles autorisaient tout, en silence. |

Ce qui a été gardé et adapté vit dans `.claude/` : la discipline testeur/codeur/
relecteur, les gardes de périmètre, et surtout la ligne rouge sur l'étalon.
