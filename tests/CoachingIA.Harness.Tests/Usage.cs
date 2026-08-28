using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai des indicateurs d'usage. On y vérifie surtout des choses qui
/// coûteraient cher si elles étaient fausses : une alerte de sous-utilisation
/// injustifiée décrédibilise le coach, et une semaine en cours comparée à une
/// enveloppe pleine en produirait une chaque lundi.
/// </summary>
public static class UsageTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Familles de modèles");
        check(UsageAnalyzer.FamilyOf("claude-opus-5") == ModelFamily.Opus, "opus reconnu");
        check(UsageAnalyzer.FamilyOf("claude-sonnet-4-5") == ModelFamily.Sonnet, "sonnet reconnu");
        check(UsageAnalyzer.FamilyOf("claude-haiku-4-5") == ModelFamily.Haiku, "haiku reconnu");
        check(UsageAnalyzer.FamilyOf("modele-maison") == ModelFamily.Autre, "un modèle inconnu ne casse rien");
        check(UsageAnalyzer.FamilyOf(null) == ModelFamily.Autre, "un modèle absent ne casse rien");

        Console.WriteLine("\nDécoupage en semaines ISO");
        var monday = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
        var sunday = new DateTimeOffset(2026, 8, 23, 23, 0, 0, TimeSpan.Zero);
        var nextMonday = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);
        check(UsageAnalyzer.WeekKey(monday) == UsageAnalyzer.WeekKey(sunday),
              "lundi et dimanche tombent dans la même semaine");
        check(UsageAnalyzer.WeekKey(monday) != UsageAnalyzer.WeekKey(nextMonday),
              "le lundi suivant ouvre une nouvelle semaine");

        Console.WriteLine("\nAgrégation");
        var session = Session(monday);
        var analyzer = new UsageAnalyzer { WeeklyTokenBudget = 1_000_000 };
        var weeks = analyzer.ByWeek([session]);
        check(weeks.Count == 1, $"une seule semaine (obtenu {weeks.Count})");
        var w = weeks[0];
        check(w.TotalRead == 300_000 + 100_000, $"les jetons lus s'additionnent (obtenu {w.TotalRead})");
        check(Math.Abs(w.ShareOf(ModelFamily.Opus) - 0.75) < 0.01,
              $"la part d'Opus est calculée sur les jetons (obtenu {w.ShareOf(ModelFamily.Opus):P0})");
        check(Math.Abs(w.CallShareOf(ModelFamily.Opus) - 0.5) < 0.01,
              "la part d'appels diffère de la part de jetons — les deux sont utiles");
        check(w.ActiveDays.Count == 1, "un seul jour actif");
        check(Math.Abs(w.CacheRatio - 0.5) < 0.01, $"le taux de cache est calculé (obtenu {w.CacheRatio:P0})");

        Console.WriteLine("\nTypes de travail");
        check(UsageAnalyzer.Classify(Task("Edit", "Read")) == WorkKind.Edition,
              "lire puis écrire, c'est de l'édition");
        check(UsageAnalyzer.Classify(Task("Read", "Grep")) == WorkKind.Exploration, "lire seulement, c'est explorer");
        check(UsageAnalyzer.Classify(Task("Bash", "Bash", "Read")) == WorkKind.Execution, "surtout du Bash, c'est exécuter");
        check(UsageAnalyzer.Classify(Task("WebSearch")) == WorkKind.Recherche, "chercher sur le web");
        check(UsageAnalyzer.Classify(Task("Task", "Edit")) == WorkKind.Orchestration,
              "déléguer prime sur éditer");
        check(UsageAnalyzer.Classify(Task()) == WorkKind.Conversation, "sans outil, c'est une conversation");

        Console.WriteLine("\nAlertes");
        var thisMonday = ThisMonday();
        var quiet = new UsageAnalyzer { WeeklyTokenBudget = 1_000_000 };
        var twoLowWeeks = new List<WeekUsage>
        {
            Week(thisMonday.AddDays(-14), read: 200_000, days: 3),
            Week(thisMonday.AddDays(-7), read: 300_000, days: 3),
        };
        var alerts = quiet.Alerts(twoLowWeeks);
        check(alerts.Any(a => a.Key == "sous_utilisation"),
              "deux semaines basses de suite déclenchent l'alerte de sous-utilisation");

        var oneLowWeek = new List<WeekUsage>
        {
            Week(thisMonday.AddDays(-14), read: 900_000, days: 4),
            Week(thisMonday.AddDays(-7), read: 300_000, days: 3),
        };
        check(!quiet.Alerts(oneLowWeek).Any(a => a.Key == "sous_utilisation"),
              "une seule semaine basse ne suffit pas — un creux n'est pas une tendance");

        var currentOnly = new List<WeekUsage> { Week(thisMonday, read: 50_000, days: 1) };
        check(quiet.Alerts(currentOnly).Count == 0,
              "la semaine en cours ne déclenche aucune alerte : elle est incomplète");

        var noRef = new UsageAnalyzer();
        var withoutBudget = noRef.Alerts(twoLowWeeks);
        check(!withoutBudget.Any(a => a.Key is "sous_utilisation" or "plafond_proche"),
              "sans enveloppe de référence, aucune alerte de volume n'est inventée");

        var lowCache = new List<WeekUsage> { Week(thisMonday.AddDays(-7), read: 900_000, days: 4, cacheShare: 0.1) };
        check(noRef.Alerts(lowCache).Any(a => a.Key == "cache_faible"), "un cache effondré est signalé");

        Console.WriteLine("\nExport de dépense");
        var csv = Path.Combine(Path.GetTempPath(), "spend-" + Guid.NewGuid().ToString("N")[..6] + ".csv");
        File.WriteAllText(csv, """
            User Email,Account UUID,Product,Model,Model Family,Request Count,Prompt Tokens,Completion Tokens,Net Spend (USD),Gross Spend (USD)
            a@x.fr,u1,Claude Code,claude-opus-5,Opus,10,"1,000,000",50000,0.00,12.50
            a@x.fr,u1,Chat,claude-haiku-4-5,Haiku,5,100000,5000,0.00,0.40
            b@x.fr,u2,Claude Code,claude-sonnet-4-5,Sonnet,3,200000,9000,0.00,1.10
            """);
        try
        {
            var warnings = new List<string>();
            var rows = SpendReportReader.Read(csv, warnings);
            check(warnings.Count == 0, "l'export standard est lu sans avertissement");
            check(rows.Count == 3, $"3 lignes lues (obtenu {rows.Count})");
            check(rows[0].PromptTokens == 1_000_000, $"les séparateurs de milliers sont gérés (obtenu {rows[0].PromptTokens})");
            check(rows[0].Model == "claude-opus-5",
                  $"« Model » gagne sur « Model Family » (obtenu {rows[0].Model})");
            check(rows[0].GrossSpend == 12.50m, "la dépense est lue");

            var learners = SpendAggregator.ByLearner(rows);
            check(learners.Count == 2, $"2 personnes (obtenu {learners.Count})");
            check(learners[0].Email == "a@x.fr", "le plus gros consommateur en tête");
            check(learners[0].TotalTokens == 1_155_000, $"les lignes d'une personne sont cumulées (obtenu {learners[0].TotalTokens})");
            check(learners[0].ByProduct.Count == 2, "la répartition par produit est conservée");
            check(learners[0].ShareOf(ModelFamily.Opus) > learners[0].ShareOf(ModelFamily.Haiku),
                  "la répartition par famille est calculée");
        }
        finally { try { File.Delete(csv); } catch { } }

        Console.WriteLine("\nEn-têtes inattendus");
        var odd = Path.Combine(Path.GetTempPath(), "odd-" + Guid.NewGuid().ToString("N")[..6] + ".csv");
        File.WriteAllText(odd, "colonne1,colonne2\nx,y\n");
        try
        {
            var warnings = new List<string>();
            var rows = SpendReportReader.Read(odd, warnings);
            check(rows.Count == 0 && warnings.Count > 0,
                  "un fichier qui n'est pas l'export attendu le dit au lieu de produire des chiffres faux");
        }
        finally { try { File.Delete(odd); } catch { } }
    }

    private static DateOnly ThisMonday()
    {
        var d = DateTimeOffset.UtcNow.UtcDateTime.Date;
        return DateOnly.FromDateTime(d.AddDays(-(((int)d.DayOfWeek + 6) % 7)));
    }

    private static WeekUsage Week(DateOnly monday, long read, int days, double cacheShare = 0.8)
    {
        var w = new WeekUsage { Week = monday.ToString("yyyy-MM-dd"), MondayOf = monday, Tasks = 6 };
        for (var i = 0; i < days; i++) w.ActiveDays.Add(monday.AddDays(i));
        w.Models["claude-opus-5"] = new ModelUsage(ModelFamily.Opus, "claude-opus-5")
        {
            Calls = 20,
            CacheReadTokens = (long)(read * cacheShare),
            InputTokens = read - (long)(read * cacheShare),
            OutputTokens = read / 50,
        };
        return w;
    }

    private static SegmentedTask Task(params string[] tools)
    {
        var task = new SegmentedTask { Id = "t", SessionId = "s" };
        var turn = new Turn { SessionId = "s", Prompt = "p" };
        var at = DateTimeOffset.UtcNow;
        foreach (var name in tools)
            turn.ToolCalls.Add(new ToolCall { Id = Guid.NewGuid().ToString("N")[..6], Name = name, CalledAt = at });
        task.Turns.Add(turn);
        return task;
    }

    private static TranscriptSession Session(DateTimeOffset at)
    {
        var s = new TranscriptSession { SessionId = "sess", StartedAt = at, EndedAt = at.AddHours(1) };
        var turn = new Turn
        {
            SessionId = "sess", Prompt = "Ajoute un test",
            StartedAt = at, EndedAt = at.AddMinutes(20), StopReason = "end_turn",
        };
        turn.ToolCalls.Add(new ToolCall { Id = "c1", Name = "Edit", CalledAt = at });
        turn.Steps.Add(new AssistantStep
        {
            At = at, Model = "claude-opus-5",
            InputTokens = 150_000, CacheReadTokens = 150_000, OutputTokens = 5_000,
        });
        turn.Steps.Add(new AssistantStep
        {
            At = at.AddMinutes(5), Model = "claude-haiku-4-5",
            InputTokens = 50_000, CacheReadTokens = 50_000, OutputTokens = 1_000,
        });
        s.Turns.Add(turn);
        return s;
    }
}
