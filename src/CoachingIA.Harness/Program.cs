using System.Text.Json;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Transcripts;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Harness").Get<HarnessOptions>() ?? new HarnessOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(sp => new SessionRegistry(options.SessionIdleTimeout));
builder.Services.AddSingleton<SpanFactory>();
builder.Services.AddSingleton<TaskSegmenter>();
builder.Services.AddSingleton(new SignalExtractor { ContextWindow = options.ContextWindow });
builder.Services.AddSingleton<TranscriptIngestor>();
builder.Services.AddHostedService<IdleSweeper>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName: "coachingia-harness", serviceVersion: "0.1.0")
        // En OTLP/gRPC, c'est cet attribut de ressource - et lui seul - qui
        // determine le projet cote Phoenix. L'en-tete x-project-name ne vaut
        // que pour l'endpoint HTTP.
        .AddAttributes([new KeyValuePair<string, object>(OI.ProjectNameResource, options.ProjectName)]))
    .WithTracing(t => t
        .AddSource(SpanFactory.SourceName)
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(options.OtlpEndpoint);
            o.Protocol = OtlpExportProtocol.Grpc;
        }));

var app = builder.Build();

// Un hook bloque le tour de l'apprenant tant qu'il n'a pas repondu. Toute la
// surface HTTP ci-dessous suit donc une seule regle : repondre vite, repondre
// 200, ne jamais faire echouer la session de quelqu'un parce que le coach a un
// probleme. Une trace perdue est un incident mineur ; un tour bloque ne l'est pas.
app.MapPost("/hooks/{eventName?}", async (
    string? eventName,
    HttpRequest request,
    SpanFactory factory,
    ILogger<Program> log) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body, default, request.HttpContext.RequestAborted);
        var hook = document.RootElement.Deserialize<HookEvent>(HookEvent.Json) ?? new HookEvent();

        // Le nom d'evenement du corps fait foi ; le segment d'URL n'est qu'un
        // filet de securite pour une configuration qui ne l'enverrait pas.
        if (string.IsNullOrEmpty(hook.HookEventName) && eventName is not null
            && Routes.Map.TryGetValue(eventName, out var fromRoute))
            hook = hook with { HookEventName = fromRoute };

        var span = factory.Handle(hook);
        if (span is null)
            log.LogDebug("Evenement non cartographie : {Event}", hook.HookEventName);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Hook ignore apres erreur d'ingestion");
    }

    return Results.Json(new { @continue = true });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Rejeu de l'historique. C'est la voie principale de collecte : elle rattrape
// tout ce que les hooks n'ont pas vu, y compris les semaines antérieures à
// l'installation du harnais. Idempotente au sens où relancer produit les mêmes
// spans, aux mêmes dates — Phoenix les dédoublonne par identifiant de trace.
app.MapPost("/ingest", (
    TranscriptIngestor ingestor,
    HarnessOptions options,
    ILogger<Program> log,
    string? root = null,
    int? days = null) =>
{
    // Une chaîne vide dans appsettings.json ne vaut pas "non renseigné" pour
    // l'opérateur ?? : on la traite comme telle explicitement.
    var configured = string.IsNullOrWhiteSpace(options.TranscriptRoot) ? null : options.TranscriptRoot;
    var directory = root ?? configured ?? TranscriptReader.DefaultRoot;
    if (!Directory.Exists(directory))
        return Results.Problem($"Aucun dossier de transcripts à {directory}", statusCode: 404);

    var since = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : (DateTimeOffset?)null;
    var report = new ParseReport();

    var files = TranscriptReader.FindTranscripts(directory)
        .Where(f => since is null || File.GetLastWriteTimeUtc(f) >= since.Value.UtcDateTime)
        .ToList();

    var sessions = SessionBuilder.Build(files.SelectMany(f => TranscriptReader.Read(f, report)));
    var result = ingestor.Ingest(sessions);

    if (report.LooksBroken)
        log.LogWarning("{Rate:P1} de lignes illisibles : le format des transcripts a peut-être changé",
            report.UnreadableRate);

    log.LogInformation("Ingestion : {Result}", result);
    return Results.Json(new
    {
        directory,
        files = files.Count,
        result.Sessions, result.Tasks, result.Turns, result.ToolCalls, result.Signals,
        parse = new { report.LinesRead, report.LinesUnreadable, rate = report.UnreadableRate, report.LooksBroken },
        warnings = report.Warnings.Take(5),
    });
});

app.MapGet("/status", (SessionRegistry registry, HarnessOptions o) => Results.Json(new
{
    learner = o.LearnerId,
    surface = o.Surface,
    project = o.ProjectName,
    otlp = o.OtlpEndpoint,
    captureContent = o.CaptureContent,
    transcriptRoot = string.IsNullOrWhiteSpace(o.TranscriptRoot) ? TranscriptReader.DefaultRoot : o.TranscriptRoot,
    openSpans = registry.OpenCount,
}));

app.Run();

/// <summary>
/// Correspondance explicite entre le segment d'URL et le nom canonique de
/// l'evenement. Une derivation automatique casserait sur les cas ou les deux
/// divergent (post-tool-fail contre PostToolUseFailure).
/// </summary>
static class Routes
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["session-start"] = "SessionStart",
        ["session-end"] = "SessionEnd",
        ["user-prompt"] = "UserPromptSubmit",
        ["stop"] = "Stop",
        ["stop-failure"] = "StopFailure",
        ["post-tool"] = "PostToolUse",
        ["post-tool-fail"] = "PostToolUseFailure",
        ["post-tool-batch"] = "PostToolBatch",
        ["pre-compact"] = "PreCompact",
        ["post-compact"] = "PostCompact",
        ["subagent-start"] = "SubagentStart",
        ["subagent-stop"] = "SubagentStop",
        ["task-created"] = "TaskCreated",
        ["task-completed"] = "TaskCompleted",
        ["permission-denied"] = "PermissionDenied",
    };
}

/// <summary>
/// Un span n'est exporte qu'a sa fermeture. Une session interrompue sans
/// SessionEnd - editeur ferme brutalement, machine en veille - resterait donc
/// invisible dans Phoenix. Ce balayage la ferme d'office passe le delai d'inactivite.
/// </summary>
internal sealed class IdleSweeper(SessionRegistry registry, ILogger<IdleSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var closed = registry.SweepIdle();
            if (closed > 0) log.LogInformation("{Count} span(s) inactif(s) ferme(s) d'office", closed);
        }
    }
}
