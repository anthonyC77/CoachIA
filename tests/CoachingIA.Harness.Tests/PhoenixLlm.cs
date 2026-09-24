using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du span LLM émis au hook Stop/StopFailure (spec §4.2) : présence
/// de llm.model_name et des compteurs llm.token_count.* sous le span de tour,
/// repli silencieux quand le transcript manque ou est illisible, et respect de
/// CaptureContent = false. Le fixture JSONL est écrit à la main, dans le même
/// esprit que TranscriptTests.Fixture().
/// </summary>
public static class PhoenixLlmTests
{
    public static void Run(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "coachingia-tests-llm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == SpanFactory.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);

            var options = new HarnessOptions { LearnerId = "anthony", Surface = "vscode" };
            using var registry = new SessionRegistry(options.SessionIdleTimeout);
            var factory = new SpanFactory(registry, options);

            static Activity? Turn(List<Activity> caps, string sessionId)
                => caps.LastOrDefault(a => a.OperationName == "turn" && Tag(a, OI.SessionId) == sessionId);
            static Activity? Llm(List<Activity> caps, string sessionId)
                => caps.LastOrDefault(a => a.OperationName == "llm" && Tag(a, OI.SessionId) == sessionId);
            static string? Tag(Activity a, string key) => a.GetTagItem(key)?.ToString();

            Console.WriteLine("Hook Stop avec transcript_path");
            const string s1 = "llm-s1";
            var path1 = Path.Combine(dir, "s1.jsonl");
            File.WriteAllText(path1, TurnLines(s1, "p1", "Corrige le bug", At(0),
                model: "claude-opus-5", inputTokens: 1000, cacheRead: 200, cacheCreate: 50, outputTokens: 300,
                stopReason: "end_turn", finalText: "C'est corrigé."), Encoding.UTF8);

            factory.Handle(new HookEvent { HookEventName = "SessionStart", SessionId = s1 });
            factory.Handle(new HookEvent { HookEventName = "UserPromptSubmit", SessionId = s1, PromptId = "p1", UserPrompt = "Corrige le bug" });
            factory.Handle(new HookEvent
            {
                HookEventName = "Stop", SessionId = s1, PromptId = "p1", TranscriptPath = path1,
                StopReason = "end_turn", LastAssistantMessage = "C'est corrigé.",
            });

            var turn1 = Turn(captured, s1);
            var llm1 = Llm(captured, s1);
            check(turn1 is not null, "le span de tour sort toujours, transcript_path présent ou non");
            check(llm1 is not null, "un hook Stop avec transcript_path fait émettre un span LLM");
            check(llm1 is not null && Tag(llm1, OI.SpanKind) == OI.Kind.Llm,
                  $"le span LLM porte openinference.span.kind = LLM (obtenu {Tag(llm1!, OI.SpanKind)})");
            check(llm1 is not null && turn1 is not null && llm1.ParentSpanId == turn1.SpanId,
                  "le span LLM est l'enfant du span de tour du même prompt");

            check(llm1 is not null && Tag(llm1, "llm.model_name") == "claude-opus-5",
                  $"llm.model_name vient du dernier pas du tour (obtenu {Tag(llm1!, "llm.model_name")})");
            check(llm1 is not null && Tag(llm1, "llm.token_count.prompt") == "1250",
                  $"llm.token_count.prompt est le pic d'entrée + cache (obtenu {Tag(llm1!, "llm.token_count.prompt")})");
            check(llm1 is not null && Tag(llm1, "llm.token_count.completion") == "300",
                  $"llm.token_count.completion est la sortie du tour (obtenu {Tag(llm1!, "llm.token_count.completion")})");
            check(llm1 is not null && Tag(llm1, "llm.token_count.prompt_details.cache_read") == "200",
                  $"llm.token_count.prompt_details.cache_read est le cache lu (obtenu {Tag(llm1!, "llm.token_count.prompt_details.cache_read")})");

            Console.WriteLine("\nHook StopFailure avec transcript_path");
            const string s2 = "llm-s2";
            var path2 = Path.Combine(dir, "s2.jsonl");
            File.WriteAllText(path2, TurnLines(s2, "p1", "Lance les tests", At(0),
                model: "claude-opus-5", inputTokens: 500, cacheRead: 0, cacheCreate: 0, outputTokens: 80,
                stopReason: "tool_use", finalText: ""), Encoding.UTF8);

            factory.Handle(new HookEvent { HookEventName = "SessionStart", SessionId = s2 });
            factory.Handle(new HookEvent { HookEventName = "UserPromptSubmit", SessionId = s2, PromptId = "p1", UserPrompt = "Lance les tests" });
            factory.Handle(new HookEvent
            {
                HookEventName = "StopFailure", SessionId = s2, PromptId = "p1", TranscriptPath = path2,
                ErrorType = "timeout", ErrorMessage = "délai dépassé",
            });

            check(Llm(captured, s2) is not null, "un hook StopFailure avec transcript_path émet lui aussi le span LLM");

            Console.WriteLine("\nHook Stop sans transcript_path");
            const string s3 = "llm-s3";
            factory.Handle(new HookEvent { HookEventName = "SessionStart", SessionId = s3 });
            factory.Handle(new HookEvent { HookEventName = "UserPromptSubmit", SessionId = s3, PromptId = "p1", UserPrompt = "Explique le code" });
            factory.Handle(new HookEvent { HookEventName = "Stop", SessionId = s3, PromptId = "p1", StopReason = "end_turn" });

            check(Llm(captured, s3) is null, "sans transcript_path, aucun span LLM n'est émis");
            check(Turn(captured, s3) is not null, "le tour se ferme normalement malgré l'absence de transcript_path");

            Console.WriteLine("\nTranscript introuvable puis illisible");
            const string s4 = "llm-s4";
            factory.Handle(new HookEvent { HookEventName = "SessionStart", SessionId = s4 });
            factory.Handle(new HookEvent { HookEventName = "UserPromptSubmit", SessionId = s4, PromptId = "p1", UserPrompt = "Premier tour" });
            var absent = Path.Combine(dir, "n-existe-pas.jsonl");
            var exAbsent = Record(() => factory.Handle(new HookEvent
            {
                HookEventName = "Stop", SessionId = s4, PromptId = "p1", TranscriptPath = absent, StopReason = "end_turn",
            }));
            check(exAbsent is null, $"un transcript_path introuvable ne lève aucune exception (obtenu {exAbsent})");
            check(Turn(captured, s4) is not null, "le span de tour ne disparaît pas quand le transcript est introuvable");

            factory.Handle(new HookEvent { HookEventName = "UserPromptSubmit", SessionId = s4, PromptId = "p2", UserPrompt = "Second tour" });
            var illisible = Path.Combine(dir, "s4-illisible.jsonl");
            File.WriteAllText(illisible, "{ceci n'est pas du json\n", Encoding.UTF8);
            var exIllisible = Record(() => factory.Handle(new HookEvent
            {
                HookEventName = "Stop", SessionId = s4, PromptId = "p2", TranscriptPath = illisible, StopReason = "end_turn",
            }));
            check(exIllisible is null, $"un transcript avec une ligne illisible ne lève aucune exception (obtenu {exIllisible})");
            check(captured.Count(a => a.OperationName == "turn" && Tag(a, OI.SessionId) == s4) == 2,
                  "les deux tours de la session se ferment malgré un transcript illisible");

            Console.WriteLine("\nDécalage mémorisé entre deux lectures");
            const string s5 = "llm-s5";
            var path5 = Path.Combine(dir, "s5.jsonl");
            var premierTour = TurnLines(s5, "p1", "Premier prompt", At(0),
                model: "claude-opus-5", inputTokens: 800, cacheRead: 100, cacheCreate: 0, outputTokens: 150,
                stopReason: "end_turn", finalText: "ok");
            File.WriteAllText(path5, premierTour, Encoding.UTF8);
            var len1 = new FileInfo(path5).Length;

            var tail = new TurnTail();
            var r1 = tail.Read(s5, "p1", path5);
            var offset1 = tail.OffsetOf(s5);
            check(r1 is not null, "la première lecture de la queue trouve le premier tour");
            check(offset1 > 0, $"le décalage mémorisé après la première lecture est strictement positif (obtenu {offset1})");
            check(offset1 == len1, $"le décalage mémorisé correspond à la taille du fichier lu (obtenu {offset1}, attendu {len1})");
            check(r1 is not null && r1.BytesRead == len1,
                  $"la première lecture a consommé tout le fichier, {len1} octets (obtenu {r1?.BytesRead})");

            var secondTour = TurnLines(s5, "p2", "Second prompt", At(10),
                model: "claude-sonnet-5", inputTokens: 400, cacheRead: 0, cacheCreate: 0, outputTokens: 90,
                stopReason: "end_turn", finalText: "ok2");
            File.AppendAllText(path5, secondTour, Encoding.UTF8);
            var len2 = new FileInfo(path5).Length;

            var r2 = tail.Read(s5, "p2", path5);
            var offset2 = tail.OffsetOf(s5);
            check(r2 is not null && r2.Model == "claude-sonnet-5",
                  $"la seconde lecture ne retrouve que le second tour (obtenu {r2?.Model})");
            check(r2 is not null && r2.BytesRead == len2 - offset1,
                  $"la seconde lecture ne relit que les octets ajoutés depuis le premier décalage " +
                  $"(obtenu {r2?.BytesRead}, attendu {len2 - offset1}, pas {len2})");
            check(offset2 == len2, $"le nouveau décalage suit la taille du fichier (obtenu {offset2}, attendu {len2})");

            Console.WriteLine("\nLigne coupée en cours d'écriture par Claude Code");
            const string s7 = "llm-s7";
            var path7 = Path.Combine(dir, "s7.jsonl");
            var premierTour7 = TurnLines(s7, "p1", "Premier prompt", At(0),
                model: "claude-opus-5", inputTokens: 900, cacheRead: 0, cacheCreate: 0, outputTokens: 100,
                stopReason: "end_turn", finalText: "ok");
            File.WriteAllText(path7, premierTour7, Encoding.UTF8);

            var tail7 = new TurnTail();
            var r7a = tail7.Read(s7, "p1", path7);
            var offset7a = tail7.OffsetOf(s7);
            check(r7a is not null && r7a.Model == "claude-opus-5",
                  "le premier tour, écrit en entier, est retrouvé avant toute écriture partielle");
            check(offset7a > 0, $"le décalage avance après ce premier tour complet (obtenu {offset7a})");

            // Claude Code écrit la ligne « user » du second tour en deux temps : on
            // ne voit d'abord qu'un fragment, coupé avant tout saut de ligne — donc
            // sans aucun \n final, exactement le cas que Claude Code produit quand
            // il écrit un enregistrement au fil de l'eau.
            var secondTour7 = TurnLines(s7, "p2", "Second prompt", At(10),
                model: "claude-sonnet-5", inputTokens: 400, cacheRead: 0, cacheCreate: 0, outputTokens: 90,
                stopReason: "end_turn", finalText: "ok2");
            var firstNewline7 = secondTour7.IndexOf('\n');
            var cut = firstNewline7 / 2;
            check(cut > 0 && !secondTour7[..cut].Contains('\n'),
                  "le fragment de test ne contient aucun saut de ligne : la ligne est coupée en plein milieu");
            File.AppendAllText(path7, secondTour7[..cut], Encoding.UTF8);

            var r7b = tail7.Read(s7, "p2", path7);
            var offset7b = tail7.OffsetOf(s7);
            check(r7b is null, $"une ligne encore à moitié écrite, sans \\n final, ne rend aucun tour (obtenu {(r7b is null ? "null" : r7b.Model)})");
            check(offset7b == offset7a,
                  $"le décalage ne bouge pas tant qu'aucune ligne n'est complète (obtenu {offset7b}, attendu {offset7a}) — " +
                  "sur le code d'avant la correction, il aurait avalé les octets orphelins de la ligne coupée");

            // L'écriture se termine : le reste de la ligne « user », son \n, puis la
            // ligne « assistant » complète (avec ses jetons) arrivent d'un coup.
            File.AppendAllText(path7, secondTour7[cut..], Encoding.UTF8);

            var r7c = tail7.Read(s7, "p2", path7);
            var offset7c = tail7.OffsetOf(s7);
            check(r7c is not null && r7c.Model == "claude-sonnet-5",
                  $"une fois l'écriture terminée, le second tour est retrouvé à la lecture suivante " +
                  $"(obtenu {(r7c is null ? "aucun tour" : r7c.Model)}) — c'est le point que ratait le code d'avant la correction, " +
                  "qui avait déjà dépassé le début de la ligne coupée");
            check(r7c is not null && r7c.PromptTokens == 400,
                  $"et porte les bons compteurs du second tour, pas ceux du premier (obtenu {r7c?.PromptTokens})");
            check(offset7c > offset7a,
                  $"le décalage avance enfin au-delà du premier tour, une fois la ligne complète (obtenu {offset7c}, attendu > {offset7a})");

            Console.WriteLine("\nSegment sans aucun saut de ligne complet");
            const string s8 = "llm-s8";
            var path8 = Path.Combine(dir, "s8.jsonl");
            File.WriteAllText(path8, "{\"type\":\"user\",\"sessionId\":\"" + s8 + "\",\"promptId\":\"p1\"", Encoding.UTF8); // aucun \n
            var tail8 = new TurnTail();
            Exception? ex8 = null;
            TurnTail.Result? r8 = null;
            try { r8 = tail8.Read(s8, "p1", path8); }
            catch (Exception e) { ex8 = e; }
            check(ex8 is null, $"un segment sans aucun saut de ligne complet ne lève pas d'exception (obtenu {ex8})");
            check(r8 is null, "et ne rend aucun tour, faute de ligne complète");
            check(tail8.OffsetOf(s8) == 0, $"le décalage reste à zéro tant qu'aucune ligne n'est complète (obtenu {tail8.OffsetOf(s8)})");

            Console.WriteLine("\nPromptId fourni mais absent du segment (ligne « user » illisible)");
            const string s9 = "llm-s9";
            var path9 = Path.Combine(dir, "s9.jsonl");
            var b9 = new StringBuilder();
            // La ligne « user » de p1 est illisible : elle disparaît du modèle
            // reconstruit par SessionBuilder, p1 ne devient donc jamais un tour.
            b9.AppendLine("{ceci n'est pas du json, la ligne user de p1 est perdue");
            // Un second tour, p2, complet et sans rapport, suit dans le même segment.
            b9.Append(TurnLines(s9, "p2", "Tour valide et sans rapport", At(0),
                model: "claude-haiku-5", inputTokens: 40, cacheRead: 10, cacheCreate: 0, outputTokens: 5,
                stopReason: "end_turn", finalText: "ok"));
            File.WriteAllText(path9, b9.ToString(), Encoding.UTF8);

            var tail9 = new TurnTail();
            var r9 = tail9.Read(s9, "p1", path9);
            check(r9 is null,
                  "un promptId fourni mais absent du segment ne retombe pas sur le dernier tour lu " +
                  $"(obtenu {(r9 is null ? "null" : $"model={r9.Model} prompt={r9.PromptTokens} completion={r9.CompletionTokens}")}) " +
                  "— sur le code d'avant la correction, il aurait rendu le tour p2 (claude-haiku-5, 50/5) sous l'étiquette p1");

            // Instance fraîche : tail9 a déjà consommé le fichier jusqu'à la fin en
            // cherchant p1 (les octets sont consommés qu'on trouve le tour ou non).
            var r9b = new TurnTail().Read(s9, "p2", path9);
            check(r9b is not null && r9b.Model == "claude-haiku-5",
                  $"le bon promptId, lui, retrouve bien son propre tour (obtenu {r9b?.Model})");

            Console.WriteLine("\nCaptureContent = false");
            var options2 = new HarnessOptions { LearnerId = "anthony", Surface = "vscode", CaptureContent = false };
            using var registry2 = new SessionRegistry(options2.SessionIdleTimeout);
            var factory2 = new SpanFactory(registry2, options2);

            const string s6 = "llm-s6";
            var path6 = Path.Combine(dir, "s6.jsonl");
            File.WriteAllText(path6, TurnLines(s6, "p1", "Ne pas fuiter le texte", At(0),
                model: "claude-opus-5", inputTokens: 700, cacheRead: 0, cacheCreate: 0, outputTokens: 120,
                stopReason: "end_turn", finalText: "Réponse confidentielle."), Encoding.UTF8);

            factory2.Handle(new HookEvent { HookEventName = "SessionStart", SessionId = s6 });
            factory2.Handle(new HookEvent { HookEventName = "UserPromptSubmit", SessionId = s6, PromptId = "p1", UserPrompt = "Ne pas fuiter le texte" });
            factory2.Handle(new HookEvent
            {
                HookEventName = "Stop", SessionId = s6, PromptId = "p1", TranscriptPath = path6,
                StopReason = "end_turn", LastAssistantMessage = "Réponse confidentielle.",
            });

            var llm6 = Llm(captured, s6);
            check(llm6 is not null, "le span LLM sort même quand CaptureContent vaut false");
            check(llm6 is not null && Tag(llm6, OI.InputValue) is null,
                  "CaptureContent = false : le span LLM ne porte pas input.value");
            check(llm6 is not null && Tag(llm6, OI.OutputValue) is null,
                  "CaptureContent = false : le span LLM ne porte pas output.value");
            check(llm6 is not null && Tag(llm6, "llm.token_count.prompt") == "700",
                  $"CaptureContent = false : les compteurs de jetons restent présents (obtenu {Tag(llm6!, "llm.token_count.prompt")})");
            check(llm6 is not null && Tag(llm6, "llm.token_count.completion") == "120",
                  $"CaptureContent = false : llm.token_count.completion reste présent (obtenu {Tag(llm6!, "llm.token_count.completion")})");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* nettoyage au mieux */ }
        }
    }

    private static Exception? Record(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    /// <summary>Un tour minimal (prompt humain + réponse d'assistant), au format d'un vrai transcript.</summary>
    private static string TurnLines(
        string sessionId, string promptId, string prompt, DateTimeOffset at,
        string model, long inputTokens, long cacheRead, long cacheCreate, long outputTokens,
        string stopReason, string finalText)
    {
        var b = new StringBuilder();
        void Line(object o) => b.AppendLine(JsonSerializer.Serialize(o));

        Line(new
        {
            type = "user", sessionId, uuid = Guid.NewGuid().ToString(), timestamp = at.ToString("O"),
            promptId, origin = new { kind = "human" },
            message = new { role = "user", content = prompt },
        });

        Line(new
        {
            type = "assistant", sessionId, uuid = Guid.NewGuid().ToString(), timestamp = at.AddSeconds(1).ToString("O"),
            message = new
            {
                model, stop_reason = stopReason,
                usage = new
                {
                    input_tokens = inputTokens, cache_read_input_tokens = cacheRead,
                    cache_creation_input_tokens = cacheCreate, output_tokens = outputTokens,
                },
                content = new object[] { new { type = "text", text = finalText } },
            },
        });

        return b.ToString();
    }
}
