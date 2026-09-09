---
name: demo
description: Prépare la démo de 90 secondes qui montre une corvée qui disparaît (pas une capacité qui apparaît), à partir d'une tâche réelle du golden set ; à utiliser pour "prépare la démo", "qu'est-ce qu'on montre au sponsor", "démo pour le mandat".
disable-model-invocation: true
argument-hint: "<corvée> <personne à convaincre>"
---

Critère de démo : on montre une corvée qui disparaît, pas une capacité qui apparaît. Si la phrase de la démo commence par « le système peut… », recommence.

1. Nomme la corvée en une phrase du métier (« ressaisir chaque demande de badge dans deux outils »), et qui la subit aujourd'hui.
2. Choisis la tâche de `eval/tasks/` qui la porte ; sa 3e ligne doit être signée par quelqu'un du métier (`verified_by`), sinon la démo n'a pas d'étalon.
3. Script de 90 s : avant (la corvée, chronométrée) / après (le run, avec sa provenance affichée : « d'où vient cette phrase ? » a une réponse) / le chiffre (`success_rate` de la tâche sur le dernier run réel, mesure de largeur n°4).
4. Après la démo : `python3 eval/width_report.py --cycle N --demo-chore "<corvée>" --demo-shown-to "<personne>"` — c'est le critère du nœud 3.
