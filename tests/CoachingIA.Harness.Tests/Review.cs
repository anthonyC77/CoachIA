using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du bilan. Ce qui est vérifié ici tient surtout à des promesses
/// faites à l'apprenant : aucune observation sans exemple, aucune félicitation
/// de politesse, et jamais de jugement sur une semaine encore en cours.
/// </summary>
public static class ReviewTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        var catalog = LensCatalog.Load(lensDir);
        var neutral = new LensWriter(catalog.Resolve("neutre"));
        var sc2 = new LensWriter(catalog.Resolve("starcraft2"));
        var builder = new WeeklyReviewBuilder();

        var thisMonday = ThisMonday();
        var lastWeek = thisMonday.AddDays(-7);
        var before = thisMonday.AddDays(-14);

        Console.WriteLine("Semaine visée");
        var sessions = new List<TranscriptSession>
        {
            Session(At(before, 1), sloppy: true, tasks: 3),
            Session(At(lastWeek, 1), sloppy: true, tasks: 3),
            Session(At(thisMonday, 1), sloppy: true, tasks: 2),
        };
        var review = builder.Build(sessions, null, neutral);
        check(review.Week == UsageAnalyzer.WeekKey(At(lastWeek, 1)),
              $"la dernière semaine close est choisie, pas celle en cours (obtenu {review.Week})");
        check(!review.IsEmpty, "le bilan porte des tâches");

        Console.WriteLine("\nObservations");
        check(review.Observations.Count is > 0 and <= 3,
              $"trois observations au maximum (obtenu {review.Observations.Count})");
        check(review.Observations.Select(o => o.Level).Distinct().Count() == review.Observations.Count,
              "jamais deux observations du même palier");
        check(review.Observations.All(o => o.TaskTitle.Length > 0),
              "chaque observation cite une tâche réelle — c'est la promesse du format");
        check(review.Observations.All(o => o.Evidence.Length > 0),
              "et la phrase qui explique le chiffre");
        check(review.Observations.All(o => o.TaskDate >= lastWeek && o.TaskDate < thisMonday),
              "les exemples viennent bien de la semaine visée");

        Console.WriteLine("\nLa réussite ne se distribue pas");
        check(review.Win is null, "aucune félicitation quand rien n'a progressé");

        var improving = new List<TranscriptSession>
        {
            Session(At(before, 1), sloppy: true, tasks: 3),
            Session(At(lastWeek, 1), sloppy: false, tasks: 3),
        };
        var better = builder.Build(improving, null, neutral);
        check(better.Win is not null, "une vraie progression est saluée");
        check(better.Win is null || better.Win.After > better.Win.Before,
              "et elle va bien dans le bon sens");

        var single = builder.Build([Session(At(lastWeek, 1), sloppy: false, tasks: 2)], null, neutral);
        check(single.Win is null, "sans semaine de comparaison, on ne félicite pas");

        Console.WriteLine("\nSemaine demandée explicitement");
        var targeted = builder.Build(sessions, UsageAnalyzer.WeekKey(At(before, 1)), neutral);
        check(targeted.Week == UsageAnalyzer.WeekKey(At(before, 1)), "--week vise la semaine demandée");

        var absent = builder.Build(sessions, "1999-W01", neutral);
        check(absent.IsEmpty, "une semaine sans tâche rend un bilan vide plutôt qu'une erreur");

        Console.WriteLine("\nRendu");
        var md = ReviewRenderer.ToMarkdown(review, neutral);
        check(md.Contains("## Ce qui a progressé"), "le bilan ouvre sur ce qui a progressé");
        check(md.IndexOf("Ce qui a progressé", StringComparison.Ordinal)
              < md.IndexOf("Ce que les traces montrent", StringComparison.Ordinal),
              "la réussite passe avant les reproches");
        check(md.IndexOf("Ce que les traces montrent", StringComparison.Ordinal)
              < md.IndexOf("Le défi de la semaine", StringComparison.Ordinal),
              "et le défi ferme le bilan");
        check(md.Contains("Par exemple le"), "chaque observation est incarnée par un exemple daté");
        check(!md.Contains(",00 jetons") && !md.Contains("1,234,567"),
              "les nombres sont écrits à la française");

        var mdLens = ReviewRenderer.ToMarkdown(review, sc2);
        check(mdLens.Contains("lentille StarCraft II"), "la lentille est annoncée");
        check(md.Split('\n').Length > 15, "le bilan a de la matière");

        var empty = ReviewRenderer.ToMarkdown(absent, neutral);
        check(empty.Contains("Aucune tâche"), "un bilan vide le dit franchement");
        check(!empty.Contains("Le défi"), "et ne propose pas de défi sur rien");

        Console.WriteLine("\nLa grille des signaux");
        var spec = SignalSpecs.Find("rework_ratio");
        check(spec is not null && !spec.HigherIsBetter, "rework_ratio se lit à l'envers : moins vaut mieux");
        check(spec!.Gap(0.6) > 0 && spec.Gap(0.1) < 0, "l'écart change de signe autour de la cible");
        check(SignalSpecs.Find("harness_breadth")!.Gap(2) > 0, "un signal en valeur absolue s'évalue aussi");
        check(double.IsNaN(SignalSpecs.Find("loop_closure")!.Gap(double.NaN)),
              "un signal indéterminé ne produit pas d'écart");
        check(SignalSpecs.Find("nexistepas") is null, "un signal inconnu ne fait pas tomber la grille");
    }

    internal static DateOnly ThisMonday()
    {
        var d = DateTimeOffset.UtcNow.UtcDateTime.Date;
        return DateOnly.FromDateTime(d.AddDays(-(((int)d.DayOfWeek + 6) % 7)));
    }

    internal static DateTimeOffset At(DateOnly day, int hour)
        => new(day.Year, day.Month, day.Day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Une session fabriquée : « sloppy » produit des tâches sans critère
    /// d'acceptation, sans vérification et avec des reprises ; l'inverse produit
    /// des tâches propres. De quoi faire bouger les signaux dans les deux sens.
    /// </summary>
    internal static TranscriptSession Session(DateTimeOffset start, bool sloppy, int tasks)
    {
        var s = new TranscriptSession { SessionId = "s-" + start.Ticks, StartedAt = start, EndedAt = start.AddHours(2) };
        for (var t = 0; t < tasks; t++)
        {
            var at = start.AddMinutes(t * 30);
            var prompt = sloppy
                ? $"Regarde le module {t}"
                : $"Ajoute un test sur le module {t}. Il doit passer au vert avant la fin.";

            var turn = new Turn
            {
                SessionId = s.SessionId, Prompt = prompt,
                StartedAt = at, EndedAt = at.AddMinutes(8), StopReason = "end_turn",
            };
            turn.Steps.Add(new AssistantStep
            {
                At = at, Model = "claude-opus-5",
                InputTokens = 20_000, CacheReadTokens = 60_000, OutputTokens = 900,
            });
            for (var c = 0; c < 6; c++)
                turn.ToolCalls.Add(new ToolCall { Id = $"c{t}{c}", Name = "Edit", CalledAt = at.AddMinutes(c) });

            if (!sloppy)
                turn.ToolCalls.Add(new ToolCall
                {
                    Id = $"v{t}", Name = "Bash", CalledAt = at.AddMinutes(7),
                    InputJson = "{\"command\":\"dotnet test\"}",
                });
            s.Turns.Add(turn);

            if (sloppy)
            {
                // Une relance corrective : elle reste dans la même tâche et
                // alimente rework_ratio.
                var fix = new Turn
                {
                    SessionId = s.SessionId, Prompt = "Non, ce n'est pas ça",
                    StartedAt = at.AddMinutes(9), EndedAt = at.AddMinutes(12), StopReason = "end_turn",
                };
                fix.Steps.Add(new AssistantStep { At = at.AddMinutes(9), Model = "claude-opus-5", InputTokens = 21_000, CacheReadTokens = 61_000, OutputTokens = 300 });
                s.Turns.Add(fix);
            }
        }
        s.EndedAt = s.Turns[^1].EndedAt;
        return s;
    }
}

/// <summary>
/// Banc d'essai de la critique de prompt. C'est la partie du bilan qui touche
/// directement au texte de l'apprenant : elle doit être exacte sur ce qui
/// manque, et incapable d'inventer quoi que ce soit hors ligne.
/// </summary>
public static class PromptCriticTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Grille de lecture");
        const string vague = "Regarde le module de facturation";
        const string complet = """
            Corrige le calcul de TVA dans src/Billing/Invoice.cs, uniquement dans ce fichier.
            C'est fini quand dotnet test passe au vert, sans casser l'API publique.
            Renvoie le patch. Le taux vient d'une contrainte métier, à savoir la réforme 2024.
            Va jusqu'au bout sans me demander.
            """;

        check(PromptRubric.Missing(vague).Count >= 4, "un prompt vague manque de plusieurs critères");
        check(PromptRubric.Missing(complet).Count == 0,
              $"un prompt complet ne manque de rien (manque : {string.Join(", ", PromptRubric.Missing(complet).Select(m => m.Label))})");
        check(PromptRubric.Coverage(complet) == 1.0, "et sa couverture est pleine");
        check(PromptRubric.Coverage(vague) < 0.5, "celle du prompt vague est basse");
        check(PromptRubric.Missing(vague).First().Essential,
              "les critères essentiels sont signalés en premier");

        Console.WriteLine("\nCritique hors ligne");
        var critic = new HeuristicPromptCritic();
        var ctx = new PromptContext("Facturation", 3, 20, false, false, ["Bash"], new DateOnly(2026, 8, 18));
        var c = critic.Critique(vague, ctx);
        check(c is not null, "un prompt incomplet est critiqué");
        check(c!.Original == vague, "l'original est conservé tel quel");
        check(c.Rewrite.StartsWith(vague), "la proposition part du texte de l'apprenant");
        check(c.Rewrite.Contains('['), "et marque d'un crochet ce qui reste à compléter");
        check(c.Source == "heuristique", "la source est annoncée honnêtement");
        check(c.Cost.Contains("3 reprise"), $"le coût réel est rappelé : {c.Cost}");
        check(c.Cost.Contains("non aboutie"), "y compris l'abandon");

        check(critic.Critique(complet, ctx) is null, "un prompt complet n'est pas critiqué pour le plaisir");

        Console.WriteLine("\nRepli du juge");
        // Un binaire qui n'existe pas : la critique doit retomber sur l'heuristique
        // sans lever, parce qu'un coach ne doit jamais devenir un point de panne.
        var judged = new ClaudePromptCritic(critic, TimeSpan.FromSeconds(5), "binaire-qui-nexiste-pas");
        var fallback = judged.Critique(vague, ctx);
        check(fallback is not null, "le juge absent ne fait pas disparaître la critique");
        check(fallback!.Source == "heuristique", "et la source reste honnête sur ce qui a produit le texte");
        check(judged.LastError is not null, $"l'erreur est retenue pour être expliquée : {judged.LastError}");
        check(judged.Critique(complet, ctx) is null, "un prompt complet n'appelle même pas le juge");

        Console.WriteLine("\nChoix du prompt à critiquer");
        var cheap = Task("Ajoute un test sur le parseur", rework: 0, failed: 0, completed: true);
        var costly = Task("Regarde le module", rework: 4, failed: 2, completed: false);
        var pick = PromptPicker.Pick([(cheap, []), (costly, [])]);
        check(pick?.Task == costly, "le prompt le plus coûteux est retenu");
        check(pick!.Value.Context.ReworkTurns == 4, "avec son coût réel");

        var onlyCheap = PromptPicker.Pick([(cheap, [])]);
        check(onlyCheap is null, "une semaine sans prompt coûteux ne produit pas de critique forcée");
    }

    private static SegmentedTask Task(string prompt, int rework, int failed, bool completed)
    {
        var t = new SegmentedTask { Id = "t", SessionId = "s", Title = prompt };
        var at = DateTimeOffset.UtcNow.AddDays(-2);
        for (var i = 0; i <= rework; i++)
        {
            var turn = new Turn
            {
                SessionId = "s", Prompt = i == 0 ? prompt : "non, pas comme ça",
                StartedAt = at.AddMinutes(i * 2), EndedAt = at.AddMinutes(i * 2 + 1),
                StopReason = i == rework && !completed ? "tool_use" : "end_turn",
            };
            if (i == rework)
                for (var f = 0; f < failed; f++)
                    turn.ToolCalls.Add(new ToolCall { Id = "f" + f, Name = "Bash", CalledAt = at, Failed = true });
            t.Turns.Add(turn);
        }
        return t;
    }
}
