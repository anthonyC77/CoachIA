namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Le modèle que le reste du coach manipule. Il ne parle plus JSONL : il parle
/// sessions, tours, appels d'outils. Tout ce qui suit — signaux, segmentation
/// en tâches, spans — travaille sur ces objets, jamais sur le format brut.
/// C'est la seule frontière à redessiner le jour où Claude Code change de format.
/// </summary>
public sealed class TranscriptSession
{
    public required string SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? GitBranch { get; init; }
    public string? Version { get; init; }
    public string? Entrypoint { get; init; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public List<Turn> Turns { get; } = [];

    public TimeSpan Duration => EndedAt - StartedAt;
    public int ToolCallCount => Turns.Sum(t => t.ToolCalls.Count);
}

/// <summary>
/// Un tour : ce que l'humain a demandé, et tout ce que l'agent a fait avant de
/// rendre la main. C'est l'unité que la télémétrie connaît réellement — la
/// tâche, elle, se déduit (voir TaskSegmenter).
/// </summary>
public sealed class Turn
{
    public required string SessionId { get; init; }
    public string? PromptId { get; init; }
    public string? Uuid { get; init; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>Le texte tapé par l'humain. Vide pour un tour de continuation.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>La dernière réponse texte de l'assistant sur ce tour.</summary>
    public string FinalMessage { get; set; } = "";

    /// <summary>tool_use quand le tour a été coupé, end_turn quand l'agent a rendu la main.</summary>
    public string? StopReason { get; set; }

    public List<ToolCall> ToolCalls { get; } = [];
    public List<AssistantStep> Steps { get; } = [];

    /// <summary>Skills invoquées pendant le tour, d'après l'attribution portée par les records.</summary>
    public HashSet<string> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vrai si ce tour appartient à un sous-agent plutôt qu'à la conversation principale.</summary>
    public bool IsSidechain { get; set; }

    public TimeSpan Duration => EndedAt - StartedAt;

    /// <summary>
    /// Au-delà de ce silence entre deux événements d'un même tour, on considère
    /// que personne ne travaillait. Sans ce filtre, un tour laissé ouvert le
    /// soir et repris le lendemain « dure » douze heures — et tout signal fondé
    /// sur la durée devient une mesure du sommeil de l'utilisateur.
    /// </summary>
    public static TimeSpan IdleGap { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Le temps où il s'est réellement passé quelque chose, silences retirés.</summary>
    public TimeSpan ActiveDuration
    {
        get
        {
            var marks = new List<DateTimeOffset> { StartedAt };
            marks.AddRange(Steps.Select(s => s.At));
            foreach (var c in ToolCalls)
            {
                marks.Add(c.CalledAt);
                if (c.ResultAt is { } r) marks.Add(r);
            }
            marks.Add(EndedAt);
            marks.Sort();

            var total = TimeSpan.Zero;
            for (var i = 1; i < marks.Count; i++)
            {
                var gap = marks[i] - marks[i - 1];
                if (gap > TimeSpan.Zero && gap <= IdleGap) total += gap;
            }
            return total;
        }
    }

    public bool Completed => string.Equals(StopReason, "end_turn", StringComparison.OrdinalIgnoreCase);
    public int FailedToolCalls => ToolCalls.Count(c => c.Failed);

    /// <summary>Le plus gros contexte d'entrée vu sur le tour : la pression réelle sur la fenêtre.</summary>
    public long PeakInputTokens => Steps.Count == 0 ? 0 : Steps.Max(s => s.TotalInputTokens);
}

/// <summary>Un appel au modèle à l'intérieur d'un tour, avec sa consommation.</summary>
public sealed class AssistantStep
{
    public DateTimeOffset At { get; init; }
    public string? Model { get; init; }
    public string? StopReason { get; init; }
    public long InputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long OutputTokens { get; init; }

    /// <summary>Nombre d'outils appelés en une fois : au-delà de 1, l'agent parallélise.</summary>
    public int ToolUseBlocks { get; init; }

    /// <summary>Tout ce que le modèle a lu à cette étape, cache compris.</summary>
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheCreationTokens;
}

public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset CalledAt { get; init; }
    public DateTimeOffset? ResultAt { get; set; }

    /// <summary>Paramètres d'appel, en JSON. Sert aux signaux fins (Read avec ou sans offset…).</summary>
    public string? InputJson { get; init; }
    public string? ResultText { get; set; }
    public bool Failed { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>durationMs quand l'outil le fournit, sinon l'écart entre l'appel et son résultat.</summary>
    public double? DurationMs { get; set; }

    public bool IsMcp => Name.StartsWith("mcp__", StringComparison.Ordinal);
    public string? McpServer => IsMcp && Name.Split("__") is { Length: >= 2 } p ? p[1] : null;
}
