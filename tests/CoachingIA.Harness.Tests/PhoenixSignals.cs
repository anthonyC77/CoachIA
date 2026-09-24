using System.Diagnostics;
using System.Text.RegularExpressions;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Phoenix;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai de la projection des signaux en annotations Phoenix (spec
/// phoenix-visualisation §4.1) : le nombre d'annotations attendues pour une
/// tâche donnée, l'absence de score sur un signal NaN, l'absence de tout
/// label, et le respect du drapeau PushAnnotations lors d'une ingestion.
/// </summary>
public static class PhoenixSignalTests
{
    private static readonly Regex SpanIdHex = new(@"^[0-9a-f]{16}$", RegexOptions.Compiled);

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Projection pure des signaux");
        {
            var signaux = new List<Signal>
            {
                new("rework_ratio", 1, 0.25, "1 reprise sur 4 tours"),
                new("first_try_success", 1, double.NaN, "tâche encore en cours — issue inconnue"),
                new("verification_present", 3, 1, "vérification lancée : dotnet test"),
                new("context_pressure", 2, 0.42,
                    "pic de contexte lu : 84 000 jetons — largement au-delà de quarante caractères, cette phrase doit être tronquée"),
            };
            var options = new HarnessOptions { MaxValueChars = 40 };
            const string spanId = "aaaa000000000123";

            var annotations = SignalAnnotations.FromSignals(signaux, spanId, options);

            check(annotations.Count == signaux.Count,
                $"le nombre d'annotations produites égale le nombre de signaux (obtenu {annotations.Count} pour {signaux.Count} signaux)");

            var naan = annotations.First(a => a.Name == "first_try_success");
            check(naan.Result.Score is null,
                $"un signal NaN produit une annotation sans score (obtenu {naan.Result.Score?.ToString() ?? "null"})");
            check(!string.IsNullOrEmpty(naan.Result.Explication),
                $"mais avec une explication non vide (obtenu « {naan.Result.Explication} »)");

            check(annotations.All(a => a.Result.Label is null),
                "aucune annotation ne porte de label : les signaux sont des ratios continus sans seuil validé");

            check(annotations.All(a => a.AnnotatorKind == AnnotatorKinds.Code),
                "chaque annotation porte AnnotatorKind = CODE");
            check(annotations.All(a => a.Identifier == a.Name),
                "chaque annotation porte un Identifier égal à son Name — l'upsert évite les doublons au rejeu");
            check(annotations.All(a => a.Metadata is not null
                    && a.Metadata.TryGetValue("level", out _)
                    && a.Metadata.TryGetValue("source", out var src) && src == "transcript"),
                "chaque annotation porte des métadonnées avec les clés level et source=transcript");

            check(annotations.All(a => a.SpanId == spanId),
                $"le SpanId transmis est reporté tel quel sur chaque annotation (obtenu {string.Join(",", annotations.Select(a => a.SpanId).Distinct())})");

            var longue = annotations.First(a => a.Name == "context_pressure");
            var attendue = Cut(signaux.First(s => s.Key == "context_pressure").Evidence, options.MaxValueChars);
            check(longue.Result.Explication == attendue,
                $"avec MaxValueChars=40, l'explication est tronquée exactement comme le reste du code (obtenu {longue.Result.Explication!.Length} car.)");
            check(longue.Result.Explication!.Length <= options.MaxValueChars + "… [tronqué]".Length,
                "et ne dépasse jamais la longueur de troncature appliquée ailleurs dans le code");
        }

        Console.WriteLine("\nIngestion : gabarit d'une session fabriquée");
        var session = FabriqueSession();

        Console.WriteLine("\nPushAnnotations à false : le client factice ne reçoit rien");
        {
            var fake = new FakePhoenixClient();
            var ingestor = new TranscriptIngestor(new HarnessOptions { PushAnnotations = false }, phoenix: fake);
            var result = ingestor.Ingest([session]);

            check(result.Signals > 0, $"l'ingestion calcule bien des signaux (obtenu {result.Signals})");
            check(fake.Received.Count == 0,
                $"PushAnnotations à false : le client factice ne reçoit aucune annotation (obtenu {fake.Received.Count})");
        }

        Console.WriteLine("\nPushAnnotations à true : le client factice reçoit tout, et sur le vrai SpanId de tâche");
        {
            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == SpanFactory.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);

            var fake = new FakePhoenixClient();
            var ingestor = new TranscriptIngestor(new HarnessOptions { PushAnnotations = true }, phoenix: fake);
            var result = ingestor.Ingest([session]);

            check(fake.Received.Count == result.Signals,
                $"PushAnnotations à true : le nombre total d'annotations reçues vaut le nombre de signaux de la session (obtenu {fake.Received.Count} pour {result.Signals} signaux)");

            var taskSpan = captured.Single(a => a.OperationName == "task");
            var spanIdAttendu = taskSpan.SpanId.ToHexString();

            check(SpanIdHex.IsMatch(spanIdAttendu),
                $"le span de tâche porte bien un SpanId hexadécimal de 16 caractères sans préfixe (obtenu « {spanIdAttendu} »)");
            check(fake.Received.All(a => a.SpanId == spanIdAttendu),
                $"le SpanId porté par les annotations est exactement Activity.SpanId.ToHexString() du span de tâche (obtenu {string.Join(",", fake.Received.Select(a => a.SpanId).Distinct())})");

            check(fake.Received.All(a => a.AnnotatorKind == AnnotatorKinds.Code),
                "toutes les annotations reçues portent AnnotatorKind = CODE");
            check(fake.Received.All(a => a.Result.Label is null),
                "aucune des annotations reçues ne porte de label");

            check(taskSpan.GetTagItem("signal.rework_ratio") is not null,
                "l'attribut signal.rework_ratio reste écrit sur le span de tâche, en plus de l'annotation");
            check(taskSpan.GetTagItem("signal.rework_ratio.why") is not null,
                "et sa justification aussi");
        }
    }

    /// <summary>Une session à une tâche, en cours (pour produire un signal NaN au passage).</summary>
    private static TranscriptSession FabriqueSession()
    {
        var t0 = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var session = new TranscriptSession { SessionId = "sig-annot-1", StartedAt = t0, EndedAt = t0.AddMinutes(10) };

        var turn = new Turn
        {
            SessionId = "sig-annot-1",
            StartedAt = t0,
            EndedAt = t0.AddMinutes(2),
            Prompt = "Ajoute un test de non-régression sur le parseur. Il doit passer au vert avant la fin.",
            StopReason = "tool_use", // laisse la tâche en cours : produit un signal NaN (first_try_success, loop_closure)
        };
        turn.Steps.Add(new AssistantStep
        {
            At = t0.AddSeconds(30), Model = "claude-opus-5",
            InputTokens = 1_000, CacheReadTokens = 500, CacheCreationTokens = 0, OutputTokens = 100, ToolUseBlocks = 1,
        });
        turn.ToolCalls.Add(new ToolCall
        {
            Id = "tu1", Name = "Bash", CalledAt = t0.AddSeconds(31), ResultAt = t0.AddSeconds(32),
            InputJson = """{"command":"dotnet test"}""",
        });
        session.Turns.Add(turn);

        return session;
    }

    /// <summary>Même troncature que SignalAnnotations.Cut — dupliquée ici pour éprouver la sortie sans dépendre d'un membre interne.</summary>
    private static string Cut(string value, int maxChars)
        => value.Length <= maxChars
            ? value
            : string.Concat(value.AsSpan(0, maxChars), "… [tronqué]");

    /// <summary>Capture chaque annotation reçue, sans jamais toucher au réseau.</summary>
    private sealed class FakePhoenixClient : IPhoenixClient
    {
        public List<SpanAnnotation> Received { get; } = [];

        public Task AnnotateAsync(IReadOnlyList<SpanAnnotation> annotations, CancellationToken ct)
        {
            Received.AddRange(annotations);
            return Task.CompletedTask;
        }

        public Task<string> UpsertDatasetAsync(string nom, IReadOnlyList<DatasetExample> exemples, CancellationToken ct)
            => Task.FromResult("");

        public Task<string> CreateExperimentAsync(string datasetId, string nom, CancellationToken ct)
            => Task.FromResult("");

        public Task<string> CreateRunAsync(string experimentId, ExperimentRun run, CancellationToken ct)
            => Task.FromResult("");

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
