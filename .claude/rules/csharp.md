---
paths:
  - "src/**/*.cs"
  - "tests/**/*.cs"
---
# C# — les règles qui surprennent (le reste est standard)

## Le harnais de tests n'est pas un framework
- Il n'y a **ni xUnit, ni NUnit, ni assertion library**. `tests/CoachingIA.Harness.Tests` est un `Exe` : `Program.cs` construit une fermeture `Check(bool condition, string label)` et appelle une suite après l'autre.
- Une suite est `public static class XxxTests { public static void Run(Action<bool,string> check) }` — ou `Run(Action<bool,string> check, string lensDir)`. On l'enregistre par un appel littéral dans `Program.cs`, précédé d'une bannière. Pas de découverte, pas d'attribut.
- **Le libellé est le critère.** Il remplace ce qu'un `[Trait("Critere","Cn")]` ferait ailleurs : une phrase française qui énonce la promesse, et qui interpole la valeur observée quand elle échoue.
  ```csharp
  check(captured.Count == 6, $"6 spans exportés : ouverture et fermeture donnent un seul span (obtenu {captured.Count})");
  ```
  Un libellé qui ne dit pas ce qui est promis, ou qui ne montre pas ce qui a été obtenu, est un test à moitié écrit.
- Pas de moyen de lancer un test isolé, autrement qu'en commentant les suites dans `Program.cs`. C'est attendu, pas une lacune à combler.
- Doubles : écrits à la main, dans la suite. Aucune bibliothèque de mock. Les sessions synthétiques se fabriquent (`ReviewTests.Session`, `ProducteurBilan.Session`) — **jamais** en lisant `~/.claude/projects/` : un test qui lit les transcripts réels est lent, non reproductible et différent sur chaque poste.

## Ce qui ne doit pas bouger
- `CoachingIA.Harness.Core` n'a **aucune dépendance NuGet externe** (seulement la référence de framework `Microsoft.AspNetCore.App`). C'est délibéré : le cœur doit compiler et se tester hors ligne. Y ajouter un paquet est une décision, pas un détail.
- `TreatWarningsAsErrors` est actif pour toute la solution (`Directory.Build.props`). Un avertissement fait échouer la génération.
- `InvariantGlobalization` est actif : pas de `CultureInfo("fr-FR")`. Les noms de mois et de jours sont des tableaux littéraux dans les rendus, et c'est pour ça.
- `ReviewArchive.Write` ne s'écrase pas silencieusement : contenu identique → aucun fichier touché ; contenu changé → l'ancien part dans `archives/`, horodaté par **sa propre** date de modification. Ne pas « simplifier » en écriture directe.
- `CaptureContent: false` doit rester une coupure dure : aucun texte de prompt ni de sortie d'outil ne part vers OpenTelemetry quand elle est à faux.
- Les attributs OpenInference (`OI.*`) et les attributs de coaching (`Coach.*`) sont additifs. On ne détourne jamais un nom OpenInference pour y mettre une donnée de coaching.

## Écriture
- **Français** dans les commentaires, les chaînes visibles et les libellés de test. Les noms de types sont un mélange assumé (`SegmentedTask`, `LensWriter`, `Problematique`, `SceneIssueLevel { Erreur, Doute }`) : suivre le voisinage plutôt qu'une règle.
- Les commentaires XML de ce dépôt disent **pourquoi**, pas quoi — souvent avec la raison pédagogique ou éthique du choix (« un coach qui ne cite pas n'est qu'un tableau de bord », « on refuse de bloquer un pack sur une question de goût »). Un commentaire qui paraphrase la signature n'apporte rien.
- Pas d'`async` là où rien n'attend : la brique d'évaluation est synchrone exprès, tout y tourne en lot sur un poste. Un `Task<>` qui ne sert jamais remonte jusqu'au CLI, qui est un script.
