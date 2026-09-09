using System.Diagnostics;
using System.Text.Json;
using CoachingIA.Harness.Core;

// Le harnais se relance lui-meme comme sonde : eprouver ClaudeCli demande un
// vrai processus, et le seul executable dont on soit sur sur n'importe quel
// poste, hors ligne, c'est celui-ci.
if (args.Any(a => a.Contains(CoachingIA.Harness.Tests.SondeCli.Marqueur, StringComparison.Ordinal)))
    return CoachingIA.Harness.Tests.SondeCli.Jouer(args);

// Banc d'essai sans dependance externe : un ActivityListener capture les spans
// produits par SpanFactory, exactement comme le ferait le SDK OpenTelemetry.
// Objectif : verifier la forme des traces (parents, attributs, durees) sans
// avoir besoin d'un Phoenix qui tourne.

var captured = new List<Activity>();
using var listener = new ActivityListener
{
    ShouldListenTo = s => s.Name == SpanFactory.SourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = captured.Add,
};
ActivitySource.AddActivityListener(listener);

var options = new HarnessOptions { LearnerId = "anthony", Surface = "vscode", MaxValueChars = 40 };
using var registry = new SessionRegistry(options.SessionIdleTimeout);
var factory = new SpanFactory(registry, options);

var failures = new List<string>();
void Check(bool condition, string label)
{
    if (condition) { Console.WriteLine($"  ok   {label}"); return; }
    failures.Add(label);
    Console.WriteLine($"  FAIL {label}");
}

static HookEvent Ev(string name, object? extra = null)
{
    var json = JsonSerializer.Serialize(extra ?? new { });
    var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    node["hook_event_name"] = JsonSerializer.SerializeToElement(name);
    return JsonSerializer.Deserialize<HookEvent>(JsonSerializer.Serialize(node), HookEvent.Json)!;
}

static string? Tag(Activity a, string key) => a.GetTagItem(key)?.ToString();
Activity Find(string name) => captured.Last(a => a.OperationName == name);

Console.WriteLine("Session complete");
factory.Handle(Ev("SessionStart", new { session_id = "s1", cwd = "D:/CoachingIA", session_start_reason = "startup" }));
factory.Handle(Ev("UserPromptSubmit", new { session_id = "s1", prompt_id = "p1", user_prompt = "Corrige le bug d'authentification dans auth.cs" }));
factory.Handle(Ev("PostToolUse", new
{
    session_id = "s1",
    prompt_id = "p1",
    tool_name = "Grep",
    tool_use_id = "toolu_1",
    tool_input = new { pattern = "login" },
    tool_response = "3 matches",
    tool_execution_time_ms = 250.0,
}));
factory.Handle(Ev("PostToolUseFailure", new
{
    session_id = "s1", prompt_id = "p1", tool_name = "Bash",
    tool_input = new { command = "dotnet test" }, tool_execution_time_ms = 4000.0,
    error_message = "exit 1",
}));
factory.Handle(Ev("PreCompact", new { session_id = "s1", prompt_id = "p1" }));
factory.Handle(Ev("SubagentStart", new { session_id = "s1", prompt_id = "p1", agent_id = "a1", agent_type = "Explore" }));
factory.Handle(Ev("SubagentStop", new { session_id = "s1", prompt_id = "p1", agent_id = "a1", last_assistant_message = "12 fichiers" }));
factory.Handle(Ev("Stop", new { session_id = "s1", prompt_id = "p1", stop_reason = "end_turn", last_assistant_message = "C'est corrigé." }));
factory.Handle(Ev("SessionEnd", new { session_id = "s1" }));

var turn = Find("turn");
var session = Find("session");
var grep = captured.Last(a => Tag(a, OI.ToolName) == "Grep");
var bash = captured.Last(a => Tag(a, OI.ToolName) == "Bash");
var compact = Find("context.compact");
var agent = Find("agent.Explore");

Check(captured.Count == 6, $"6 spans exportes : ouverture et fermeture donnent un seul span (obtenu {captured.Count})");
Check(session.Parent is null && session.ParentSpanId == default, "la session est un span racine");
Check(turn.ParentSpanId == session.SpanId, "le tour est enfant de la session");
Check(grep.ParentSpanId == turn.SpanId, "l'appel d'outil est enfant du tour");
Check(agent.ParentSpanId == turn.SpanId, "le sous-agent est enfant du tour");
Check(grep.TraceId == session.TraceId, "toute la session partage une trace");

Check(Tag(session, OI.SpanKind) == "AGENT", "session : span kind AGENT");
Check(Tag(turn, OI.SpanKind) == "CHAIN", "tour : span kind CHAIN");
Check(Tag(grep, OI.SpanKind) == "TOOL", "outil : span kind TOOL");
Check(Tag(compact, OI.SpanKind) == "CHAIN", "compaction : span kind CHAIN");

Check(Tag(grep, OI.UserId) == "anthony", "user.id renseigne");
Check(Tag(grep, Coach.Surface) == "vscode", "surface renseignee");
Check(Tag(compact, Coach.Level) == "2", "compaction rattachee au palier 2");
Check(Tag(grep, Coach.Level) == "3", "appel d'outil rattache au palier 3");
Check(Tag(agent, Coach.Level) == "5", "sous-agent rattache au palier 5");
Check(Tag(turn, Coach.Level) == "1", "tour rattache au palier 1");

Check(Math.Abs(grep.Duration.TotalMilliseconds - 250) < 30, $"duree d'outil reconstituee (obtenu {grep.Duration.TotalMilliseconds:F0} ms)");
Check(Math.Abs(bash.Duration.TotalMilliseconds - 4000) < 30, "duree d'outil longue reconstituee");
Check(bash.Status == ActivityStatusCode.Error, "un echec d'outil marque le span en erreur");
Check(Tag(bash, Coach.Signal) == "tool_failure_rate", "un echec d'outil porte le bon signal");
Check(grep.Status != ActivityStatusCode.Error, "un succes d'outil ne marque pas d'erreur");

Check(Tag(turn, OI.InputValue)!.EndsWith("[tronqué]"), "le contenu long est tronque");
Check(Tag(turn, OI.OutputValue) == "C'est corrigé.", "la reponse finale est attachee au tour");
Check(Tag(turn, Coach.Outcome) == "completed", "un tour termine porte outcome=completed");
Check(Tag(grep, OI.InputValue) == "{\"pattern\":\"login\"}", "l'entree d'outil est serialisee en JSON");
Check(Tag(agent, OI.GraphNodeParentId) == turn.SpanId.ToHexString(), "graph.node.parent_id pointe sur le tour");

Console.WriteLine("\nSession sans SessionStart (harnais demarre en cours de route)");
captured.Clear();
factory.Handle(Ev("PostToolUse", new { session_id = "s2", prompt_id = "p2", tool_name = "Read", tool_execution_time_ms = 10.0 }));
var orphan = captured.Single(a => Tag(a, OI.ToolName) == "Read");
Check(captured.Count == 1, "un seul span exporte : la session reste ouverte");
Check(orphan.ParentSpanId != default, "l'outil orphelin est rattache a une session recuperee");
Check(registry.OpenCount > 0, "la session recuperee est enregistree");

Console.WriteLine("\nContenu desactive");
captured.Clear();
var silent = new SpanFactory(registry, new HarnessOptions { CaptureContent = false });
silent.Handle(Ev("UserPromptSubmit", new { session_id = "s3", prompt_id = "p3", user_prompt = "secret" }));
silent.Handle(Ev("Stop", new { session_id = "s3", prompt_id = "p3", stop_reason = "end_turn" }));
var quiet = Find("turn");
Check(Tag(quiet, OI.InputValue) is null, "aucun texte de prompt n'est exporte");
Check(Tag(quiet, OI.SessionId) == "s3", "les metadonnees structurelles restent exportees");

Console.WriteLine("\nEvenement inconnu");
captured.Clear();
Check(factory.Handle(Ev("EvenementQuiNExistePasEncore", new { session_id = "s4" })) is null, "un evenement inconnu est ignore sans erreur");
Check(factory.Handle(Ev("PostToolUse", new { tool_name = "Read" })) is not null, "un evenement sans session_id ne fait pas tomber l'ingestion");

Console.WriteLine("\n────────────────────────────  transcripts  ────────────────────────────\n");
CoachingIA.Harness.Tests.TranscriptTests.Run(Check);

Console.WriteLine("\n────────────────────────────  usage  ────────────────────────────\n");
CoachingIA.Harness.Tests.UsageTests.Run(Check);

Console.WriteLine("\n─────────────────────────  corpus de maturité  ─────────────────────────\n");
CoachingIA.Harness.Tests.MaturiteTests.Run(Check, FindLensDir());

Console.WriteLine("\n────────────────────────────  lentilles  ────────────────────────────\n");
CoachingIA.Harness.Tests.LensTests.Run(Check, FindLensDir());

static string FindLensDir()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "lenses");
        if (Directory.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    return "lenses";
}

Console.WriteLine("\n──────────────────────────────  mcp  ──────────────────────────────\n");
CoachingIA.Harness.Tests.McpTests.Run(Check, FindLensDir());

Console.WriteLine("\n────────────────────────────  corpus  ────────────────────────────\n");
CoachingIA.Harness.Tests.CorpusTests.Run(Check, FindLensDir());

Console.WriteLine("\n────────────────────────────  variantes  ────────────────────────────\n");
CoachingIA.Harness.Tests.VariantTests.Run(Check, FindLensDir());

Console.WriteLine("\n────────────────────────────  bilan  ────────────────────────────\n");
CoachingIA.Harness.Tests.ReviewTests.Run(Check, FindLensDir());

Console.WriteLine("\n──────────────────────────  rétrospective  ──────────────────────────\n");
CoachingIA.Harness.Tests.RetroTests.Run(Check, FindLensDir());

Console.WriteLine("\n────────────────────────────  archives  ────────────────────────────\n");
CoachingIA.Harness.Tests.ArchiveTests.Run(Check);

Console.WriteLine("\n────────────────────────  critique de prompt  ────────────────────────\n");
CoachingIA.Harness.Tests.PromptCriticTests.Run(Check);

Console.WriteLine("\n─────────────────────────────  claude -p  ─────────────────────────────\n");
CoachingIA.Harness.Tests.ClaudeCliTests.Run(Check);

// En dernier, et ce n'est pas un hasard : la suite du corpus de maturité mute
// la façade statique SignalSpecs, dont héritent toutes les suites qui suivent.
// L'évaluation monte donc explicitement le sien plutôt que d'hériter d'un état
// qui dépendrait de l'ordre des suites.
Console.WriteLine("\n───────────────────────────  évaluation  ───────────────────────────\n");
CoachingIA.Harness.Tests.EvaluationTests.Run(Check, FindRepoRoot());

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        if (Directory.Exists(Path.Combine(dir, "evals")) && Directory.Exists(Path.Combine(dir, "lenses")))
            return dir;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    return ".";
}

Console.WriteLine();
if (failures.Count == 0) { Console.WriteLine("Tout est vert."); return 0; }
Console.WriteLine($"{failures.Count} verification(s) en echec :");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;
