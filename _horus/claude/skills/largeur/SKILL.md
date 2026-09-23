---
name: largeur
description: Publie les 4 mesures de largeur du cycle (golden qui passe, outils sans relâcher la politique, contexte sans dégradation, tâches sans intervention humaine) au même endroit ; à utiliser pour "publie les mesures", "rapport du cycle", "les chiffres de la semaine", "élargir".
disable-model-invocation: true
argument-hint: "--cycle N [--reviewed-by X] [--demo-chore … --demo-shown-to …]"
---

Élargir, pas ajouter. Un chiffre qui bouge devant témoin fabrique un mandat ; une note d'intention n'en fabrique pas.

1. Préalable : un run retrieval et un run de tâches réels de ce cycle dans `eval/runs/` (sinon lance-les d'abord ; code 1 de `width_report.py` = une source manque).
2. `python3 eval/width_report.py $ARGUMENTS --label "$(git rev-parse --short HEAD)"` → `eval/width/cycle-NN.json` + `.md`.
3. Le rapport n'est publié qu'une fois relu par un tiers (`--reviewed-by`) : ce n'est pas l'auteur qui pèse. Relance la commande avec le nom du relecteur et, si la démo a eu lieu, `--demo-chore` / `--demo-shown-to`.
4. Compare au cycle précédent en une ligne par mesure. Ne commente pas au-delà du chiffre.
