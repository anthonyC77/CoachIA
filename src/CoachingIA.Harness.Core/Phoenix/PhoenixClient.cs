using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace CoachingIA.Harness.Core.Phoenix;

/// <summary>
/// Le verdict porté par une annotation. Un champ null n'est jamais sérialisé :
/// c'est le seul moyen de dire « pas de label » ou « score indéterminé » sans
/// mentir avec un zéro ou une chaîne vide dans les moyennes de Phoenix.
/// </summary>
public sealed record AnnotationResult(
    [property: JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    [property: JsonPropertyName("score"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Score,
    [property: JsonPropertyName("explanation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Explication);

/// <summary>
/// Une annotation sur un span existant. `Identifier`, renseigné, fait l'upsert
/// côté Phoenix : rejouer une ingestion ne duplique donc rien.
/// </summary>
public sealed record SpanAnnotation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("span_id")] string SpanId,              // hexadécimal sans préfixe
    [property: JsonPropertyName("annotator_kind")] string AnnotatorKind,       // AnnotatorKinds.Code / .Llm / .Humain
    [property: JsonPropertyName("result")] AnnotationResult Result,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata = null,
    [property: JsonPropertyName("identifier")] string? Identifier = null);

/// <summary>Les trois valeurs admises par `annotator_kind` côté Phoenix.</summary>
public static class AnnotatorKinds
{
    public const string Code = "CODE";
    public const string Llm = "LLM";
    public const string Humain = "HUMAN";
}

/// <summary>Un exemple d'un jeu d'épreuves, tel que publié dans un dataset Phoenix.</summary>
public sealed record DatasetExample(
    IReadOnlyDictionary<string, string> Input,
    IReadOnlyDictionary<string, string> Output,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>Une épreuve jouée dans le cadre d'un experiment Phoenix.</summary>
public sealed record ExperimentRun(
    string ExampleId,
    string Sortie,
    DateTimeOffset Debut,
    DateTimeOffset Fin,
    string? Erreur = null);

/// <summary>
/// Parle HTTP à Phoenix, et rien d'autre : ni les signaux, ni les verdicts, ne
/// sont de son ressort. L'interface existe pour que les unités qui en dépendent
/// s'éprouvent contre un faux client, dans le harnais de tests synchrone qui
/// n'a pas de conteneur d'injection.
/// </summary>
public interface IPhoenixClient
{
    Task AnnotateAsync(IReadOnlyList<SpanAnnotation> annotations, CancellationToken ct);
    Task<string> UpsertDatasetAsync(string nom, IReadOnlyList<DatasetExample> exemples, CancellationToken ct);
    Task<string> CreateExperimentAsync(string datasetId, string nom, CancellationToken ct);
    Task<string> CreateRunAsync(string experimentId, ExperimentRun run, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// Client REST de Phoenix. `HttpClient` et `System.Text.Json`, rien d'autre —
/// `CoachingIA.Harness.Core` n'a aucune dépendance NuGet externe.
///
/// Les annotations ne sont jamais envoyées en ligne dans `AnnotateAsync` : un
/// lot passe dans un <see cref="Channel{T}"/> consommé en arrière-plan, avec
/// reprise et recul exponentiel. C'est la règle d'or du harnais, valable mot
/// pour mot pour les annotations : une trace perdue est un incident mineur, un
/// tour bloqué ne l'est pas. `FlushAsync` existe pour l'appelant qui, lui, sait
/// qu'il a fini et veut la garantie que la file est vidée avant de rendre la
/// main (voir POST /ingest dans le hôte web).
///
/// Le spike consigné dans docs/phoenix-spike-annotation.md a tranché la course
/// à lever du §3.1 : posté sans le paramètre `sync`, Phoenix répond toujours
/// `200 {"data":[]}`, que le span existe ou non — la réponse ne dit donc rien
/// du succès réel. Posté avec `?sync=true`, un span inconnu répond `404`, et un
/// span connu répond `200` avec l'annotation créée. Le client poste donc en
/// synchrone : le coût (attendre que Phoenix ait traité l'annotation) ne
/// remonte à personne, puisque tout part de la file en arrière-plan.
/// </summary>
public sealed class PhoenixClient : IPhoenixClient, IDisposable
{
    private const string AnnotatePath = "/v1/span_annotations?sync=true";

    private readonly HttpClient _http;
    private readonly HarnessOptions _options;
    private readonly Channel<IReadOnlyList<SpanAnnotation>> _queue =
        Channel.CreateUnbounded<IReadOnlyList<SpanAnnotation>>(new UnboundedChannelOptions { SingleReader = true });

    // Compte les lots en file ou en cours d'envoi, pour que FlushAsync sache
    // attendre sans interroger la file à l'aveugle.
    private readonly object _sync = new();
    private int _pending;
    private TaskCompletionSource _idle = Done();

    public PhoenixClient(HttpClient httpClient, HarnessOptions options)
    {
        _http = httpClient;
        _options = options;
        // Le consommateur tourne pour la durée de vie du client ; il s'arrête
        // de lui-même quand Dispose complète la file.
        _ = Task.Run(ConsumeAsync);
    }

    public PhoenixClient(HttpMessageHandler handler, HarnessOptions options)
        : this(new HttpClient(handler), options)
    {
    }

    public Task AnnotateAsync(IReadOnlyList<SpanAnnotation> annotations, CancellationToken ct)
    {
        // À false, plus aucune annotation ne part - les spans, eux, continuent
        // par une voie entièrement différente (l'exporteur OTLP).
        if (!_options.PushAnnotations || annotations is null || annotations.Count == 0)
            return Task.CompletedTask;

        lock (_sync)
        {
            if (_pending == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending++;
        }

        // La file est non bornée : l'écriture ne peut échouer que si elle a été
        // explicitement complétée (à la destruction du client), auquel cas il
        // n'y a plus personne pour consommer le lot - on le compte comme fait.
        if (!_queue.Writer.TryWrite(annotations))
            MarkOneDone();

        return Task.CompletedTask;
    }

    public async Task<string> UpsertDatasetAsync(string nom, IReadOnlyList<DatasetExample> exemples, CancellationToken ct)
    {
        var response = await UploadDataset(nom, exemples, "create", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Le jeu existe déjà : on ajoute une version plutôt que d'échouer,
            // pour qu'une campagne rejouée ne bloque pas la publication suivante.
            response.Dispose();
            response = await UploadDataset(nom, exemples, "append", ct).ConfigureAwait(false);
        }

        using var _ = response;
        response.EnsureSuccessStatusCode();
        return await ReadJsonField(response, "dataset_id", ct).ConfigureAwait(false);
    }

    public async Task<string> CreateExperimentAsync(string datasetId, string nom, CancellationToken ct)
    {
        using var response = await PostJsonAsync(
            $"/v1/datasets/{Uri.EscapeDataString(datasetId)}/experiments",
            new { name = nom }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadJsonField(response, "id", ct).ConfigureAwait(false);
    }

    public async Task<string> CreateRunAsync(string experimentId, ExperimentRun run, CancellationToken ct)
    {
        using var response = await PostJsonAsync(
            $"/v1/experiments/{Uri.EscapeDataString(experimentId)}/runs",
            new
            {
                dataset_example_id = run.ExampleId,
                output = run.Sortie,
                repetition_number = 1,
                start_time = run.Debut,
                end_time = run.Fin,
                error = run.Erreur,
            }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadJsonField(response, "id", ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        Task idle;
        lock (_sync)
        {
            if (_pending == 0) return;
            idle = _idle.Task;
        }

        try
        {
            await idle.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Un flush qui n'aboutit pas n'est pas une exception pour
            // l'appelant : même règle d'or que pour AnnotateAsync.
        }
    }

    public void Dispose() => _queue.Writer.TryComplete();

    private async Task ConsumeAsync()
    {
        await foreach (var batch in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await SendWithRetryAsync(batch).ConfigureAwait(false);
            }
            catch
            {
                // Rien de ce que fait l'envoi ne doit faire tomber le
                // consommateur : une seule mauvaise réponse ne doit pas priver
                // les lots suivants de leur chance d'être envoyés.
            }
            finally
            {
                MarkOneDone();
            }
        }
    }

    private async Task SendWithRetryAsync(IReadOnlyList<SpanAnnotation> batch)
    {
        var body = JsonSerializer.Serialize(new { data = batch });
        var attempts = 1 + Math.Max(0, _options.AnnotationMaxRetries);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(BuildUri(AnnotatePath), content).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch
            {
                // Erreur réseau : traitée exactement comme une mauvaise
                // réponse, on retente selon le même recul.
            }

            if (attempt < attempts)
                await Task.Delay(Backoff(attempt)).ConfigureAwait(false);
        }

        Console.Error.WriteLine(
            $"[Phoenix] {batch.Count} annotation(s) abandonnée(s) après {attempts} tentative(s) : "
            + string.Join(", ", batch.Select(a => a.Name)));
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));

    private async Task<HttpResponseMessage> UploadDataset(
        string nom, IReadOnlyList<DatasetExample> exemples, string action, CancellationToken ct)
        => await PostJsonAsync("/v1/datasets/upload?sync=true", new
        {
            action,
            name = nom,
            inputs = exemples.Select(e => e.Input).ToArray(),
            outputs = exemples.Select(e => e.Output).ToArray(),
            metadata = exemples.Select(e => e.Metadata).ToArray(),
        }, ct).ConfigureAwait(false);

    private async Task<HttpResponseMessage> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _http.PostAsync(BuildUri(path), content, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadJsonField(HttpResponseMessage response, string field, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("data").GetProperty(field).GetString()
            ?? throw new InvalidOperationException($"Réponse Phoenix sans champ « {field} ».");
    }

    private string BuildUri(string path) => _options.PhoenixBaseUrl.TrimEnd('/') + path;

    private void MarkOneDone()
    {
        TaskCompletionSource? toSignal = null;
        lock (_sync)
        {
            if (_pending > 0) _pending--;
            if (_pending == 0) toSignal = _idle;
        }
        toSignal?.TrySetResult();
    }

    private static TaskCompletionSource Done()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }
}
