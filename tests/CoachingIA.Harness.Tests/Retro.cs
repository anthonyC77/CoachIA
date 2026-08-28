using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai de la rétrospective. Deux promesses sont vérifiées ici, et ce
/// sont les deux seules qui comptent vraiment : une progression réelle doit être
/// reconnue et datée, et une période sans travail ne doit jamais être lue comme
/// une régression. Le reste n'est que présentation.
/// </summary>
public static class RetroTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        var writer = new LensWriter(LensCatalog.Load(lensDir).Resolve("starcraft2"), "zerg");

        // Seize semaines : huit brouillonnes, puis huit propres, avec un mois
        // d'arrêt au milieu. C'est le scénario d'un apprenant qui progresse
        // vraiment, et qui prend des vacances.
        var monday = ReviewTests.ThisMonday().AddDays(-7 * 17);
        var sessions = new List<TranscriptSession>();
        var silenceFrom = monday.AddDays(7 * 8);

        for (var w = 0; w < 17; w++)
        {
            var week = monday.AddDays(7 * w);
            if (w is >= 8 and < 11) continue;               // trois semaines sans rien
            if (week >= ReviewTests.ThisMonday()) continue;  // on ne juge pas la semaine en cours
            sessions.Add(ReviewTests.Session(ReviewTests.At(week.AddDays(1), 9), sloppy: w < 8, tasks: 3));
        }

        var retro = new RetrospectiveBuilder().Build(sessions, writer);

        Console.WriteLine("Période couverte");
        check(!retro.IsEmpty, "la rétrospective trouve du travail à rejouer");
        check(retro.WeeksActive == 14, $"quatorze semaines travaillées, les trois de coupure exclues (obtenu {retro.WeeksActive})");
        check(retro.WeeksCovered >= 16, $"sur une fenêtre d'au moins seize semaines (obtenu {retro.WeeksCovered})");
        check(retro.WeeksSilent >= 3, "les semaines sans trace sont comptées à part");
        check(retro.Months.Count >= 4, $"au moins quatre mois distincts (obtenu {retro.Months.Count})");

        Console.WriteLine("\nUne progression réelle");
        var verif = retro.Trails.FirstOrDefault(t => t.Key == "verification_present");
        check(verif is not null, "la vérification est suivie sur toute la période");
        check(verif!.Verdict == TrendVerdict.Acquis,
              $"passer de zéro test à un test par tâche est reconnu comme acquis (obtenu {verif.Verdict})");
        check(verif.Late > verif.Early, "et la fin est meilleure que le début");
        check(verif.CrossedOn is not null, "la bascule est datée");
        check(verif.CrossedOn!.Value >= silenceFrom,
              $"à la reprise, pas avant (obtenu {verif.CrossedOn.Value:yyyy-MM-dd}, reprise le {silenceFrom:yyyy-MM-dd})");
        check(retro.Milestones.Any(m => m.SignalKey == "verification_present"),
              "et elle figure dans les bascules");
        check(retro.Milestones.SequenceEqual(retro.Milestones.OrderBy(m => m.On)),
              "les bascules sont racontées dans l'ordre");

        Console.WriteLine("\nUn trou n'est pas une régression");
        check(retro.Silences.Count >= 1, "la coupure est repérée");
        var silence = retro.Silences[0];
        check(silence.Weeks == 3, $"trois semaines sans session (obtenu {silence.Weeks})");
        check(!retro.Trails.Any(t => t.Points.Any(p => p.MondayOf >= silenceFrom && p.MondayOf < silenceFrom.AddDays(21))),
              "aucun point de mesure n'est inventé pendant la coupure");
        check(retro.With(TrendVerdict.EnRecul).All(t => t.Late < t.Early || !t.HigherIsBetter),
              "aucun signal n'est déclaré en recul sans que les chiffres le disent");

        Console.WriteLine("\nLa lentille habille sans remplacer");
        check(verif.Statement.Length > 0 && !verif.Statement.Contains("overseer"),
              "le constat reste dit en français de métier");
        check(verif.Flourish is not null && verif.Flourish.Contains("overseer"),
              "l'image, elle, parle bien le zerg");
        check(verif.LevelTerm.Length > 0, "le palier porte son nom de lentille");

        Console.WriteLine("\nRefus de conclure trop tôt");
        var court = new List<TranscriptSession>
        {
            ReviewTests.Session(ReviewTests.At(ReviewTests.ThisMonday().AddDays(-14), 9), sloppy: true, tasks: 2),
            ReviewTests.Session(ReviewTests.At(ReviewTests.ThisMonday().AddDays(-7), 9), sloppy: true, tasks: 2),
        };
        var mince = new RetrospectiveBuilder().Build(court, writer);
        check(mince.Trails.All(t => t.Verdict == TrendVerdict.TropPeuDeDonnees),
              "deux semaines ne suffisent à prononcer aucun verdict");

        Console.WriteLine("\nFenêtre demandée");
        var depuis = ReviewTests.ThisMonday().AddDays(-7 * 5);
        var recent = new RetrospectiveBuilder().Build(sessions, writer, since: depuis);
        check(recent.From >= depuis.AddDays(-6), "--depuis coupe bien le début de la période");
        check(recent.Tasks < retro.Tasks, "et la rétrospective courte porte sur moins de tâches");

        Console.WriteLine("\nRendu HTML");
        var html = HtmlRetrospectiveRenderer.Render(retro, writer);
        check(html.Contains("<!doctype html>"), "la page est autonome");
        check(html.Contains("Ce qui est acquis"), "et met l'acquis en tête");
        check(html.Contains("<svg"), "les trajectoires sont dessinées");
        check(!html.Contains("NaN") && !html.Contains("∞"), "aucun chiffre indéfini n'atteint la page");
        check(html.Contains("Les bascules"), "les bascules datées apparaissent");

        // Une image de signal raconte toujours un défaut. Sous un acquis, elle
        // dirait le contraire du titre — donc elle ne doit pas y être.
        var acquis = html.IndexOf("Ce qui est acquis", StringComparison.Ordinal);
        var suite = html.IndexOf("<section", acquis + 1, StringComparison.Ordinal);
        var bloc = suite > acquis ? html[acquis..suite] : html[acquis..];
        check(!bloc.Contains("class=\"flourish\""),
              "aucune scène de défaite ne s'affiche sous ce qui est acquis");
        check(!bloc.Contains("Pour franchir"),
              "et aucun conseil de correction non plus");
        check(System.Text.RegularExpressions.Regex.Count(html, "<section") >= 4,
              "la page a bien plusieurs sections");
    }
}
