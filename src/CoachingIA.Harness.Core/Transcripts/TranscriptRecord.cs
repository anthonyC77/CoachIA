using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Une ligne de transcript JSONL, telle que Claude Code la dépose sous
/// ~/.claude/projects/&lt;projet&gt;/&lt;sessionId&gt;.jsonl
///
/// Ce format est INTERNE : il n'est pas un contrat public et change d'une
/// version à l'autre. Toute la classe est donc écrite pour survivre à
/// l'inconnu — chaque champ est facultatif, aucun n'est validé, et le JSON
/// brut est conservé pour que le parseur puisse fouiller ce que ce modèle
/// n'anticipe pas encore.
/// </summary>
public sealed record TranscriptRecord
{
    /// <summary>assistant | user | attachment | system | mode | last-prompt | …</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    [JsonPropertyName("uuid")] public string? Uuid { get; init; }
    [JsonPropertyName("parentUuid")] public string? ParentUuid { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; init; }
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("gitBranch")] public string? GitBranch { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("entrypoint")] public string? Entrypoint { get; init; }

    /// <summary>Vrai sur les tours d'un sous-agent. Le socle du palier 5.</summary>
    [JsonPropertyName("isSidechain")] public bool? IsSidechain { get; init; }

    /// <summary>Vrai quand le tour a été injecté par le système (skill, rappel) et non tapé par l'humain.</summary>
    [JsonPropertyName("isMeta")] public bool? IsMeta { get; init; }

    [JsonPropertyName("promptId")] public string? PromptId { get; init; }
    [JsonPropertyName("promptSource")] public string? PromptSource { get; init; }
    [JsonPropertyName("origin")] public JsonElement? Origin { get; init; }
    [JsonPropertyName("permissionMode")] public string? PermissionMode { get; init; }

    /// <summary>Le message au format API Anthropic : rôle, blocs de contenu, usage.</summary>
    [JsonPropertyName("message")] public JsonElement? Message { get; init; }

    /// <summary>Résultat enrichi d'un appel d'outil : stdout, durationMs, interrupted…</summary>
    [JsonPropertyName("toolUseResult")] public JsonElement? ToolUseResult { get; init; }

    /// <summary>Skill qui a produit ce tour d'assistant. Signal direct du palier 3.</summary>
    [JsonPropertyName("attributionSkill")] public string? AttributionSkill { get; init; }
    [JsonPropertyName("attributionMcpServer")] public string? AttributionMcpServer { get; init; }
    [JsonPropertyName("attributionMcpTool")] public string? AttributionMcpTool { get; init; }

    [JsonPropertyName("subtype")] public string? Subtype { get; init; }
    [JsonPropertyName("attachment")] public JsonElement? Attachment { get; init; }

    /// <summary>La ligne d'origine, pour tout ce que ce modèle ne sait pas encore lire.</summary>
    [JsonIgnore] public JsonElement Raw { get; init; }

    /// <summary>Numéro de ligne dans le fichier : sert aux rapports d'erreur.</summary>
    [JsonIgnore] public int LineNumber { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>
/// Ce que la lecture d'un lot de transcripts a rencontré. Un parseur tolérant
/// qui ne compte pas ce qu'il ignore est un parseur qui ment : la dérive de
/// format doit se voir, sinon elle se découvre trois semaines trop tard.
/// </summary>
public sealed class ParseReport
{
    public int FilesRead { get; set; }
    public int LinesRead { get; set; }
    public int LinesUnreadable { get; set; }
    public int RecordsKept { get; set; }
    public Dictionary<string, int> RecordTypes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Versions { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = [];

    public double UnreadableRate => LinesRead == 0 ? 0 : (double)LinesUnreadable / LinesRead;

    /// <summary>Au-delà de ce seuil, le format a probablement changé sous nos pieds.</summary>
    public bool LooksBroken => LinesRead > 50 && UnreadableRate > 0.02;

    public void Count(string bucket, Dictionary<string, int> into)
        => into[bucket] = into.TryGetValue(bucket, out var n) ? n + 1 : 1;
}
