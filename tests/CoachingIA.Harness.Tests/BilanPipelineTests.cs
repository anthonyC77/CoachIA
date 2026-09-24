using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du pipeline du bilan : la promesse tenue ici est que découper
/// <c>coachingia bilan</c> en étapes séparées — préparer, critiquer, archiver —
/// ne change rien à ce qu'il produit, et que la revue survit à un aller-retour
/// par un fichier JSON, prête à être reprise par une activity distincte.
/// </summary>
public static class BilanPipelineTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        var catalog = LensCatalog.Load(lensDir);
        var neutral = new LensWriter(catalog.Resolve("neutre"));

        var thisMonday = ReviewTests.ThisMonday();
        var lastWeek = thisMonday.AddDays(-7);
        var before = thisMonday.AddDays(-14);
        var sessions = new List<CoachingIA.Harness.Core.Transcripts.TranscriptSession>
        {
            ReviewTests.Session(ReviewTests.At(before, 1), sloppy: true, tasks: 3),
            ReviewTests.Session(ReviewTests.At(lastWeek, 1), sloppy: true, tasks: 3),
            ReviewTests.Session(ReviewTests.At(thisMonday, 1), sloppy: true, tasks: 2),
        };

        Console.WriteLine("Critique différée puis appliquée");
        var direct = new WeeklyReviewBuilder { Critic = new HeuristicPromptCritic() }.Build(sessions, null, neutral);
        var differed = BilanPipeline.Preparer(sessions, null, neutral, null, differerCritique: true);
        check(differed.PromptOfTheWeek is null, "sans avoir appliqué la critique, PromptOfTheWeek reste vide");
        check(differed.PromptACritiquer is not null, "mais le prompt choisi est déjà posé");

        BilanPipeline.AppliquerCritique(differed, new HeuristicPromptCritic());
        check(HtmlReviewRenderer.Render(differed, neutral) == HtmlReviewRenderer.Render(direct, neutral),
              "le rendu HTML de la critique différée est identique, caractère pour caractère, à celui de la critique immédiate");
        check(ReviewRenderer.ToMarkdown(differed, neutral) == ReviewRenderer.ToMarkdown(direct, neutral),
              "et le rendu Markdown aussi");

        Console.WriteLine("\nAppliquer une critique sans prompt choisi ne fait rien");
        var sansPrompt = new WeeklyReview { Week = "2026-W01", MondayOf = default };
        BilanPipeline.AppliquerCritique(sansPrompt, new HeuristicPromptCritic());
        check(sansPrompt.PromptOfTheWeek is null, "sans PromptACritiquer, AppliquerCritique ne pose rien");

        Console.WriteLine("\nSérialisation de la revue");
        var rejouee = BilanPipeline.Preparer(sessions, null, neutral, null, differerCritique: true);
        var json = WeeklyReviewSnapshot.Serialiser(rejouee);
        var relue = WeeklyReviewSnapshot.Deserialiser(json);
        BilanPipeline.AppliquerCritique(relue, new HeuristicPromptCritic());
        check(relue.PromptOfTheWeek is not null, "la critique appliquée après relecture a bien tourné");
        check(HtmlReviewRenderer.Render(relue, neutral) == HtmlReviewRenderer.Render(direct, neutral),
              "le rendu HTML après aller-retour JSON puis critique retrouve celui de la critique immédiate");
        check(ReviewRenderer.ToMarkdown(relue, neutral) == ReviewRenderer.ToMarkdown(direct, neutral),
              "et le rendu Markdown aussi");

        var reserialise = WeeklyReviewSnapshot.Serialiser(WeeklyReviewSnapshot.Deserialiser(WeeklyReviewSnapshot.Serialiser(direct)));
        check(reserialise == WeeklyReviewSnapshot.Serialiser(direct),
              "sérialiser, relire puis resérialiser redonne exactement le même JSON");

        Console.WriteLine("\nSérialisation de la critique seule");
        check(direct.PromptOfTheWeek is not null, "la revue directe porte bien une critique, pour l'éprouver");
        var critique = direct.PromptOfTheWeek!;
        var critiqueJson = WeeklyReviewSnapshot.SerialiserCritique(critique);
        var critiqueRelue = WeeklyReviewSnapshot.DeserialiserCritique(critiqueJson);
        check(critiqueRelue.Missing.Select(i => i.Key).SequenceEqual(critique.Missing.Select(i => i.Key)),
              "les critères manquants se retrouvent, dans le même ordre");
        check(critiqueRelue.Present.Select(i => i.Key).SequenceEqual(critique.Present.Select(i => i.Key)),
              "et les critères présents aussi");
        check(critiqueRelue.Missing.Concat(critiqueRelue.Present)
                  .All(item => ReferenceEquals(item, PromptRubric.Items.First(i => i.Key == item.Key))),
              "chaque critère réhydraté est bien l'instance canonique de PromptRubric.Items, pas une copie");

        Console.WriteLine("\nInventaire des transcripts");
        var dossierTranscripts = Path.Combine(Path.GetTempPath(), "coachingia-pipeline-inv-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dossierTranscripts);
        try
        {
            File.WriteAllText(Path.Combine(dossierTranscripts, "un.jsonl"), "");
            File.WriteAllText(Path.Combine(dossierTranscripts, "deux.jsonl"), "");
            File.WriteAllText(Path.Combine(dossierTranscripts, "trois.jsonl"), "");
            var chemins = BilanPipeline.Inventorier(dossierTranscripts, 2);
            check(chemins.Count == 2, $"la limite tronque bien l'inventaire (obtenu {chemins.Count})");
            check(chemins.All(Path.IsPathRooted), "chaque chemin rendu est absolu");
        }
        finally
        {
            Directory.Delete(dossierTranscripts, recursive: true);
        }

        Console.WriteLine("\nArchivage");
        var dossierBilans = Path.Combine(Path.GetTempPath(), "coachingia-pipeline-arc-" + Guid.NewGuid().ToString("n"));
        try
        {
            var (page, texte) = BilanPipeline.Archiver(dossierBilans, direct, neutral);
            check(File.Exists(Path.Combine(dossierBilans, direct.Week + ".html")), "la page HTML est écrite");
            check(File.Exists(Path.Combine(dossierBilans, direct.Week + ".md")), "le texte Markdown est écrit");
            check(page.Outcome != WriteOutcome.Unchanged, "le premier appel crée bien quelque chose");

            var (page2, texte2) = BilanPipeline.Archiver(dossierBilans, direct, neutral);
            check(page2.Outcome == WriteOutcome.Unchanged, "un second appel identique ne touche pas la page");
            check(texte2.Outcome == WriteOutcome.Unchanged, "ni le texte");
        }
        finally
        {
            Directory.Delete(dossierBilans, recursive: true);
        }
    }
}
