namespace CoachingIA.Harness.Core;

/// <summary>
/// Noms d'attributs des conventions semantiques OpenInference, telles que Phoenix
/// les attend. Reference : github.com/Arize-ai/openinference/blob/main/spec/semantic_conventions.md
/// </summary>
public static class OI
{
    public const string SpanKind = "openinference.span.kind";
    public const string InputValue = "input.value";
    public const string InputMime = "input.mime_type";
    public const string OutputValue = "output.value";
    public const string OutputMime = "output.mime_type";
    public const string ToolName = "tool.name";
    public const string ToolParameters = "tool.parameters";
    public const string Metadata = "metadata";
    public const string Tags = "tag.tags";
    public const string SessionId = "session.id";
    public const string UserId = "user.id";
    public const string AgentName = "agent.name";
    public const string GraphNodeId = "graph.node.id";
    public const string GraphNodeParentId = "graph.node.parent_id";

    /// <summary>Attribut de ressource qui determine le projet cote Phoenix en OTLP/gRPC.</summary>
    public const string ProjectNameResource = "openinference.project.name";

    public static class Kind
    {
        public const string Agent = "AGENT";
        public const string Chain = "CHAIN";
        public const string Tool = "TOOL";
        public const string Llm = "LLM";
        public const string Guardrail = "GUARDRAIL";
        public const string Evaluator = "EVALUATOR";
    }

    public const string MimeJson = "application/json";
    public const string MimeText = "text/plain";
}

/// <summary>
/// Attributs propres au coach. Ils ne remplacent pas OpenInference : ils s'ajoutent,
/// pour que le MaturityEngine puisse filtrer les spans par palier et par signal
/// sans avoir a re-deviner ce que chaque span voulait dire.
/// </summary>
public static class Coach
{
    public const string Learner = "coaching.learner";
    public const string Surface = "coaching.surface";
    public const string Level = "coaching.level";
    public const string Signal = "coaching.signal";
    public const string HookEvent = "coaching.hook_event";
    public const string PromptId = "coaching.prompt_id";
    public const string ToolUseId = "coaching.tool_use_id";
    public const string Outcome = "coaching.outcome";
    /// <summary>D'où vient ce span : transcript (lot) ou hook (temps réel).</summary>
    public const string Source = "coaching.source";
}
