---
name: livrer
description: Orchestre la livraison d'un plan validé en vagues de tâches parallèles (testeur, codeur, relecteur, éval) avec arrêt aux portes humaines ; à utiliser pour "livre le plan", "lance la vague", "exécute les tâches", "déroule la livraison".
disable-model-invocation: true
argument-hint: "[specs/<epopee>/plan.md]"
---

Livraison de $ARGUMENTS. Tu es l'orchestrateur ; tu n'écris ni test ni code toi-même. Parallélise les branches à l'intérieur d'une vague, avance le tronc d'une vague à la fois.

1. Lis le plan. Une tâche est éligible quand toutes ses `depends_on` sont `done` et fusionnées. Plafond 5 par vague ; au-delà, coupe par ordre d'id.
2. Pour chaque tâche de la vague, en parallèle : `status: in_progress` (fichier de tâche + plan), branche `task/<id>` depuis l'intégration (`git worktree add` toi-même si le worktree doit partir de l'intégration), lance `testeur` avec le chemin de la tâche et la branche.
3. Porte humaine A (relecture inversée) : affiche `id → Cn → nom du test` pour toute la vague, une seule fois. Arrête-toi. Attends une réponse explicite.
4. Pour chaque tâche validée, en parallèle : `codeur` sur la branche, puis `relecteur`. `TEST_CONTESTÉ` / `BLOQUÉ` / `SPEC_INCOMPLÈTE` → `status: blocked`, sans arrêter les autres.
5. Fin de vague : pour chaque `MERGEABLE`, merge sur l'intégration puis `dotnet test`. Un merge qui casse repasse la tâche en `blocked` avec diagnostic. Les autres passent `done`.
6. Si `src/Horus.Retrieval` a été touché : `eval-retrieval` une fois pour la vague. Si `src/Horus.Tools` a été touché : `python3 eval/check_tool_schemas.py` doit sortir 0. Si l'orchestrateur a été touché : `/eval-taches`. Une régression est bloquante.
7. Porte humaine B : `git diff --stat` de l'intégration, verdicts, chiffres d'éval, liste des `blocked`. Arrête-toi. Ne commite rien sur la branche principale.
8. Vague suivante seulement après accord explicite. Une seule modification du harnais par cycle, jamais au milieu d'une vague.
