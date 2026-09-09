---
name: evals
description: Joue la campagne d'évaluation de CoachingIA (promesses du bilan, découpe en tâches, conformité de réécriture, scènes) et compare à l'état approuvé evals/verdict.json ; à utiliser après un changement de TaskSegmenter, SignalExtractor, WeeklyReview ou de src/CoachingIA.Harness.Core/Evaluation, ou quand on demande "lance l'éval", "la découpe a-t-elle régressé", "les promesses tiennent-elles".
allowed-tools: Bash, Read
---

Deux portes d'entrée, la même composition (`CampagneStandard`) : le harnais de tests, qui garde l'état approuvé, et `coachingia evaluer`, qui joue la campagne seule. La première fait foi — c'est elle qui tourne avant un commit.

1. `dotnet run --project tests/CoachingIA.Harness.Tests`
   Lis la section qui suit la bannière `évaluation`. Code 0 = tout est vert. Code 1 = au moins une vérification a échoué, **toutes suites confondues**.

   Pour la campagne seule, sans les autres suites : `dotnet run --project src/CoachingIA.Cli -- evaluer`. Codes : 0 rien n'a bougé, 1 un écart à trancher, **2 la campagne n'a rien pu mesurer** — un 2 ne se lit jamais comme un vert.

   `--juge` y ajoute l'avis de `claude -p` sur les mêmes questions que le code (section `Ce que le juge dit du code`). Il coûte un appel par épreuve, n'entre **jamais** dans l'état approuvé et ne change aucun code de sortie : un désaccord désigne un endroit à regarder, il ne prononce rien.

2. Avant de conclure au rouge, écarte les échecs déjà connus : `.claude/tests-attendus.txt` liste les vérifications acceptées telles quelles (aujourd'hui trois, qui visent des variantes de lentille tournant par semaine et n'ont rien à voir avec le code). Un échec qui n'est pas dans ce fichier est un vrai échec.

3. Rapporte, dans cet ordre :
   - **les écarts contre l'état approuvé**, avec leur sens : `régression` (ça passait, ça ne passe plus), `progrès` (ça échouait, ça passe — à approuver), `nouveau` (épreuve jamais vue), `disparu` (épreuve retirée du jeu) ;
   - le tableau `Ce que la campagne mesure` : réussis / échecs / **indécis comptés à part** — un évaluateur qui n'a pas su conclure n'a pas constaté de défaut, les confondre transformerait une panne en mauvaise note ;
   - `Ce qui ne passe pas, et pourquoi` : les explications, pas seulement les chiffres ;
   - la ligne `Le contenu réellement livré` : l'état du pack de scènes qui part chez l'apprenant.

4. Un **progrès** n'est pas forcément une bonne nouvelle. Sur une épreuve étiquetée `fabriquee-fausse`, une bascule `non_conforme → conforme` veut dire que l'évaluateur a cessé de mordre. La vérification « les N étiquettes déclarées par les épreuves sont obtenues » est là pour ça : si elle est rouge, lis-la avant tout le reste.

5. **Jamais `COACHINGIA_APPROUVER_EVALS`.** Poser un nouveau point de référence est une décision humaine, prise depuis un terminal humain ; le hook `garde-etalon.ps1` bloque de toute façon. Ne modifie ni `evals/cas/`, ni `evals/verdict.json`, ni pour « remettre au propre ».

6. Ne propose aucune correction du code évalué. Si une régression demande une décision, dis laquelle et arrête-toi.
