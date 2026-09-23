using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>Réglages de l'export. Les valeurs par défaut sont les plus prudentes.</summary>
public sealed class RunExportOptions
{
    /// <summary>
    /// Le chemin ou la commande visée par chaque outil. C'est de la structure
    /// et non du contenu — mais c'est aussi ce qui permet de VOIR une boucle :
    /// la règle « action répétée » compare des couples (outil, cible). À false,
    /// le lecteur montre qu'il se passe quelque chose sans dire quoi.
    /// </summary>
    public bool CaptureArgs { get; init; } = true;

    /// <summary>
    /// Le texte des prompts, des messages finaux et des sorties d'outils.
    /// Même coupure dure que <see cref="HarnessOptions.CaptureContent"/> : à
    /// false, seule l'empreinte part, dans le champ <c>ref</c>.
    /// </summary>
    public bool CaptureContent { get; init; }

    public int MaxArgChars { get; init; } = 160;

    /// <summary>
    /// Quota d'appels affiché par agent. Un transcript ne porte aucune
    /// politique : c'est un réglage de lecture, pas une règle. Il sert à donner
    /// une échelle à l'anneau de quota et à la règle « quota projeté ».
    /// </summary>
    public int MaxCallsParAgent { get; init; } = 25;

    /// <summary>Tarif, en € par million de jetons. Absent du transcript, donc nul par défaut.</summary>
    public double EurParMillionEntree { get; init; }
    public double EurParMillionSortie { get; init; }
}

/// <summary>Ce que l'export a produit, et ce qu'il a dû deviner.</summary>
public sealed class RunExportResult
{
    public required string RunId { get; init; }
    public List<RunEvent> Events { get; } = [];
    public List<RunAgent> Agents { get; } = [];

    /// <summary>
    /// Les décisions d'attribution que l'export a prises faute de mieux. Même
    /// discipline que <see cref="TaskSegmenter"/> : une heuristique se
    /// journalise, sinon elle se prend pour une mesure.
    /// </summary>
    public List<string> Decisions { get; } = [];
}

/// <summary>
/// Projette une session relue en journal d'événements lisible par « Ruche ».
///
/// Cette classe ne parse RIEN : elle lit le modèle que <see cref="SessionBuilder"/>
/// produit déjà. C'est volontaire — il n'y a qu'une seule frontière avec le
/// format JSONL de Claude Code, et elle est dans <see cref="TranscriptReader"/>.
/// Un second parseur, c'est un second endroit à réparer le jour où le format bouge.
/// </summary>
public static class RunExporter
{
    private const string Principal = "principal";
    private const string Humain = "humain";

    public static RunExportResult Project(TranscriptSession session, RunExportOptions? options = null)
    {
        var o = options ?? new RunExportOptions();
        var runId = session.SessionId;
        var t0 = session.StartedAt;
        var result = new RunExportResult { RunId = runId };

        // On accumule (instant, rang, événement) puis on trie : les tours d'un
        // sous-agent et ceux du fil principal sont entrelacés dans le temps, et
        // c'est cet entrelacement qu'on veut voir.
        var marks = new List<(DateTimeOffset At, int Rank, RunEvent Ev)>();
        var known = new Dictionary<string, RunAgent>(StringComparer.Ordinal);
        var rank = 0;

        RunEvent Ev(DateTimeOffset at, string actor, string kind, string origin = "observed",
                    RunCost? cost = null, string? reference = null)
        {
            var e = new RunEvent
            {
                Run = runId,
                Actor = actor,
                Kind = kind,
                Origin = origin,
                T = Ms(at - t0),
                Cost = cost,
                Ref = reference,
            };
            marks.Add((at, rank++, e));
            return e;
        }

        void Declare(string id, string role, int depth)
        {
            if (known.ContainsKey(id)) return;
            var a = new RunAgent { Id = id, Role = role, Depth = depth };
            known[id] = a;
            result.Agents.Add(a);
        }

        Declare(Principal, "principal", 0);
        Declare(Humain, "humain", -1);

        // ── Les fenêtres de sous-agents ────────────────────────────────────
        // Un appel à l'outil « Task » ouvre un sous-agent ; les tours marqués
        // IsSidechain qui tombent dans sa fenêtre lui appartiennent. Le
        // transcript ne porte pas d'identifiant d'agent : c'est la meilleure
        // attribution possible, et elle se journalise.
        var windows = new List<(string Id, DateTimeOffset From, DateTimeOffset To)>();
        var spawnCount = new Dictionary<string, int>(StringComparer.Ordinal);

        // Premier passage : on ne fait que relever les fenêtres. Rien n'est émis
        // ici — l'attribution des tours en dépend, et il faut la table complète
        // avant de pouvoir rattacher quoi que ce soit.
        foreach (var turn in session.Turns)
        {
            if (turn.IsSidechain) continue;
            foreach (var call in turn.ToolCalls)
            {
                if (!string.Equals(call.Name, "Task", StringComparison.OrdinalIgnoreCase)) continue;
                var type = Field(call.InputJson, "subagent_type") ?? "sous-agent";
                spawnCount.TryGetValue(type, out var n);
                spawnCount[type] = n + 1;
                var id = n == 0 ? type : type + "#" + (n + 1).ToString(CultureInfo.InvariantCulture);
                windows.Add((id, call.CalledAt, call.ResultAt ?? session.EndedAt));
                Declare(id, type, 1);
            }
        }

        // Second passage : la naissance et la mort de chaque sous-agent.
        foreach (var (id, from, to) in windows)
        {
            var spawn = Ev(from, id, "agent.spawn");
            spawn.Payload["role"] = known[id].Role;
            spawn.Payload["parent"] = Principal;
            spawn.Payload["depth"] = 1;
            Ev(to, id, "agent.end").Payload["status"] = "completed";
        }

        string ActorOf(Turn turn)
        {
            if (!turn.IsSidechain) return Principal;
            var candidates = windows.FindAll(w => turn.StartedAt >= w.From && turn.StartedAt <= w.To);
            if (candidates.Count == 1) return candidates[0].Id;
            if (candidates.Count == 0)
            {
                result.Decisions.Add(
                    $"tour de sous-agent à {turn.StartedAt:HH:mm:ss} hors de toute fenêtre Task : rattaché au fil principal");
                return Principal;
            }
            var pick = candidates[^1];
            result.Decisions.Add(
                $"tour de sous-agent à {turn.StartedAt:HH:mm:ss} dans {candidates.Count} fenêtres Task ouvertes : " +
                $"rattaché à « {pick.Id} », le plus récemment lancé");
            return pick.Id;
        }

        // ── Ouverture ──────────────────────────────────────────────────────
        var first = session.Turns.Count > 0 ? session.Turns[0] : null;
        var titre = first is null ? "" : first.Prompt;
        var start = Ev(t0, Principal, "run.start", reference: Empreinte(titre));
        start.Payload["task"] = session.SessionId.Length >= 8 ? session.SessionId[..8] : session.SessionId;
        start.Payload["title"] = Texte(titre, o, 200);
        start.Payload["policy"] = new Dictionary<string, object?>
        {
            ["agent"] = "claude-code",
            ["quotas"] = new Dictionary<string, object?>
            {
                ["max_calls_par_agent"] = o.MaxCallsParAgent,
                ["max_tokens"] = 0,
                ["max_wall_clock_ms"] = Ms(session.Duration),
                ["max_eur"] = 0,
            },
        };
        var spawnPrincipal = Ev(t0, Principal, "agent.spawn");
        spawnPrincipal.Payload["role"] = "principal";
        spawnPrincipal.Payload["parent"] = null;
        spawnPrincipal.Payload["depth"] = 0;

        // ── Les tours ──────────────────────────────────────────────────────
        var totalIn = 0L;
        var totalOut = 0L;
        var totalCalls = 0;
        var reprises = 0;

        for (var i = 0; i < session.Turns.Count; i++)
        {
            var turn = session.Turns[i];
            var actor = ActorOf(turn);

            // Une relance humaine en cours de run est une reprise en main : le
            // premier prompt, lui, EST la demande et vit dans run.start.
            if (i > 0 && !turn.IsSidechain && !string.IsNullOrWhiteSpace(turn.Prompt))
            {
                reprises++;
                var h = Ev(turn.StartedAt, Humain, "human", origin: "human", reference: Empreinte(turn.Prompt));
                h.Payload["to"] = Principal;
                h.Payload["text"] = Texte(turn.Prompt, o, 200);
            }

            // Les appels au modèle. On borne la durée : un tour laissé ouvert le
            // soir « dure » douze heures, et toute lecture fondée sur la durée
            // devient une mesure du sommeil de l'utilisateur.
            for (var s = 0; s < turn.Steps.Count; s++)
            {
                var step = turn.Steps[s];
                var suivant = s + 1 < turn.Steps.Count ? turn.Steps[s + 1].At : turn.EndedAt;
                foreach (var c in turn.ToolCalls)
                    if (c.CalledAt > step.At && c.CalledAt < suivant) suivant = c.CalledAt;
                var fin = suivant > step.At && suivant - step.At <= Turn.IdleGap ? suivant : step.At.AddSeconds(1);

                var cout = new RunCost
                {
                    Calls = 1,
                    TokensIn = step.TotalInputTokens,
                    TokensOut = step.OutputTokens,
                    Eur = step.TotalInputTokens / 1_000_000d * o.EurParMillionEntree
                        + step.OutputTokens / 1_000_000d * o.EurParMillionSortie,
                };
                totalIn += step.TotalInputTokens;
                totalOut += step.OutputTokens;
                totalCalls++;

                var resume = step.Model ?? "modèle";
                Ev(step.At, actor, "think.start").Payload["summary"] = resume;
                var end = Ev(fin, actor, "think.end", cost: cout);
                end.Payload["summary"] = resume;
                if (step.ToolUseBlocks > 1) end.Payload["parallele"] = step.ToolUseBlocks;
            }

            // Les outils.
            foreach (var call in turn.ToolCalls)
            {
                var cible = o.CaptureArgs ? Cible(call, o.MaxArgChars) : "";
                var debut = Ev(call.CalledAt, actor, "tool.start");
                debut.Payload["tool"] = call.Name;
                debut.Payload["args"] = cible;

                var fin = call.ResultAt
                          ?? (call.DurationMs is { } d ? call.CalledAt.AddMilliseconds(d) : call.CalledAt.AddMilliseconds(1));
                var e = Ev(fin, actor, "tool.end", reference: Empreinte(call.ResultText));
                e.Payload["tool"] = call.Name;
                e.Payload["args"] = cible;
                e.Payload["ok"] = !call.Failed;
                e.Payload["ms"] = (long)(call.DurationMs ?? (fin - call.CalledAt).TotalMilliseconds);
                if (call.Failed && call.FailureReason is { Length: > 0 } r)
                    e.Payload["out"] = Texte(r, o, 120);
            }
        }

        Ev(session.EndedAt, Principal, "agent.end").Payload["status"] = "completed";
        var runEnd = Ev(session.EndedAt, Principal, "run.end");
        runEnd.Payload["status"] = "completed";
        runEnd.Payload["totals"] = new Dictionary<string, object?>
        {
            ["calls"] = totalCalls,
            ["tok_in"] = totalIn,
            ["tok_out"] = totalOut,
            ["eur"] = Math.Round(totalIn / 1_000_000d * o.EurParMillionEntree
                               + totalOut / 1_000_000d * o.EurParMillionSortie, 4),
            ["wall_clock_ms"] = Ms(session.Duration),
            ["human_interventions"] = reprises,
        };

        // ── Numérotation ───────────────────────────────────────────────────
        // Tri stable sur (instant, rang d'émission) : deux événements au même
        // horodatage gardent l'ordre dans lequel on les a produits, ce qui
        // garantit qu'un .start précède toujours son .end.
        marks.Sort((a, b) =>
        {
            var c = a.At.CompareTo(b.At);
            return c != 0 ? c : a.Rank.CompareTo(b.Rank);
        });

        var seq = 0;
        foreach (var m in marks)
        {
            m.Ev.Seq = seq++;
            result.Events.Add(m.Ev);
        }
        return result;
    }

    /// <summary>En-tête JSON puis un événement par ligne — le format que le lecteur attend.</summary>
    public static string ToJsonl(TranscriptSession session, RunExportResult run, RunExportOptions? options = null)
    {
        var o = options ?? new RunExportOptions();
        var entete = new Dictionary<string, object?>
        {
            ["run_id"] = run.RunId,
            ["task_id"] = session.GitBranch ?? "session",
            ["contract_version"] = 1,
            ["agents"] = run.Agents,
            ["policy"] = new Dictionary<string, object?>
            {
                ["agent"] = "claude-code",
                ["quotas"] = new Dictionary<string, object?>
                {
                    ["max_calls_par_agent"] = o.MaxCallsParAgent,
                    ["max_tokens"] = 0,
                    ["max_wall_clock_ms"] = Ms(session.Duration),
                    ["max_eur"] = 0,
                },
            },
        };

        var sb = new StringBuilder();
        sb.Append(JsonSerializer.Serialize(entete, RunEvent.Json)).Append('\n');
        foreach (var e in run.Events)
            sb.Append(JsonSerializer.Serialize(e, RunEvent.Json)).Append('\n');
        return sb.ToString();
    }

    public static RunExportResult Write(string path, TranscriptSession session, RunExportOptions? options = null)
    {
        var run = Project(session, options);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJsonl(session, run, options), new UTF8Encoding(false));
        return run;
    }

    // ── Outillage ──────────────────────────────────────────────────────────

    private static long Ms(TimeSpan span) => (long)Math.Max(0, span.TotalMilliseconds);

    /// <summary>Le texte, ou rien du tout — jamais une version « presque » anonymisée.</summary>
    private static string Texte(string? value, RunExportOptions o, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!o.CaptureContent) return "(contenu non capturé)";
        var clean = value.ReplaceLineEndings(" ").Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }

    /// <summary>
    /// L'empreinte du contenu volumineux. C'est ce qui permet de dire « d'où
    /// vient cette phrase ? » sans mettre la phrase dans le journal, et de voir
    /// que deux étapes ont envoyé exactement le même contexte.
    /// </summary>
    private static string? Empreinte(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Ce que l'outil vise, en une chaîne courte. C'est le carburant de la
    /// règle « action répétée » : sans cible, trois tentatives d'écrire le même
    /// fichier ressemblent à trois actions différentes.
    /// </summary>
    private static string Cible(ToolCall call, int max)
    {
        var champ = Field(call.InputJson, "file_path")
                    ?? Field(call.InputJson, "path")
                    ?? Field(call.InputJson, "command")
                    ?? Field(call.InputJson, "pattern")
                    ?? Field(call.InputJson, "url")
                    ?? Field(call.InputJson, "description")
                    ?? "";
        var clean = champ.ReplaceLineEndings(" ").Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }

    /// <summary>Lecture défensive d'un champ : un JSON illisible ne fait jamais tomber l'export.</summary>
    private static string? Field(string? json, string name)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(name, out var prop)) return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
