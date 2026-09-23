---
name: cadrer
description: Cadre une épopée en plan de tâches (DAG + fichiers de tâche avec critères numérotés) dans le repo de specs ; à utiliser pour "cadre cette fonctionnalité", "découpe en tâches", "écris le plan", "prépare la livraison".
disable-model-invocation: true
argument-hint: "<nom-epopee> [objectif en une phrase]"
---

Cadrage de $ARGUMENTS, dans la conversation principale (c'est ici que l'humain est). Lis `docs/glossaire.md` si un terme métier est ambigu.

1. Objectif en une phrase, mesurable par un chiffre de `eval/` quand c'est possible (recall, success_rate, une mesure de largeur).
2. Découpe en tâches de ≤ 1 journée, chacune avec : critères `C1…Cn` vérifiables par un test, périmètre (fichiers), signatures publiques, `depends_on`, `lot` (back/front/eval).
3. Toute tâche qui expose un nouvel outil au modèle commence par le type (`src/Horus.Tools/Contracts/`), jamais par la description ; son critère inclut « schéma régénéré, `check_tool_schemas.py` sort 0 ».
4. Toute tâche qui touche au retrieval a un critère « `run_eval.py` sans régression » ; toute tâche qui touche à l'orchestrateur a un critère « `run_tasks_eval.py` sans régression ».
5. Écris `specs/<epopee>/plan.md` (tableau id / titre / depends_on / statut / branche + vagues de ≤ 5) et `specs/<epopee>/tasks/<id>.md`.
6. Porte humaine 1 : affiche le plan, arrête-toi, attends un accord explicite. Pas de code avant.
