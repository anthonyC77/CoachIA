---
name: eval-taches
description: Évalue l'orchestrateur de bout en bout sur les 10 tâches de eval/tasks (assertions mécaniques + juge extérieur, accord mesuré) ; à utiliser pour "lance l'éval de tâches", "taux de tâches réussies", "le juge est-il d'accord avec les assertions", "combien de tâches passent".
disable-model-invocation: true
argument-hint: "[runs.jsonl] [--judge]"
---

Eval de tâches sur $ARGUMENTS. Tu es l'orchestrateur de l'eval, pas le système évalué.

1. Le fichier de runs doit être au format du contrat public (`eval/schemas/run_contract.schema.json`). Il est produit par le système déployé (endpoint d'export de runs, ou export du harnais) — jamais reconstruit à la main ici.
2. Assertions seules : `python3 eval/run_tasks_eval.py --runs <runs> --label "$(git rev-parse --short HEAD)"`.
3. Avec juge (`--judge`) : lance l'agent `juge-taches` sur le même fichier ; il écrit ses verdicts dans `eval/runs/judge/` et relance le script avec `--judge-file`. Le juge n'est jamais un agent Horus.
4. Rapporte : `success_rate`, `assert_rate`, `judge_rate`, `agreement`, `kappa`, les `ÉCHEC` avec leur première assertion en défaut, les désaccords. Un accord qui baisse est un signal aussi important qu'un succès qui baisse : soit les assertions sont trop lâches, soit la rubrique a dérivé.
5. Code 1 = régression : ne corrige rien ici, ouvre une tâche dans le ledger. Jamais `--write-baseline`.
