using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du parseur. Le fixture est écrit à la main d'après la forme
/// observée sur un vrai transcript : mêmes types de lignes, mêmes noms de
/// champs, même imbrication. Il vaut contrat — le jour où Claude Code change
/// de format, c'est ici que ça doit casser, pas en production.
/// </summary>
public static class TranscriptTests
{
    public static int Run(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "coachingia-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "session-a.jsonl");
            File.WriteAllText(path, Fixture(), Encoding.UTF8);

            var report = new ParseReport();
            var records = TranscriptReader.Read(path, report).ToList();

            Console.WriteLine("Lecture tolérante");
            check(report.LinesUnreadable == 2, $"les 2 lignes corrompues sont comptées (obtenu {report.LinesUnreadable})");
            check(report.RecordsKept == records.Count && records.Count > 0, "les lignes valides sont conservées");
            check(!report.LooksBroken || report.UnreadableRate > 0, "le taux d'illisibles est calculé");
            check(report.RecordTypes.ContainsKey("assistant") && report.RecordTypes.ContainsKey("user"),
                  "les types de lignes sont inventoriés");

            Console.WriteLine("\nDétection du prompt humain");
            var humans = records.Count(r => SessionBuilder.IsHumanPrompt(r, out _));
            check(humans == 3, $"3 prompts humains sur 6 lignes user (obtenu {humans})");

            Console.WriteLine("\nReconstruction de la session");
            var sessions = SessionBuilder.Build(records);
            check(sessions.Count == 1, $"une seule session (obtenu {sessions.Count})");
            var s = sessions[0];
            check(s.Turns.Count == 4, $"4 tours, dont un de sous-agent (obtenu {s.Turns.Count})");
            check(s.Cwd == "/home/dev/projet" && s.GitBranch == "main", "les métadonnées de session sont lues");

            var t0 = s.Turns[0];
            check(t0.ToolCalls.Count == 2, $"2 appels d'outils sur le premier tour (obtenu {t0.ToolCalls.Count})");
            check(t0.ToolCalls[0].Name == "Grep" && t0.ToolCalls[1].Name == "Bash", "les outils sont lus dans l'ordre");
            check(t0.Completed, "le tour se termine sur end_turn");
            check(t0.Skills.Contains("engineering:testing-strategy"), "la skill attribuée est capturée");

            Console.WriteLine("\nJetons et cache");
            check(t0.PeakInputTokens == 12_000 + 3_000 + 45_000,
                  $"le pic de contexte additionne entrée, cache lu et cache écrit (obtenu {t0.PeakInputTokens})");
            check(t0.Steps.Sum(x => x.CacheReadTokens) == 45_000 + 46_000,
                  $"le cache lu est cumulé sur les deux appels du tour (obtenu {t0.Steps.Sum(x => x.CacheReadTokens)})");

            Console.WriteLine("\nÉchecs d'outils");
            var bash = t0.ToolCalls[1];
            check(bash.Failed, "un code de sortie non nul marque l'échec");
            check(bash.FailureReason == "code=1", $"la raison de l'échec est conservée (obtenu {bash.FailureReason})");
            check(bash.DurationMs is > 0, "la durée vient de durationMs ou de l'écart des horodatages");
            check(!t0.ToolCalls[0].Failed, "un outil qui réussit n'est pas marqué en échec");

            Console.WriteLine("\nSilences et durée active");
            var slow = s.Turns.First(x => x.Prompt.StartsWith("Documente"));
            check(slow.Duration.TotalMinutes > 60, $"le tour dure longtemps au mur ({slow.Duration.TotalMinutes:F0} min)");
            check(slow.ActiveDuration.TotalMinutes < 20,
                  $"mais peu en temps actif, silence retiré ({slow.ActiveDuration.TotalMinutes:F0} min)");

            Console.WriteLine("\nSous-agents");
            var agentTurn = s.Turns.FirstOrDefault(t => t.IsSidechain);
            check(agentTurn is not null, "un tour de sous-agent est reconnu");
            check(agentTurn is not null && agentTurn.FinalMessage.Contains("exploration"),
                  "le travail du sous-agent ne se mélange pas au tour humain");
            check(!s.Turns[1].FinalMessage.Contains("exploration"),
                  "et le tour humain ne récupère pas la sortie du sous-agent");

            Console.WriteLine("\nDécoupe en tâches");
            var segmenter = new TaskSegmenter();
            var tasks = segmenter.Segment(s);
            check(tasks.Count == 2, $"2 tâches : la relance corrective ne compte pas (obtenu {tasks.Count})");
            check(tasks[0].Turns.Count == 2, $"le prompt correctif reste dans la première tâche (obtenu {tasks[0].Turns.Count})");
            check(tasks[0].Decisions[1].Contains("corrective"), $"la décision est justifiée : {tasks[0].Decisions[1]}");
            check(tasks[0].Title.StartsWith("Ajoute un test"), $"le titre vient du premier prompt : {tasks[0].Title}");

            var (isNew, reason) = segmenter.IsNewTask(
                new Turn { SessionId = "x", Prompt = "Refais la migration", StartedAt = DateTimeOffset.UtcNow },
                new Turn { SessionId = "x", Prompt = "précédent", EndedAt = DateTimeOffset.UtcNow, StopReason = "end_turn" });
            check(!isNew && reason.Contains("corrective"), "« Refais » est une correction, pas un nouveau sujet");

            var (isNew2, _) = segmenter.IsNewTask(
                new Turn { SessionId = "x", Prompt = "Documente l'API publique du service de facturation", StartedAt = DateTimeOffset.UtcNow },
                new Turn { SessionId = "x", Prompt = "précédent", EndedAt = DateTimeOffset.UtcNow, StopReason = "end_turn" });
            check(isNew2, "un prompt autonome ouvre bien une tâche");

            var (isNew3, reason3) = segmenter.IsNewTask(
                new Turn { SessionId = "x", Prompt = "Autre sujet complet et détaillé sur la facturation", StartedAt = DateTimeOffset.UtcNow },
                new Turn { SessionId = "x", Prompt = "p", EndedAt = DateTimeOffset.UtcNow.AddHours(-3), StopReason = "end_turn" });
            check(isNew3 && reason3.Contains("silence"), "un long silence ouvre une tâche");

            Console.WriteLine("\nSignaux");
            var extractor = new SignalExtractor { ContextWindow = 200_000 };
            var signals = extractor.ForTask(tasks[0], s);
            double Val(string key) => signals.First(x => x.Key == key).Value;
            string Why(string key) => signals.First(x => x.Key == key).Evidence;

            check(Val("rework_ratio") > 0, "la reprise est comptée");
            check(Val("has_acceptance_criteria") == 1,
                  $"le critère d'acceptation du prompt est détecté — {Why("has_acceptance_criteria")}");
            check(Val("tool_failure_rate") > 0, "l'échec d'outil remonte dans le signal du palier 3");
            check(Val("verification_present") == 1, $"la vérification est repérée — {Why("verification_present")}");
            check(Math.Abs(Val("cache_read_ratio") - 0.75) < 0.02,
                  $"le taux de cache est calculé (obtenu {Val("cache_read_ratio"):F2})");
            check(signals.All(x => x.Evidence.Length > 0), "chaque signal porte la phrase qui l'explique");

            var inProgress = extractor.ForTask(tasks[1], s);
            check(double.IsNaN(inProgress.First(x => x.Key == "first_try_success").Value),
                  "une tâche en cours ne compte pas comme un échec");

            Console.WriteLine("\nEmission des spans");
            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == SpanFactory.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);

            var ingestor = new TranscriptIngestor(new HarnessOptions { LearnerId = "anthony", Surface = "vscode" });
            var result = ingestor.Ingest(sessions);

            check(result.Sessions == 1 && result.Tasks == 2, $"l'ingestion rend le bon compte : {result}");
            check(captured.Any(a => a.OperationName == "turn" && a.GetTagItem(OI.SpanKind)?.ToString() == OI.Kind.Agent),
                  "le tour de sous-agent sort en span AGENT");
            var sessionSpan = captured.Last(a => a.OperationName == "session");
            var taskSpan = captured.First(a => a.OperationName == "task");
            var toolSpan = captured.First(a => a.OperationName.StartsWith("tool."));
            check(taskSpan.ParentSpanId == sessionSpan.SpanId, "la tâche est enfant de la session");
            check(captured.Any(a => a.OperationName == "turn"), "les tours produisent des spans");
            check(toolSpan.GetTagItem(Coach.Source)?.ToString() == "transcript",
                  "les spans du lot sont marqués comme venant du transcript");
            check(sessionSpan.StartTimeUtc.Year == 2026, "les spans portent la date d'origine, pas celle du rejeu");
            check(taskSpan.GetTagItem("signal.rework_ratio") is not null, "les signaux voyagent sur le span de tâche");
            check(taskSpan.GetTagItem("signal.rework_ratio.why") is not null, "et leur justification aussi");

            Console.WriteLine("\nDossier vide");
            var empty = TranscriptReader.FindTranscripts(Path.Combine(dir, "nexistepas")).ToList();
            check(empty.Count == 0, "un dossier absent ne lève pas d'exception");

            return 0;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* nettoyage au mieux */ } }
    }

    private static string Fixture()
    {
        var b = new StringBuilder();
        void Line(object o) => b.AppendLine(JsonSerializer.Serialize(o));
        const string sid = "sess-a";
        var t = new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);
        string At(int min) => t.AddMinutes(min).ToString("O");

        // Un prompt humain avec un critère d'acceptation explicite.
        Line(new { type = "user", sessionId = sid, uuid = "u1", timestamp = At(0), cwd = "/home/dev/projet",
                   gitBranch = "main", version = "2.1.240", promptId = "p1", origin = new { kind = "human" },
                   message = new { role = "user", content = "Ajoute un test de non-régression sur le parseur. Il doit passer au vert avant la fin." } });

        Line(new { type = "assistant", sessionId = sid, uuid = "a1", timestamp = At(1), cwd = "/home/dev/projet",
                   gitBranch = "main", version = "2.1.240", attributionSkill = "engineering:testing-strategy",
                   message = new { model = "claude-opus-5", stop_reason = "tool_use",
                       usage = new { input_tokens = 12000, cache_read_input_tokens = 45000, cache_creation_input_tokens = 3000, output_tokens = 400 },
                       content = new object[] {
                           new { type = "thinking", thinking = "…" },
                           new { type = "tool_use", id = "tu1", name = "Grep", input = new { pattern = "ParseHook" } },
                           new { type = "tool_use", id = "tu2", name = "Bash", input = new { command = "dotnet test" } } } } });

        Line(new { type = "user", sessionId = sid, uuid = "u2", timestamp = At(2), version = "2.1.240",
                   message = new { role = "user", content = new object[] {
                       new { type = "tool_result", tool_use_id = "tu1", content = "4 matches" } } },
                   toolUseResult = new { durationMs = 180.0 } });

        // Échec signalé par le code de sortie, pas par is_error.
        Line(new { type = "user", sessionId = sid, uuid = "u3", timestamp = At(3), version = "2.1.240",
                   message = new { role = "user", content = new object[] {
                       new { type = "tool_result", tool_use_id = "tu2", content = "1 test failed" } } },
                   toolUseResult = new { stdout = "…", stderr = "", code = 1, interrupted = false } });

        b.AppendLine("{ceci n'est pas du json");                       // ligne corrompue 1

        Line(new { type = "assistant", sessionId = sid, uuid = "a2", timestamp = At(4), version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "end_turn",
                       usage = new { input_tokens = 13000, cache_read_input_tokens = 46000, cache_creation_input_tokens = 0, output_tokens = 300 },
                       content = new object[] { new { type = "text", text = "Test ajouté, il passe." } } } });

        // Injection système : ne doit pas ouvrir de tour.
        Line(new { type = "user", sessionId = sid, uuid = "u4", timestamp = At(5), isMeta = true, version = "2.1.240",
                   message = new { role = "user", content = "<system-reminder>rappel interne</system-reminder>" } });

        // Relance corrective : même tâche.
        Line(new { type = "user", sessionId = sid, uuid = "u5", timestamp = At(6), version = "2.1.240",
                   promptId = "p2", origin = new { kind = "human" },
                   message = new { role = "user", content = "Non, le test ne couvre pas le cas des lignes vides." } });

        Line(new { type = "assistant", sessionId = sid, uuid = "a3", timestamp = At(7), version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "end_turn",
                       usage = new { input_tokens = 14000, cache_read_input_tokens = 47000, cache_creation_input_tokens = 0, output_tokens = 200 },
                       content = new object[] { new { type = "text", text = "Corrigé." } } } });

        // Un tour de sous-agent.
        Line(new { type = "assistant", sessionId = sid, uuid = "a4", timestamp = At(8), isSidechain = true, version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "end_turn",
                       usage = new { input_tokens = 500, cache_read_input_tokens = 0, cache_creation_input_tokens = 0, output_tokens = 50 },
                       content = new object[] { new { type = "text", text = "exploration terminée" } } } });

        b.AppendLine("");                                              // ligne vide : ignorée sans compter
        b.AppendLine("[1,2,3]");                                       // ligne corrompue 2 (JSON valide, mais pas un objet)

        // Nouvelle tâche, laissée en cours, avec un long silence au milieu.
        Line(new { type = "user", sessionId = sid, uuid = "u6", timestamp = At(200), version = "2.1.240",
                   promptId = "p3", origin = new { kind = "human" },
                   message = new { role = "user", content = "Documente l'API publique du service de facturation" } });

        Line(new { type = "assistant", sessionId = sid, uuid = "a5", timestamp = At(205), version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "tool_use",
                       usage = new { input_tokens = 9000, cache_read_input_tokens = 1000, cache_creation_input_tokens = 0, output_tokens = 120 },
                       content = new object[] { new { type = "tool_use", id = "tu3", name = "Read", input = new { file_path = "Api.cs" } } } } });

        Line(new { type = "assistant", sessionId = sid, uuid = "a6", timestamp = At(320), version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "tool_use",
                       usage = new { input_tokens = 9500, cache_read_input_tokens = 1200, cache_creation_input_tokens = 0, output_tokens = 90 },
                       content = new object[] { new { type = "tool_use", id = "tu4", name = "Write", input = new { file_path = "API.md" } } } } });

        return b.ToString();
    }
}
