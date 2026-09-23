using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core;

/// <summary>
/// Charge utile d'un hook Claude Code. Tous les champs sont facultatifs : chaque
/// evenement n'en remplit qu'une partie, et la liste s'allonge au fil des versions.
/// On ne valide donc rien ici - un champ inconnu ne doit jamais faire tomber
/// l'ingestion, sans quoi une mise a jour de Claude Code aveugle le coach.
/// </summary>
public sealed record HookEvent
{
    [JsonPropertyName("hook_event_name")] public string? HookEventName { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("prompt_id")] public string? PromptId { get; init; }
    [JsonPropertyName("transcript_path")] public string? TranscriptPath { get; init; }
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("permission_mode")] public string? PermissionMode { get; init; }

    [JsonPropertyName("tool_name")] public string? ToolName { get; init; }
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; init; }
    [JsonPropertyName("tool_input")] public JsonElement? ToolInput { get; init; }
    [JsonPropertyName("tool_response")] public JsonElement? ToolResponse { get; init; }
    [JsonPropertyName("tool_execution_time_ms")] public double? ToolExecutionTimeMs { get; init; }

    [JsonPropertyName("user_prompt")] public string? UserPrompt { get; init; }
    [JsonPropertyName("last_assistant_message")] public string? LastAssistantMessage { get; init; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; init; }

    [JsonPropertyName("agent_id")] public string? AgentId { get; init; }
    [JsonPropertyName("agent_type")] public string? AgentType { get; init; }

    [JsonPropertyName("session_start_reason")] public string? SessionStartReason { get; init; }
    [JsonPropertyName("error_type")] public string? ErrorType { get; init; }
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public sealed class HarnessOptions
{
    /// <summary>Collecteur OTLP de Phoenix. gRPC par defaut ; 6006 pour l'OTLP HTTP.</summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>Nom du projet Phoenix (attribut de ressource, seule voie en gRPC).</summary>
    public string ProjectName { get; set; } = "coachingia";

    /// <summary>Identifiant d'apprenant. Constante tant qu'il n'y en a qu'un.</summary>
    public string LearnerId { get; set; } = "default";

    /// <summary>Surface d'origine par defaut quand le hook ne la precise pas.</summary>
    public string Surface { get; set; } = "vscode";

    /// <summary>
    /// Capture du contenu (texte des prompts, entrees et sorties d'outils).
    /// A false, seules les metadonnees structurelles partent vers Phoenix.
    /// </summary>
    public bool CaptureContent { get; set; } = true;

    /// <summary>Troncature des valeurs textuelles, en caracteres.</summary>
    public int MaxValueChars { get; set; } = 4000;

    /// <summary>Duree au bout de laquelle une session sans evenement est fermee d'office.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Dossier des transcripts JSONL. Vide = l'emplacement par defaut de
    /// Claude Code (~/.claude/projects).
    /// </summary>
    public string? TranscriptRoot { get; set; }

    /// <summary>
    /// Fenetre de contexte du modele, reference de la pression de contexte.
    /// A ajuster au modele reellement utilise : un mauvais reglage donne des
    /// taux au-dessus de 100 %, et l'evidence du signal le dit explicitement
    /// plutot que de laisser passer un chiffre faux en silence.
    /// </summary>
    public long ContextWindow { get; set; } = 200_000;

    /// <summary>
    /// Enveloppe hebdomadaire de reference, en jetons lus. 0 = pas de
    /// reference : on ne rapporte que des tendances. Aucun plafond n'est code
    /// en dur, les limites reelles n'etant ni publiees ni stables.
    /// </summary>
    public long WeeklyTokenBudget { get; set; }

    /// <summary>
    /// Lentille de vocabulaire : neutre par defaut. Elle ne change ni les
    /// mesures ni les conseils, seulement les mots pour les dire.
    /// </summary>
    public string Lens { get; set; } = "neutre";

    /// <summary>Dossier des packs de vocabulaire. Vide = le dossier lenses du depot.</summary>
    public string? LensDirectory { get; set; }

    /// <summary>
    /// API REST de Phoenix (annotations, datasets, experiments, runs).
    /// Distincte d'OtlpEndpoint, qui reste en gRPC sur 4317.
    /// </summary>
    public string PhoenixBaseUrl { get; set; } = "http://localhost:6006";

    /// <summary>
    /// A false, plus aucune annotation ne part vers Phoenix. Les spans
    /// continuent par l'exporteur OTLP, qui n'a rien a voir avec ce reglage.
    /// </summary>
    public bool PushAnnotations { get; set; } = true;

    /// <summary>
    /// Nombre de tentatives supplementaires apres la premiere pour l'envoi
    /// d'une annotation. Au-dela, l'annotation est abandonnee avec un log en
    /// avertissement - jamais avec une exception qui remonterait a l'appelant.
    /// </summary>
    public int AnnotationMaxRetries { get; set; } = 3;
}
