using System.Text.Json;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Reconstruit une session exploitable à partir des lignes brutes : qui a
/// demandé quoi, quels outils ont tourné, combien de contexte a été relu.
/// </summary>
public static class SessionBuilder
{
    /// <summary>
    /// Préfixes qui trahissent un tour fabriqué par l'outillage plutôt que tapé
    /// par un humain. Ils changeront ; c'est pourquoi ils vivent ici, en un seul
    /// endroit, plutôt que dispersés dans les conditions.
    /// </summary>
    private static readonly string[] SyntheticPrefixes =
    [
        "<system-reminder>", "<local-command-", "<command-name>", "<command-message>",
        "<command-args>", "Caveat:", "<user-prompt-submit-hook>",
    ];

    /// <summary>
    /// Un tour compte comme humain s'il porte une marque explicite (origin.kind
    /// = human), et sinon s'il ressemble à du texte tapé : pas de drapeau isMeta,
    /// pas de bloc tool_result, pas de préfixe d'outillage. Les deux voies
    /// coexistent parce que les surfaces ne remplissent pas les mêmes champs —
    /// le CLI, l'extension VS Code et Cowork diffèrent ici.
    /// </summary>
    public static bool IsHumanPrompt(TranscriptRecord r, out string text)
    {
        text = "";
        if (!string.Equals(r.Type, "user", StringComparison.Ordinal)) return false;
        if (r.IsMeta == true) return false;

        text = ExtractUserText(r.Message).Trim();
        if (text.Length == 0) return false;

        foreach (var prefix in SyntheticPrefixes)
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        if (r.Origin is { } origin && origin.ValueKind == JsonValueKind.Object
            && origin.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String)
            return string.Equals(kind.GetString(), "human", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    public static List<TranscriptSession> Build(IEnumerable<TranscriptRecord> records)
    {
        var sessions = new Dictionary<string, TranscriptSession>(StringComparer.Ordinal);
        var pending = new Dictionary<string, ToolCall>(StringComparer.Ordinal);

        // Le tour courant se suit séparément pour la conversation principale et
        // pour les sous-agents : leurs lignes s'entrelacent dans le fichier, et
        // les fondre ensemble attribuerait le travail du sous-agent au tour
        // humain — ce qui effacerait justement le signal du palier 5.
        var current = new Dictionary<string, Turn>(StringComparer.Ordinal);
        static string Lane(string sessionId, bool sidechain) => sessionId + (sidechain ? "|agent" : "|main");

        foreach (var r in records)
        {
            var sid = r.SessionId;
            if (string.IsNullOrEmpty(sid)) continue;

            if (!sessions.TryGetValue(sid, out var session))
            {
                session = new TranscriptSession
                {
                    SessionId = sid, Cwd = r.Cwd, GitBranch = r.GitBranch,
                    Version = r.Version, Entrypoint = r.Entrypoint,
                    StartedAt = r.Timestamp ?? default,
                };
                sessions[sid] = session;
            }
            if (r.Timestamp is { } ts)
            {
                if (session.StartedAt == default || ts < session.StartedAt) session.StartedAt = ts;
                if (ts > session.EndedAt) session.EndedAt = ts;
            }

            switch (r.Type)
            {
                case "user" when IsHumanPrompt(r, out var prompt):
                {
                    var turn = new Turn
                    {
                        SessionId = sid, PromptId = r.PromptId, Uuid = r.Uuid,
                        StartedAt = r.Timestamp ?? session.EndedAt,
                        EndedAt = r.Timestamp ?? session.EndedAt,
                        Prompt = prompt,
                        IsSidechain = r.IsSidechain == true,
                    };
                    session.Turns.Add(turn);
                    current[Lane(sid, false)] = turn;
                    // Un nouveau prompt humain clôt le contexte du sous-agent
                    // précédent : ce qui suivra appartiendra à un autre travail.
                    current.Remove(Lane(sid, true));
                    break;
                }

                case "user":
                    AbsorbToolResults(r, pending);
                    break;

                case "assistant":
                    AbsorbAssistant(r, session, current, pending);
                    break;
            }
        }

        foreach (var s in sessions.Values)
        {
            // Une session peut commencer par du travail d'agent avant tout prompt
            // humain (reprise de session). On ne fabrique pas de tour fantôme :
            // ces étapes se rattachent au premier tour réel s'il existe.
            s.Turns.RemoveAll(t => t.Prompt.Length == 0 && t.ToolCalls.Count == 0 && t.Steps.Count == 0);
        }
        return [.. sessions.Values.OrderBy(s => s.StartedAt)];
    }

    private static void AbsorbAssistant(
        TranscriptRecord r, TranscriptSession session,
        Dictionary<string, Turn> current, Dictionary<string, ToolCall> pending)
    {
        var sid = r.SessionId!;
        var sidechain = r.IsSidechain == true;
        var lane = sid + (sidechain ? "|agent" : "|main");

        if (!current.TryGetValue(lane, out var turn))
        {
            // Aucun prompt humain vu jusqu'ici : on ouvre un tour de continuation
            // plutôt que de jeter le travail (session reprise, --continue).
            turn = new Turn
            {
                SessionId = sid, Uuid = r.Uuid,
                StartedAt = r.Timestamp ?? session.EndedAt,
                EndedAt = r.Timestamp ?? session.EndedAt,
                IsSidechain = sidechain,
            };
            session.Turns.Add(turn);
            current[lane] = turn;
        }

        if (r.Timestamp is { } ts && ts > turn.EndedAt) turn.EndedAt = ts;
        if (r.AttributionSkill is { Length: > 0 } skill) turn.Skills.Add(skill);

        if (r.Message is not { ValueKind: JsonValueKind.Object } msg) return;

        var usage = msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var toolBlocks = 0;

        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(block, "type");
                if (kind == "tool_use")
                {
                    toolBlocks++;
                    var id = Str(block, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var call = new ToolCall
                    {
                        Id = id,
                        Name = Str(block, "name") ?? "unknown",
                        CalledAt = r.Timestamp ?? turn.EndedAt,
                        InputJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : null,
                    };
                    turn.ToolCalls.Add(call);
                    pending[id] = call;
                }
                else if (kind == "text")
                {
                    var text = Str(block, "text");
                    if (!string.IsNullOrWhiteSpace(text)) turn.FinalMessage = text!;
                }
            }
        }

        var stop = Str(msg, "stop_reason");
        if (stop is { Length: > 0 }) turn.StopReason = stop;

        turn.Steps.Add(new AssistantStep
        {
            At = r.Timestamp ?? turn.EndedAt,
            Model = Str(msg, "model"),
            StopReason = stop,
            InputTokens = Num(usage, "input_tokens"),
            CacheReadTokens = Num(usage, "cache_read_input_tokens"),
            CacheCreationTokens = Num(usage, "cache_creation_input_tokens"),
            OutputTokens = Num(usage, "output_tokens"),
            ToolUseBlocks = toolBlocks,
        });
    }

    private static void AbsorbToolResults(TranscriptRecord r, Dictionary<string, ToolCall> pending)
    {
        if (r.Message is not { ValueKind: JsonValueKind.Object } msg) return;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (Str(block, "type") != "tool_result") continue;
            var id = Str(block, "tool_use_id");
            if (id is null || !pending.Remove(id, out var call)) continue;

            call.ResultAt = r.Timestamp;
            call.ResultText = Truncate(RawText(block, "content"), 2000);

            // L'échec ne se lit pas dans un seul champ : is_error est souvent
            // absent, et l'information vit alors dans toolUseResult. On croise
            // les trois sources plutôt que de faire confiance à la première.
            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var reason = isError ? "is_error" : null;

            if (r.ToolUseResult is { ValueKind: JsonValueKind.Object } tur)
            {
                if (tur.TryGetProperty("durationMs", out var d) && d.TryGetDouble(out var ms)) call.DurationMs = ms;
                if (tur.TryGetProperty("interrupted", out var i) && i.ValueKind == JsonValueKind.True)
                { isError = true; reason ??= "interrupted"; }
                if (tur.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
                { isError = true; reason ??= "success=false"; }
                if (tur.TryGetProperty("code", out var c) && c.TryGetInt32(out var code) && code != 0)
                { isError = true; reason ??= $"code={code}"; }
            }

            call.Failed = isError;
            call.FailureReason = reason;
            call.DurationMs ??= call.ResultAt is { } end ? (end - call.CalledAt).TotalMilliseconds : null;
        }
    }

    /// <summary>
    /// Le contenu d'un message utilisateur est tantôt une chaîne, tantôt une
    /// liste de blocs. On ne garde que le texte : les tool_result appartiennent
    /// à l'agent, pas à l'humain.
    /// </summary>
    private static string ExtractUserText(JsonElement? message)
    {
        if (message is not { ValueKind: JsonValueKind.Object } msg) return "";
        if (!msg.TryGetProperty("content", out var content)) return "";

        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (Str(block, "type") != "text") continue;
            if (Str(block, "text") is { Length: > 0 } t) parts.Add(t);
        }
        return string.Join("\n", parts);
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long Num(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string? RawText(JsonElement e, string name)
        => e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
