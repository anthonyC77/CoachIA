---
paths:
  - "src/Horus.Tools/**/*.cs"
  - "src/**/RunPolicy*.cs"
  - "src/**/RunContext*.cs"
---
# Harnais d'exécution en production — un outil est un type, pas une description
- Un outil = un `record` annoté `[Tool("nom", SideEffect = …, Description = …)]` + un `record` `[ToolOutput("nom")]`. Le schéma JSON est généré depuis le type (`eval/gen_tool_schemas.py` aujourd'hui, source generator C# demain). On ne l'écrit jamais à la main ; on modifie le type et on régénère.
- Le schéma sert trois fois : description pour le modèle, validation E/S dans le harnais, assertion dans l'eval. Supprimer une propriété doit casser la validation ET l'eval au même commit (`eval/check_tool_schemas.py` le vérifie).
- `RunPolicy` est figée avant la première action d'un run et recopiée dans le contrat de run public : allow-list d'outils par agent, quotas durs (appels, tokens, wall-clock, €), identité SI (`principal`, `si_role`). Dépasser un quota termine le run en `quota_exceeded`, pas en boucle.
- Tout outil `SideEffect = true` exige une clé d'idempotence ; sans clé, le harnais refuse l'appel avant de l'exécuter.
- Le `CancellationToken` est propagé à chaque appel d'outil ; un run annulé se termine en `cancelled` sans effet de bord supplémentaire.
- Chaque fragment de contexte envoyé au modèle porte sa provenance (`document`, `tool_call`, `user`, `policy`) ; ce qui n'a pas de provenance ne va pas dans le contexte.
- Test du garde-fou avant de merger : *une action dangereuse empêchée uniquement par le prompt n'est pas empêchée.* Si tu en trouves une, ajoute une sonde dans `eval/guardrail_probes.jsonl` et le garde-fou mécanique qui la ferme.
