using System.Diagnostics;
using System.Text.Json;

namespace CoachingIA.Harness.Core;

/// <summary>
/// Traduit un evenement de hook en span OpenInference.
///
/// Trois formes de spans seulement :
///   - les spans ouverts (session, tour de prompt, sous-agent), fermes par un
///     evenement ulterieur ;
///   - les spans reconstitues (appel d'outil), crees deja termines a partir de
///     leur duree connue ;
///   - les marqueurs (compaction, permission refusee, tache), de duree nulle,
///     qui n'existent que pour porter un signal.
/// </summary>
public sealed class SpanFactory
{
    public const string SourceName = "CoachingIA.Harness";
    public static readonly ActivitySource Source = new(SourceName, "0.1.0");

    private readonly SessionRegistry _registry;
    private readonly HarnessOptions _options;
    private readonly TimeProvider _time;

    public SpanFactory(SessionRegistry registry, HarnessOptions options, TimeProvider? time = null)
    {
        _registry = registry;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Point d'entree unique. Renvoie le span produit, ou null si l'evenement
    /// n'est pas encore cartographie - auquel cas on l'ignore en silence plutot
    /// que d'inventer un span dont personne ne saura quoi faire.
    /// </summary>
    public Activity? Handle(HookEvent e)
    {
        // Un handler ASP.NET peut deja porter une Activity ambiante ; sans cela
        // les spans du coach s'accrocheraient a la requete HTTP du hook, ce qui
        // ferait apparaitre le harnais lui-meme dans les traces de l'apprenant.
        Activity.Current = null;

        return e.HookEventName switch
        {
            "SessionStart" => OpenSession(e),
            "SessionEnd" => CloseSession(e),
            "UserPromptSubmit" => OpenPrompt(e),
            "Stop" => ClosePrompt(e, ok: true),
            "StopFailure" => ClosePrompt(e, ok: false),
            "PostToolUse" => ToolSpan(e, ok: true),
            "PostToolUseFailure" => ToolSpan(e, ok: false),
            "SubagentStart" => OpenAgent(e),
            "SubagentStop" => CloseAgent(e),
            "PreCompact" => Marker(e, "context.compact", OI.Kind.Chain, level: 2, signal: "compaction_mode"),
            "PostCompact" => Marker(e, "context.compacted", OI.Kind.Chain, level: 2, signal: "compaction_mode"),
            "PostToolBatch" => Marker(e, "loop.tool_batch", OI.Kind.Chain, level: 4, signal: "parallelism_index"),
            "TaskCreated" => Marker(e, "loop.task_created", OI.Kind.Chain, level: 4, signal: "plan_structure"),
            "TaskCompleted" => Marker(e, "loop.task_completed", OI.Kind.Chain, level: 4, signal: "plan_structure"),
            "PermissionDenied" => Marker(e, "harness.permission_denied", OI.Kind.Guardrail, level: 3, signal: "permission_friction"),
            _ => null,
        };
    }

    // ---------- session ----------

    private Activity? OpenSession(HookEvent e)
    {
        var sessionId = e.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return null;

        var activity = Source.StartActivity("session", ActivityKind.Internal, default(ActivityContext));
        if (activity is null) return null;

        Common(activity, e);
        activity.SetTag(OI.SpanKind, OI.Kind.Agent);
        activity.SetTag(OI.AgentName, "claude-code");
        activity.SetTag("coaching.session_start_reason", e.SessionStartReason);
        activity.SetTag("coaching.cwd", e.Cwd);
        activity.SetTag("coaching.permission_mode", e.PermissionMode);

        _registry.Open(SessionRegistry.ScopeSession, sessionId, activity);
        return activity;
    }

    private Activity? CloseSession(HookEvent e)
    {
        if (string.IsNullOrEmpty(e.SessionId)) return null;
        var activity = _registry.Close(SessionRegistry.ScopeSession, e.SessionId);
        if (activity is null) return null;
        activity.SetTag(Coach.Outcome, "ended");
        activity.Dispose();
        return activity;
    }

    /// <summary>
    /// Retrouve la session, ou en ouvre une a la volee. Le hook SessionStart peut
    /// manquer legitimement : session reprise, harnais demarre en cours de route.
    /// Refuser de tracer dans ce cas ferait perdre des sessions entieres.
    /// </summary>
    private ActivityContext? SessionContext(HookEvent e)
    {
        if (string.IsNullOrEmpty(e.SessionId)) return null;
        var existing = _registry.ContextOf(SessionRegistry.ScopeSession, e.SessionId);
        if (existing is not null) return existing;

        var recovered = OpenSession(e with { HookEventName = "SessionStart", SessionStartReason = "recovered" });
        return recovered?.Context;
    }

    // ---------- tour de prompt ----------

    private Activity? OpenPrompt(HookEvent e)
    {
        var promptId = e.PromptId ?? e.SessionId;
        if (string.IsNullOrEmpty(promptId)) return null;

        var parent = SessionContext(e);
        var activity = Start("turn", ActivityKind.Internal, parent);
        if (activity is null) return null;

        Common(activity, e);
        activity.SetTag(OI.SpanKind, OI.Kind.Chain);
        activity.SetTag(Coach.Level, 1);
        activity.SetTag(Coach.Signal, "prompt_clarity");
        SetText(activity, OI.InputValue, e.UserPrompt);
        activity.SetTag(OI.InputMime, OI.MimeText);

        _registry.Open(SessionRegistry.ScopePrompt, promptId, activity);
        return activity;
    }

    private Activity? ClosePrompt(HookEvent e, bool ok)
    {
        var promptId = e.PromptId ?? e.SessionId;
        if (string.IsNullOrEmpty(promptId)) return null;

        var activity = _registry.Close(SessionRegistry.ScopePrompt, promptId);
        if (activity is null) return null;

        activity.SetTag("coaching.stop_reason", e.StopReason);
        activity.SetTag(Coach.Outcome, ok ? "completed" : "failed");
        SetText(activity, OI.OutputValue, e.LastAssistantMessage);
        if (!ok)
        {
            activity.SetStatus(ActivityStatusCode.Error, e.ErrorMessage ?? e.ErrorType);
            activity.SetTag("coaching.error_type", e.ErrorType);
        }
        activity.Dispose();
        return activity;
    }

    // ---------- appels d'outils ----------

    private Activity? ToolSpan(HookEvent e, bool ok)
    {
        var parent = PromptContext(e);
        var now = _time.GetUtcNow();
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, e.ToolExecutionTimeMs ?? 0));
        var start = now - duration;

        var name = "tool." + (e.ToolName ?? "unknown");
        var activity = Source.StartActivity(
            name, ActivityKind.Internal, parent ?? default,
            tags: null, links: null, startTime: start);
        if (activity is null) return null;

        Common(activity, e);
        activity.SetTag(OI.SpanKind, OI.Kind.Tool);
        activity.SetTag(OI.ToolName, e.ToolName);
        activity.SetTag(Coach.ToolUseId, e.ToolUseId);
        activity.SetTag(Coach.Level, 3);
        activity.SetTag(Coach.Signal, ok ? "harness_breadth" : "tool_failure_rate");
        activity.SetTag(Coach.Outcome, ok ? "ok" : "failed");

        SetJson(activity, OI.InputValue, e.ToolInput);
        if (e.ToolInput is not null) activity.SetTag(OI.InputMime, OI.MimeJson);
        SetJson(activity, OI.OutputValue, e.ToolResponse);
        if (e.ToolResponse is not null) activity.SetTag(OI.OutputMime, OI.MimeJson);

        if (!ok) activity.SetStatus(ActivityStatusCode.Error, e.ErrorMessage);

        activity.SetEndTime(now.UtcDateTime);
        activity.Dispose();
        return activity;
    }

    // ---------- sous-agents ----------

    private Activity? OpenAgent(HookEvent e)
    {
        if (string.IsNullOrEmpty(e.AgentId)) return null;
        var parent = PromptContext(e);
        var activity = Start("agent." + (e.AgentType ?? "unknown"), ActivityKind.Internal, parent);
        if (activity is null) return null;

        Common(activity, e);
        activity.SetTag(OI.SpanKind, OI.Kind.Agent);
        activity.SetTag(OI.AgentName, e.AgentType);
        activity.SetTag(OI.GraphNodeId, e.AgentId);
        if (parent is { } p) activity.SetTag(OI.GraphNodeParentId, p.SpanId.ToHexString());
        activity.SetTag(Coach.Level, 5);
        activity.SetTag(Coach.Signal, "graph_depth");

        _registry.Open(SessionRegistry.ScopeAgent, e.AgentId, activity);
        return activity;
    }

    private Activity? CloseAgent(HookEvent e)
    {
        if (string.IsNullOrEmpty(e.AgentId)) return null;
        var activity = _registry.Close(SessionRegistry.ScopeAgent, e.AgentId);
        if (activity is null) return null;
        SetText(activity, OI.OutputValue, e.LastAssistantMessage);
        activity.SetTag(Coach.Outcome, "ended");
        activity.Dispose();
        return activity;
    }

    // ---------- marqueurs ----------

    private Activity? Marker(HookEvent e, string name, string kind, int level, string signal)
    {
        var parent = PromptContext(e);
        var activity = Start(name, ActivityKind.Internal, parent);
        if (activity is null) return null;

        Common(activity, e);
        activity.SetTag(OI.SpanKind, kind);
        activity.SetTag(Coach.Level, level);
        activity.SetTag(Coach.Signal, signal);
        if (e.ToolName is not null) activity.SetTag(OI.ToolName, e.ToolName);
        activity.Dispose();
        return activity;
    }

    // ---------- utilitaires ----------

    private ActivityContext? PromptContext(HookEvent e)
    {
        var promptId = e.PromptId;
        if (!string.IsNullOrEmpty(promptId))
        {
            var ctx = _registry.ContextOf(SessionRegistry.ScopePrompt, promptId);
            if (ctx is not null) return ctx;
        }
        // Un evenement hors tour de prompt (SessionStart, hook precoce) se rattache
        // directement a la session.
        return SessionContext(e);
    }

    private static Activity? Start(string name, ActivityKind kind, ActivityContext? parent)
        => Source.StartActivity(name, kind, parent ?? default);

    private void Common(Activity a, HookEvent e)
    {
        a.SetTag(OI.SessionId, e.SessionId);
        a.SetTag(OI.UserId, _options.LearnerId);
        a.SetTag(Coach.Learner, _options.LearnerId);
        a.SetTag(Coach.Surface, _options.Surface);
        a.SetTag(Coach.HookEvent, e.HookEventName);
        a.SetTag(Coach.PromptId, e.PromptId);
    }

    private void SetText(Activity a, string key, string? value)
    {
        if (!_options.CaptureContent || string.IsNullOrEmpty(value)) return;
        a.SetTag(key, Truncate(value));
    }

    private void SetJson(Activity a, string key, JsonElement? value)
    {
        if (!_options.CaptureContent || value is null) return;
        var text = value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString() ?? string.Empty
            : value.Value.GetRawText();
        if (text.Length == 0) return;
        a.SetTag(key, Truncate(text));
    }

    private string Truncate(string value)
        => value.Length <= _options.MaxValueChars
            ? value
            : string.Concat(value.AsSpan(0, _options.MaxValueChars), "… [tronqué]");
}
