using System.Diagnostics;
using CoachingIA.Harness.Core.Phoenix;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Convertit des sessions relues en spans OpenInference. Contrairement à
/// SpanFactory, qui reçoit des événements au fil de l'eau et doit garder l'état
/// des spans ouverts, l'ingestion en lot connaît déjà le début et la fin de
/// chaque chose : elle crée des spans terminés, aux horodatages d'origine.
/// C'est ce qui permet de rejouer des semaines d'historique et de les voir
/// arriver dans Phoenix à leur vraie date.
/// </summary>
public sealed class TranscriptIngestor
{
    private readonly HarnessOptions _options;
    private readonly TaskSegmenter _segmenter;
    private readonly SignalExtractor _signals;
    private readonly IPhoenixClient? _phoenix;

    public TranscriptIngestor(
        HarnessOptions options, TaskSegmenter? segmenter = null, SignalExtractor? signals = null,
        IPhoenixClient? phoenix = null)
    {
        _options = options;
        _segmenter = segmenter ?? new TaskSegmenter();
        _signals = signals ?? new SignalExtractor { CaptureContent = options.CaptureContent };
        _phoenix = phoenix;
    }

    public IngestResult Ingest(IEnumerable<TranscriptSession> sessions)
    {
        var result = new IngestResult();

        foreach (var session in sessions)
        {
            if (session.Turns.Count == 0) continue;
            Activity.Current = null;

            var sessionSpan = SpanFactory.Source.StartActivity(
                "session", ActivityKind.Internal, default(ActivityContext),
                tags: null, links: null, startTime: session.StartedAt);
            if (sessionSpan is null) continue;

            Tag(sessionSpan, session);
            sessionSpan.SetTag(OI.SpanKind, OI.Kind.Agent);
            sessionSpan.SetTag(OI.AgentName, "claude-code");
            sessionSpan.SetTag("coaching.cwd", session.Cwd);
            sessionSpan.SetTag("coaching.git_branch", session.GitBranch);
            sessionSpan.SetTag("coaching.cc_version", session.Version);
            sessionSpan.SetTag("coaching.entrypoint", session.Entrypoint);
            sessionSpan.SetTag(Coach.Source, "transcript");

            var tasks = _segmenter.Segment(session);
            result.Sessions++;
            result.Tasks += tasks.Count;

            foreach (var task in tasks)
            {
                var taskSpan = SpanFactory.Source.StartActivity(
                    "task", ActivityKind.Internal, sessionSpan.Context,
                    tags: null, links: null, startTime: task.StartedAt);

                if (taskSpan is not null)
                {
                    Tag(taskSpan, session);
                    taskSpan.SetTag(OI.SpanKind, OI.Kind.Chain);
                    taskSpan.SetTag(OI.InputValue, Cut(task.Title));
                    taskSpan.SetTag("coaching.task_id", task.Id);
                    taskSpan.SetTag("coaching.task_source", task.Source);
                    taskSpan.SetTag("coaching.task_decomposed", task.HasTaskEvents);
                    taskSpan.SetTag("coaching.active_minutes", Math.Round(task.ActiveDuration.TotalMinutes, 1));
                    taskSpan.SetTag("coaching.rework_turns", task.ReworkTurns);
                    taskSpan.SetTag(Coach.Outcome, task.InProgress ? "in_progress" : task.Completed ? "completed" : "open");
                    taskSpan.SetTag(Coach.Source, "transcript");

                    // Les signaux voyagent avec la tâche : c'est ce qui permettra
                    // au bilan de citer un chiffre ET la phrase qui l'explique.
                    var taskSignals = _signals.ForTask(task, session);
                    foreach (var signal in taskSignals)
                    {
                        // Un signal indéterminé n'est pas un zéro : on ne l'écrit
                        // pas du tout, plutôt que de polluer les moyennes de Phoenix.
                        if (!double.IsNaN(signal.Value)) taskSpan.SetTag($"signal.{signal.Key}", signal.Value);
                        taskSpan.SetTag($"signal.{signal.Key}.why", signal.Evidence);
                        result.Signals++;
                    }

                    // Les attributs signal.{clé} ci-dessus restent l'unique source
                    // dans le span lui-même ; l'annotation s'ajoute par-dessus, sur
                    // le même span de tâche, pour que Phoenix puisse trier/agréger.
                    if (_phoenix is not null && _options.PushAnnotations)
                    {
                        var annotations = SignalAnnotations.FromSignals(
                            taskSignals, taskSpan.SpanId.ToHexString(), _options);
                        _phoenix.AnnotateAsync(annotations, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }

                foreach (var turn in task.Turns)
                    EmitTurn(turn, session, taskSpan?.Context ?? sessionSpan.Context, result);

                // Les sous-agents sortent sous la même tâche : c'est ce qui rend
                // la profondeur d'orchestration lisible dans Phoenix.
                foreach (var agentTurn in task.AgentTurns)
                    EmitTurn(agentTurn, session, taskSpan?.Context ?? sessionSpan.Context, result);

                if (taskSpan is not null)
                {
                    taskSpan.SetEndTime(Max(task.EndedAt, task.StartedAt).UtcDateTime);
                    taskSpan.Dispose();
                }
            }

            sessionSpan.SetEndTime(Max(session.EndedAt, session.StartedAt).UtcDateTime);
            sessionSpan.Dispose();
        }

        return result;
    }

    private void EmitTurn(Turn turn, TranscriptSession session, ActivityContext parent, IngestResult result)
    {
        var turnSpan = SpanFactory.Source.StartActivity(
            "turn", ActivityKind.Internal, parent, tags: null, links: null, startTime: turn.StartedAt);
        if (turnSpan is null) return;

        Tag(turnSpan, session);
        turnSpan.SetTag(OI.SpanKind, turn.IsSidechain ? OI.Kind.Agent : OI.Kind.Chain);
        turnSpan.SetTag(Coach.Level, 1);
        turnSpan.SetTag(Coach.Signal, "prompt_clarity");
        turnSpan.SetTag(Coach.Source, "transcript");
        turnSpan.SetTag("coaching.stop_reason", turn.StopReason);
        turnSpan.SetTag(Coach.Outcome, turn.Completed ? "completed" : "interrupted");
        turnSpan.SetTag(Coach.PromptId, turn.PromptId);

        if (_options.CaptureContent)
        {
            if (turn.Prompt.Length > 0) turnSpan.SetTag(OI.InputValue, Cut(turn.Prompt));
            if (turn.FinalMessage.Length > 0) turnSpan.SetTag(OI.OutputValue, Cut(turn.FinalMessage));
        }

        // Les compteurs de jetons vivent dans le transcript : la pression de
        // contexte et le taux de cache n'exigent donc aucune télémétrie tierce.
        var peak = turn.PeakInputTokens;
        if (peak > 0) turnSpan.SetTag("llm.token_count.prompt", peak);
        var outTokens = turn.Steps.Sum(s => s.OutputTokens);
        if (outTokens > 0) turnSpan.SetTag("llm.token_count.completion", outTokens);
        var cacheRead = turn.Steps.Sum(s => s.CacheReadTokens);
        if (cacheRead > 0) turnSpan.SetTag("llm.token_count.prompt_details.cache_read", cacheRead);
        if (turn.Steps.Count > 0) turnSpan.SetTag("llm.model_name", turn.Steps[^1].Model);
        if (turn.Skills.Count > 0) turnSpan.SetTag("coaching.skills", string.Join(",", turn.Skills));

        foreach (var call in turn.ToolCalls)
        {
            var span = SpanFactory.Source.StartActivity(
                "tool." + call.Name, ActivityKind.Internal, turnSpan.Context,
                tags: null, links: null, startTime: call.CalledAt);
            if (span is null) continue;

            Tag(span, session);
            span.SetTag(OI.SpanKind, OI.Kind.Tool);
            span.SetTag(OI.ToolName, call.Name);
            span.SetTag(Coach.ToolUseId, call.Id);
            span.SetTag(Coach.Level, 3);
            span.SetTag(Coach.Signal, call.Failed ? "tool_failure_rate" : "harness_breadth");
            span.SetTag(Coach.Outcome, call.Failed ? "failed" : "ok");
            span.SetTag(Coach.Source, "transcript");
            if (call.IsMcp) span.SetTag("coaching.mcp_server", call.McpServer);
            if (_options.CaptureContent)
            {
                if (call.InputJson is { Length: > 0 }) { span.SetTag(OI.InputValue, Cut(call.InputJson)); span.SetTag(OI.InputMime, OI.MimeJson); }
                if (call.ResultText is { Length: > 0 }) span.SetTag(OI.OutputValue, Cut(call.ResultText));
            }
            if (call.Failed) span.SetStatus(ActivityStatusCode.Error, call.FailureReason);

            var end = call.ResultAt ?? call.CalledAt.AddMilliseconds(call.DurationMs ?? 0);
            span.SetEndTime(Max(end, call.CalledAt).UtcDateTime);
            span.Dispose();
            result.ToolCalls++;
        }

        turnSpan.SetEndTime(Max(turn.EndedAt, turn.StartedAt).UtcDateTime);
        turnSpan.Dispose();
        result.Turns++;
    }

    private void Tag(Activity a, TranscriptSession session)
    {
        a.SetTag(OI.SessionId, session.SessionId);
        a.SetTag(OI.UserId, _options.LearnerId);
        a.SetTag(Coach.Learner, _options.LearnerId);
        a.SetTag(Coach.Surface, _options.Surface);
    }

    private string Cut(string value)
        => value.Length <= _options.MaxValueChars
            ? value
            : string.Concat(value.AsSpan(0, _options.MaxValueChars), "… [tronqué]");

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

public sealed class IngestResult
{
    public int Sessions { get; set; }
    public int Tasks { get; set; }
    public int Turns { get; set; }
    public int ToolCalls { get; set; }
    public int Signals { get; set; }
    public override string ToString()
        => $"{Sessions} session(s), {Tasks} tâche(s), {Turns} tour(s), {ToolCalls} appel(s) d'outil, {Signals} signal(aux)";
}
