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
/// Un exemple tel que Phoenix le rend (GET /v1/datasets/{id}/examples) : son
/// identifiant réel (un GlobalID encodé en base64, à reporter tel quel dans
/// <see cref="ExperimentRun.ExampleId"/>), ses métadonnées telles que publiées
/// à l'upload, et sa date de dernière mise à jour.
/// </summary>
public sealed record ExemplePhoenix(
    string Id,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset? MisAJour);

/// <summary>
/// Le verdict d'un évaluateur sur un run d'experiment, tel que
/// POST /v1/experiment_evaluations l'attend. Un run n'est pas un span : cette
/// route est distincte de /v1/span_annotations, qu'un run ne peut pas viser
/// (spec §5.0).
/// </summary>
public sealed record RunEvaluation(
    string ExperimentRunId,
    string Name,
    string AnnotatorKind,          // AnnotatorKinds.Code / .Llm
    AnnotationResult Result,
    DateTimeOffset Debut,
    DateTimeOffset Fin,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Les trois routes Phoenix qui manquaient à <see cref="IPhoenixClient"/> pour
/// publier une campagne d'évaluation : retrouver un dataset par nom, lister
/// ses exemples pour connaître leurs identifiants réels, et évaluer un run.
///
/// Interface séparée, volontairement : <c>IPhoenixClient</c> porte le chemin
/// chaud (qui ne lève jamais) et a déjà des implémentations factices dans
/// d'autres suites de tests du chantier — lui ajouter un membre les casserait
/// à la fusion. La publication d'une campagne, elle, est un traitement hors
/// ligne dont l'appelant rattrape les échecs ; ces trois méthodes LÈVENT sur
/// échec HTTP, comme les méthodes datasets/experiments existantes.
/// </summary>
public interface IPhoenixExperiences
{
    Task<string?> TrouverDatasetAsync(string nom, CancellationToken ct);
    Task<IReadOnlyList<ExemplePhoenix>> ListerExemplesAsync(string datasetId, CancellationToken ct);
    Task EvaluerRunAsync(RunEvaluation evaluation, CancellationToken ct);
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
public sealed class PhoenixClient : IPhoenixClient, IPhoenixExperiences, IDisposable
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

    /// <summary>
    /// GET /v1/datasets?name=… — le filtre par nom existe côté serveur, ce qui
    /// dispense de parcourir une liste paginée. Le nom reste vérifié côté
    /// client (égalité exacte sur le champ <c>name</c> de chaque résultat), au
    /// cas où le filtre serveur deviendrait un jour une simple recherche.
    /// Rend <c>null</c> si aucun dataset ne porte ce nom.
    /// </summary>
    public async Task<string?> TrouverDatasetAsync(string nom, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            BuildUri("/v1/datasets?name=" + Uri.EscapeDataString(nom)), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                && string.Equals(n.GetString(), nom, StringComparison.Ordinal))
                return item.GetProperty("id").GetString();

        return null;
    }

    /// <summary>
    /// GET /v1/datasets/{id}/examples. Chaque exemple porte son identifiant
    /// Phoenix réel (celui qu'exige <see cref="ExperimentRun.ExampleId"/>), ses
    /// métadonnées — une valeur JSON qui n'est pas une chaîne est rendue par son
    /// texte JSON brut, pour ne rien perdre de son contenu — et sa date de
    /// dernière mise à jour si la réponse la porte.
    /// </summary>
    public async Task<IReadOnlyList<ExemplePhoenix>> ListerExemplesAsync(string datasetId, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            BuildUri($"/v1/datasets/{Uri.EscapeDataString(datasetId)}/examples"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var exemples = new List<ExemplePhoenix>();
        if (doc.RootElement.GetProperty("data").TryGetProperty("examples", out var liste)
            && liste.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in liste.EnumerateArray())
            {
                var id = e.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("Réponse Phoenix avec un exemple sans identifiant.");

                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                if (e.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
                    foreach (var p in m.EnumerateObject())
                        metadata[p.Name] = p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString() ?? "" : p.Value.GetRawText();

                DateTimeOffset? misAJour = e.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(u.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                    ? dt : null;

                exemples.Add(new ExemplePhoenix(id, metadata, misAJour));
            }
        }

        return exemples;
    }

    /// <summary>
    /// POST /v1/experiment_evaluations. Un run d'experiment n'est pas un span
    /// (spec §5.0) : cette route, distincte de /v1/span_annotations, est la
    /// seule à accepter un <c>experiment_run_id</c>.
    /// </summary>
    public async Task EvaluerRunAsync(RunEvaluation evaluation, CancellationToken ct)
    {
        using var response = await PostJsonAsync("/v1/experiment_evaluations", new
        {
            experiment_run_id = evaluation.ExperimentRunId,
            name = evaluation.Name,
            annotator_kind = evaluation.AnnotatorKind,
            start_time = evaluation.Debut,
            end_time = evaluation.Fin,
            result = evaluation.Result,
            metadata = evaluation.Metadata,
        }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
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
