---
name: noeuds
description: Dit où en est le projet contre les 4 nœuds de progression (étalon, passage typé, run lisible du dehors, tiers qui lit le chiffre) et ce qui manque pour franchir le prochain ; à utiliser pour "on en est où", "quel nœud", "qu'est-ce qui bloque le nœud suivant", "peut-on passer à la suite".
disable-model-invocation: true
argument-hint: "[--node N]"
---

1. `python3 eval/check_nodes.py $ARGUMENTS`. Code 0 = franchi, 1 = non, 2 = erreur (rapporte, ne contourne pas).
2. Règle du tronc : on ne commence pas N+1 avant d'avoir franchi N. Si le nœud courant n'est pas franchi, la seule proposition valable est la plus petite action qui fait passer un `[KO]` à `[OK]` — un nœud non franchi rétrécit, il ne rallonge pas son cycle.
3. Une décision par cycle : propose-en une, pas trois.
4. Arrêt : le nœud courant, ses `[KO]` avec la commande qui les vérifie, la décision proposée. Rien d'autre.
