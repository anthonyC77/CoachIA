using System.Text.Json;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai de l'export de runs. La session est fabriquée à la main : lire
/// un vrai transcript rendrait la suite lente, non reproductible et différente
/// sur chaque poste — et ce qu'on veut éprouver ici n'est pas le parseur (il a
/// sa propre suite) mais la PROJECTION.
///
/// La dernière vérification est d'une autre nature : elle compare la liste des
/// types d'événements du C# à celle du miroir TypeScript. Deux définitions du
/// même contrat dérivent toujours ; autant que ce soit un test qui le dise.
/// </summary>
public static class RunExportTests
{
    public static int Run(Action<bool, string> check, string repoRoot)
    {
        var t0 = new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);
        var session = Fabriquer(t0);

        // ── Sans contenu : le réglage prudent, celui qui doit être le défaut ──
        var prudent = new RunExportOptions { CaptureArgs = true, CaptureContent = false };
        var run = RunExporter.Project(session, prudent);
        var evs = run.Events;

        var numerotation = true;
        for (var i = 0; i < evs.Count; i++) if (evs[i].Seq != i) numerotation = false;
        check(numerotation, $"l'export numérote sans trou : {evs.Count} événements, seq de 0 à {evs.Count - 1}");

        // Un compteur et non un ensemble : un agent peut ouvrir deux outils en
        // parallèle, et deux clés identiques ne doivent pas s'annuler.
        var ordonnes = true;
        var ouverts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in evs)
        {
            if (!e.Kind.StartsWith("think.", StringComparison.Ordinal)
                && !e.Kind.StartsWith("tool.", StringComparison.Ordinal)
                && !e.Kind.StartsWith("run.", StringComparison.Ordinal)) continue;

            var cle = Cle(e);
            if (e.Kind.EndsWith(".start", StringComparison.Ordinal))
            {
                ouverts.TryGetValue(cle, out var n);
                ouverts[cle] = n + 1;
            }
            else if (e.Kind.EndsWith(".end", StringComparison.Ordinal))
            {
                if (!ouverts.TryGetValue(cle, out var n) || n == 0) ordonnes = false;
                else ouverts[cle] = n - 1;
            }
        }
        check(ordonnes, "un .start précède toujours son .end, même à horodatage identique");

        var spawns = evs.FindAll(e => e.Kind == "agent.spawn");
        check(spawns.Count == 2,
            $"un appel à Task fait naître un sous-agent : {spawns.Count} agent.spawn (le principal et Explore)");

        var explore = evs.FindAll(e => e.Actor == "Explore" && e.Kind == "tool.start");
        check(explore.Count == 1,
            $"le tour de sous-agent est rattaché à la fenêtre Task qui l'a ouvert : {explore.Count} outil sous « Explore »");

        var fuite = evs.Find(e => e.Payload.TryGetValue("title", out var v)
                                  && v is string s && s.Contains("authentification", StringComparison.Ordinal));
        check(fuite is null, "CaptureContent à false ne laisse passer aucun texte de prompt dans le journal");

        var debut = evs.Find(e => e.Kind == "run.start");
        check(debut?.Ref is { Length: > 8 } && debut.Ref.StartsWith("sha256:", StringComparison.Ordinal),
            $"le contenu écarté laisse quand même son empreinte : ref = {debut?.Ref ?? "(aucune)"}");

        var cible = evs.Find(e => e.Kind == "tool.start" && e.Actor == "principal");
        var cibleTexte = cible is not null && cible.Payload.TryGetValue("args", out var a) ? a as string : null;
        check(cibleTexte == "src/Auth.cs",
            $"la cible de l'outil est portée — sans elle, trois écritures du même fichier sont indistinguables : « {cibleTexte} »");

        var fin = evs.Find(e => e.Kind == "run.end");
        var totaux = fin?.Payload.TryGetValue("totals", out var tv) == true
            ? tv as Dictionary<string, object?> : null;
        var tokIn = totaux?["tok_in"] as long? ?? -1;
        check(tokIn == 3000, $"les jetons du run.end sont la somme des étapes, cache compris : {tokIn} attendu 3000");

        var reprises = totaux?["human_interventions"] as int? ?? -1;
        check(reprises == 1,
            $"une relance humaine en cours de run compte comme reprise, le premier prompt non : {reprises}");

        var inconnus = evs.FindAll(e => Array.IndexOf(RunEvent.Kinds, e.Kind) < 0);
        check(inconnus.Count == 0,
            inconnus.Count == 0
                ? "tout type émis est déclaré dans RunEvent.Kinds"
                : $"types émis hors contrat : {string.Join(", ", inconnus.ConvertAll(e => e.Kind))}");

        // ── Avec contenu : l'autre côté de l'interrupteur ─────────────────────
        var bavard = RunExporter.Project(session, new RunExportOptions { CaptureContent = true });
        var titre = bavard.Events.Find(e => e.Kind == "run.start");
        var texte = titre is not null && titre.Payload.TryGetValue("title", out var tt) ? tt as string : null;
        check(texte is not null && texte.Contains("authentification", StringComparison.Ordinal),
            "CaptureContent à true rend le titre lisible : l'interrupteur fonctionne dans les deux sens");

        // ── Le fichier ────────────────────────────────────────────────────────
        var jsonl = RunExporter.ToJsonl(session, run, prudent);
        var lignes = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var relisible = lignes.Length == evs.Count + 1;
        foreach (var l in lignes)
        {
            try { using var _ = JsonDocument.Parse(l); }
            catch (JsonException) { relisible = false; }
        }
        check(relisible, $"le journal se relit : 1 en-tête + {evs.Count} lignes, toutes du JSON valide ({lignes.Length} au total)");

        // ── Le miroir TypeScript ──────────────────────────────────────────────
        var miroir = Path.Combine(repoRoot, "web", "ruche", "src", "core", "events.ts");
        if (!File.Exists(miroir))
        {
            check(true, "miroir TypeScript absent du dépôt : rien à comparer, rien ne peut donc diverger");
        }
        else
        {
            var source = File.ReadAllText(miroir);
            var manquants = new List<string>();
            foreach (var k in RunEvent.Kinds)
                if (!source.Contains($"'{k}'", StringComparison.Ordinal)) manquants.Add(k);
            check(manquants.Count == 0,
                manquants.Count == 0
                    ? $"le miroir TypeScript connaît les {RunEvent.Kinds.Length} types du contrat C#"
                    : $"le miroir TypeScript ignore : {string.Join(", ", manquants)}");
        }

        return 0;
    }

    private static string Cle(RunEvent e)
    {
        var racine = e.Kind[..e.Kind.LastIndexOf('.')];
        return e.Actor + "/" + racine;
    }

    /// <summary>
    /// Une session minimale mais complète : un tour humain, un appel au modèle,
    /// un outil, un Task qui ouvre un sous-agent, le tour du sous-agent, puis
    /// une relance humaine. Tout ce que la projection doit savoir faire.
    /// </summary>
    private static TranscriptSession Fabriquer(DateTimeOffset t0)
    {
        var session = new TranscriptSession
        {
            SessionId = "0f3a91cc-2b44-4d10-9e77-aa0011223344",
            Cwd = "D:/CoachingIA",
            GitBranch = "corpus-maturite",
            StartedAt = t0,
            EndedAt = t0.AddMinutes(6),
        };

        var t1 = new Turn
        {
            SessionId = session.SessionId,
            Uuid = "u1",
            StartedAt = t0,
            EndedAt = t0.AddMinutes(2),
            Prompt = "Corrige le bug d'authentification dans Auth.cs",
            StopReason = "end_turn",
        };
        t1.Steps.Add(new AssistantStep
        {
            At = t0.AddSeconds(2), Model = "claude-sonnet",
            InputTokens = 1000, CacheReadTokens = 200, OutputTokens = 300, ToolUseBlocks = 1,
        });
        t1.ToolCalls.Add(new ToolCall
        {
            Id = "toolu_1", Name = "Edit", CalledAt = t0.AddSeconds(20),
            ResultAt = t0.AddSeconds(21), InputJson = """{"file_path":"src/Auth.cs"}""",
            DurationMs = 900,
        });
        t1.ToolCalls.Add(new ToolCall
        {
            Id = "toolu_2", Name = "Task", CalledAt = t0.AddSeconds(40),
            ResultAt = t0.AddSeconds(95),
            InputJson = """{"subagent_type":"Explore","description":"chercher les usages"}""",
        });
        session.Turns.Add(t1);

        var sous = new Turn
        {
            SessionId = session.SessionId,
            Uuid = "u2",
            StartedAt = t0.AddSeconds(45),
            EndedAt = t0.AddSeconds(90),
            IsSidechain = true,
            StopReason = "end_turn",
        };
        sous.Steps.Add(new AssistantStep
        {
            At = t0.AddSeconds(46), Model = "claude-haiku",
            InputTokens = 1800, OutputTokens = 120, ToolUseBlocks = 1,
        });
        sous.ToolCalls.Add(new ToolCall
        {
            Id = "toolu_3", Name = "Grep", CalledAt = t0.AddSeconds(50),
            ResultAt = t0.AddSeconds(51), InputJson = """{"pattern":"Authenticate"}""",
        });
        session.Turns.Add(sous);

        var t2 = new Turn
        {
            SessionId = session.SessionId,
            Uuid = "u3",
            StartedAt = t0.AddMinutes(3),
            EndedAt = t0.AddMinutes(6),
            Prompt = "Relance les tests",
            StopReason = "end_turn",
        };
        t2.ToolCalls.Add(new ToolCall
        {
            Id = "toolu_4", Name = "Bash", CalledAt = t0.AddMinutes(4),
            ResultAt = t0.AddMinutes(5), InputJson = """{"command":"dotnet test"}""",
            Failed = true, FailureReason = "exit 1",
        });
        session.Turns.Add(t2);

        return session;
    }
}
