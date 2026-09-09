---
paths:
  - "evals/**"
---
# L'étalon — ligne rouge et discipline

## Ce que contient `evals/`
```
evals/cas/          le jeu partageable        versionné   ← étalon, en deny
evals/verdict.json  l'état approuvé           versionné   ← étalon, en deny
evals/prive/        épreuves tirées du réel   ignoré
evals/journal/      campagnes passées         ignoré
```

## La ligne rouge
- **On ne modifie pas l'étalon pour faire passer une campagne.** `evals/cas/**` et `evals/verdict.json` sont en `deny` dans `settings.json`, et `garde-etalon.ps1` ferme la porte Bash (`sed -i`, `tee`, redirections, `Set-Content`…). Ce ne sont pas des intentions : les deux ont été testées.
- **Réapprouver est un geste humain.** `COACHINGIA_APPROUVER_EVALS=true` réécrit `verdict.json` ; le hook le bloque depuis un agent. C'est l'équivalent exact d'un `--write-baseline` : un nouveau point de référence se pose depuis un terminal humain, en connaissance de cause.
- La porte compare à l'état approuvé, **jamais à un seuil**. Un seuil flottant (« au moins 85 % ») se négocie à la baisse le jour où il gêne. Un écart fait échouer **dans les deux sens** : une épreuve qui se met à passer demande à être approuvée, ce qui laisse une trace dans `git diff`.

## Vie privée
- Aucune épreuve de `evals/cas/` ne cite un transcript réel. Le chargeur refuse `partageable: false` et `origine: "transcript"`, et la suite le vérifie — un cas privé dans le dossier versionné est un incident, pas une coquille.
- Les épreuves de segmentation sont partageables **par construction** : `IsNewTask` ne consulte du prompt que sa longueur, son ouverture parmi des listes fermées publiées dans le code, et sa référentialité. Cinq champs numériques suffisent à reconstruire un tour ; pas un mot de l'apprenant n'y figure.
- Pas d'anonymisation automatique de vrais prompts. Un prompt de développeur contient des noms de classes, de clients, d'architectures ; aucune substitution automatique n'est fiable là-dessus, et une fausse garantie est pire que pas de garantie. La paraphrase est humaine, ou le cas reste dans `evals/prive/`.

## Discipline des épreuves
- Chaque épreuve porte un `intitule` : une phrase française qui dit ce qu'elle met à l'épreuve. Un cas dont on ne sait pas dire ce qu'il éprouve n'a rien à faire dans le jeu.
- **Des cas fabriqués faux sont obligatoires.** Sans eux, un évaluateur qui a cessé de mordre se déclarerait vert. Ils portent `etiquettes: ["fabriquee-fausse", …]` et déclarent dans `attendu.etiquettes` ce qu'ils doivent faire dire — sans quoi leur panne apparaîtrait comme un « progrès ».
- Une épreuve étiquetée `echec-connu` ne se supprime pas quand elle est corrigée : elle devient la non-régression. Une épreuve retirée est un aveu, pas un progrès — et `verdict.json` étant versionné, le retrait laisse une trace.
- Un verdict porte toujours son explication, et un échec toujours sa preuve citée. Un chiffre seul ne se conteste pas.
- **Aucun `Verdict` ne parle à l'apprenant.** Il parle à qui maintient l'outil. Rien de tout cela n'a sa place dans un `bilan` ou une `retro` : le vocabulaire du reproche fait à l'apprenant (`Signal`, `SignalSpec`) et celui de la mesure de l'outil (`Verdict`, `Bareme`) restent séparés, jusque dans les attributs de span (`eval.*`, jamais `signal.*`).

## Le juge, et sa place
- Le juge LLM existe (`JugeReecriture`, sur `ClaudeCli`) et **ne mesure pas la réécriture** : il mesure l'accord entre un évaluateur du code et un avis extérieur, sur la contrainte citée mot pour mot depuis cet évaluateur. Barème `accord` / `desaccord` / `douteux`.
- Il ne vient que sur demande — `coachingia evaluer --juge` — et **jamais dans la porte** : `CampagneStandard.Porte()` ne rend que des déterministes, et c'est vérifié (`evaluateurs.All(e => e.Deterministe)`). Un évaluateur `Deterministe = false` n'entre pas dans `verdict.json`, sans quoi le fichier bougerait à chaque exécution.
- Une panne du juge rend une **indécision** qui dit pourquoi, jamais un échec : une coupure n'est pas une régression. Et un désaccord ne fait tomber aucune porte — il désigne un endroit à regarder.
- La composition (producteurs, porte, juges) vit dans `CampagneStandard`, prise au même endroit par le harnais et par la commande. Un évaluateur écrit et jamais branché est attrapé par la vérification « aucun évaluateur du noyau n'est laissé hors de la composition ».
