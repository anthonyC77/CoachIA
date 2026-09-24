using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Phoenix;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du socle Phoenix : la forme du corps JSON envoyé, la reprise
/// après une erreur serveur, le drapeau PushAnnotations, et le vidage de la
/// file par FlushAsync. Tout passe par un HttpMessageHandler factice — pas de
/// réseau — sauf la dernière section, qui tente un vrai Phoenix local et
/// s'ignore d'elle-même si le port ne répond pas.
/// </summary>
public static class PhoenixClientTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Forme du corps envoyé");
        {
            var handler = new FakeHandler();
            var client = new PhoenixClient(handler, new HarnessOptions());

            var annotations = new[]
            {
                new SpanAnnotation("autonomy_ratio", "aaaa000000000001", AnnotatorKinds.Code,
                    new AnnotationResult("bon", 0.8, "3 sur 4 tours sans intervention")),
                new SpanAnnotation("verification_present", "aaaa000000000002", AnnotatorKinds.Code,
                    new AnnotationResult(null, 1.0, "tests lancés avant de conclure")),
                new SpanAnnotation("tool_failure_rate", "aaaa000000000003", AnnotatorKinds.Code,
                    new AnnotationResult(null, 0.0, "aucun échec d'outil"), Identifier: "tool_failure_rate"),
            };

            client.AnnotateAsync(annotations, CancellationToken.None).GetAwaiter().GetResult();
            client.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

            check(handler.Requests.Count == 1,
                $"3 annotations passées en un seul appel produisent une seule requête HTTP (obtenu {handler.Requests.Count})");

            var request = handler.Requests[0];
            check(request.Uri.AbsolutePath == "/v1/span_annotations",
                $"la requête vise /v1/span_annotations (obtenu {request.Uri.AbsolutePath})");

            var data = JsonDocument.Parse(request.Body).RootElement.GetProperty("data");
            check(data.GetArrayLength() == 3, $"le tableau data porte les 3 éléments (obtenu {data.GetArrayLength()})");

            var premier = data[0];
            string[] clesAttendues = ["name", "annotator_kind", "span_id", "result", "metadata", "identifier"];
            check(clesAttendues.All(c => premier.TryGetProperty(c, out _)),
                "chaque élément porte les clés name, annotator_kind, span_id, result, metadata, identifier");
            check(premier.GetProperty("name").GetString() == "autonomy_ratio", "et le nom est le bon");
            check(premier.GetProperty("annotator_kind").GetString() == AnnotatorKinds.Code, "et l'annotator_kind est le bon");
        }

        Console.WriteLine("\nScore absent");
        {
            var handler = new FakeHandler();
            var client = new PhoenixClient(handler, new HarnessOptions());

            var annotation = new SpanAnnotation("signal_indetermine", "bbbb000000000001", AnnotatorKinds.Code,
                new AnnotationResult(null, null, "valeur non calculable sur ce transcript"));

            client.AnnotateAsync([annotation], CancellationToken.None).GetAwaiter().GetResult();
            client.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

            var result = JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("data")[0].GetProperty("result");
            check(!result.TryGetProperty("score", out _), "un Score null ne produit pas de clé score");
            check(result.TryGetProperty("explanation", out var explication), "mais la clé explanation reste présente");
            check(explication.GetString() == "valeur non calculable sur ce transcript", "avec son texte");
        }

        Console.WriteLine("\nReprise après erreur serveur");
        {
            var handler = new FakeHandler { Repondre = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
            var options = new HarnessOptions { AnnotationMaxRetries = 2 };
            var client = new PhoenixClient(handler, options);

            var leve = false;
            try
            {
                client.AnnotateAsync([Annotation("x")], CancellationToken.None).GetAwaiter().GetResult();
                client.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                leve = true;
            }

            check(!leve, "une erreur 500 répétée ne fait remonter aucune exception à AnnotateAsync ni à FlushAsync");
            check(handler.Requests.Count == 3,
                $"1 + AnnotationMaxRetries tentatives sont envoyées, soit 3 avec AnnotationMaxRetries=2 (obtenu {handler.Requests.Count})");
        }

        Console.WriteLine("\nPushAnnotations à false");
        {
            var handler = new FakeHandler();
            var options = new HarnessOptions { PushAnnotations = false };
            var client = new PhoenixClient(handler, options);

            client.AnnotateAsync([Annotation("y")], CancellationToken.None).GetAwaiter().GetResult();
            client.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

            check(handler.Requests.Count == 0, "PushAnnotations à false : le handler ne reçoit aucune requête");
        }

        Console.WriteLine("\nFlushAsync vide la file");
        {
            var handler = new FakeHandler();
            var client = new PhoenixClient(handler, new HarnessOptions());

            // Trois appels séparés : chacun passe par la file interne, traitée
            // en arrière-plan. Sans FlushAsync, rien ne garantit qu'ils soient
            // tous partis au moment où on regarde le compteur.
            client.AnnotateAsync([Annotation("a")], CancellationToken.None).GetAwaiter().GetResult();
            client.AnnotateAsync([Annotation("b")], CancellationToken.None).GetAwaiter().GetResult();
            client.AnnotateAsync([Annotation("c")], CancellationToken.None).GetAwaiter().GetResult();
            client.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

            check(handler.Requests.Count == 3,
                $"après FlushAsync, les 3 lots mis en file ont tous été envoyés (obtenu {handler.Requests.Count})");
        }

        Console.WriteLine("\nTrouverDatasetAsync");
        {
            var handler = new FakeHandler
            {
                Repondre = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"data":[{"id":"RGF0YXNldDox","name":"coachingia-evals","description":"","metadata":{},
                        "created_at":"2026-09-24T10:00:00+00:00","updated_at":"2026-09-24T10:00:00+00:00","example_count":3}],
                        "next_cursor":null}
                        """),
                },
            };
            var client = new PhoenixClient(handler, new HarnessOptions());

            var id = client.TrouverDatasetAsync("coachingia-evals", CancellationToken.None).GetAwaiter().GetResult();
            check(id == "RGF0YXNldDox", $"TrouverDatasetAsync rend l'identifiant du dataset de même nom présent dans la réponse (obtenu {id})");
            check(handler.Requests[^1].Method == HttpMethod.Get && handler.Requests[^1].Uri.AbsolutePath == "/v1/datasets",
                $"la requête est un GET sur /v1/datasets (obtenu {handler.Requests[^1].Method} {handler.Requests[^1].Uri.AbsolutePath})");
        }
        {
            var handler = new FakeHandler
            {
                Repondre = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[],"next_cursor":null}"""),
                },
            };
            var client = new PhoenixClient(handler, new HarnessOptions());

            var id = client.TrouverDatasetAsync("inexistant", CancellationToken.None).GetAwaiter().GetResult();
            check(id is null, $"TrouverDatasetAsync rend null quand aucun dataset ne porte ce nom (obtenu {id ?? "null"})");
        }

        Console.WriteLine("\nListerExemplesAsync");
        {
            var handler = new FakeHandler
            {
                Repondre = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"data":{"dataset_id":"RGF0YXNldDox","version_id":"v1","filtered_splits":[],"examples":[
                          {"id":"RGF0YXNldEV4YW1wbGU6MQ==","node_id":"x","input":{},"output":{},
                           "metadata":{"epreuve_id":"e1","empreinte":"aaa111","niveau":3},
                           "updated_at":"2026-09-24T10:34:39+00:00","source":null}
                        ]}}
                        """),
                },
            };
            var client = new PhoenixClient(handler, new HarnessOptions());

            var exemples = client.ListerExemplesAsync("RGF0YXNldDox", CancellationToken.None).GetAwaiter().GetResult();
            check(handler.Requests[^1].Method == HttpMethod.Get && handler.Requests[^1].Uri.AbsolutePath == "/v1/datasets/RGF0YXNldDox/examples",
                $"la requête est un GET sur /v1/datasets/{{id}}/examples (obtenu {handler.Requests[^1].Uri.AbsolutePath})");
            check(exemples.Count == 1, $"un exemple de la réponse factice donne un ExemplePhoenix (obtenu {exemples.Count})");
            check(exemples[0].Id == "RGF0YXNldEV4YW1wbGU6MQ==", $"avec son identifiant Phoenix (obtenu {exemples[0].Id})");
            check(exemples[0].Metadata["epreuve_id"] == "e1" && exemples[0].Metadata["empreinte"] == "aaa111",
                "et ses métadonnées telles que publiées");
            check(exemples[0].Metadata["niveau"] == "3",
                $"une valeur de métadonnée non chaîne (JSON number) est rendue en texte JSON brut (obtenu « {exemples[0].Metadata["niveau"]} »)");
            check(exemples[0].MisAJour == DateTimeOffset.Parse("2026-09-24T10:34:39+00:00"),
                $"et sa date de mise à jour (obtenu {exemples[0].MisAJour})");
        }

        Console.WriteLine("\nEvaluerRunAsync");
        {
            var handler = new FakeHandler();
            var client = new PhoenixClient(handler, new HarnessOptions());

            var evaluation = new RunEvaluation(
                "RXhwZXJpbWVudFJ1bjox", "conformite_reecriture", AnnotatorKinds.Code,
                new AnnotationResult("conforme", 1.0, "tout va bien"),
                DateTimeOffset.Parse("2026-09-24T10:00:00Z"), DateTimeOffset.Parse("2026-09-24T10:00:01Z"),
                new Dictionary<string, string> { ["preuve"] = "citation exacte" });

            client.EvaluerRunAsync(evaluation, CancellationToken.None).GetAwaiter().GetResult();

            var derniere = handler.Requests[^1];
            check(derniere.Method == HttpMethod.Post && derniere.Uri.AbsolutePath == "/v1/experiment_evaluations",
                $"la requête est un POST sur /v1/experiment_evaluations (obtenu {derniere.Method} {derniere.Uri.AbsolutePath})");

            var corps = JsonDocument.Parse(derniere.Body).RootElement;
            check(corps.GetProperty("experiment_run_id").GetString() == "RXhwZXJpbWVudFJ1bjox", "le corps porte experiment_run_id");
            check(corps.GetProperty("name").GetString() == "conformite_reecriture", "et name");
            check(corps.GetProperty("annotator_kind").GetString() == AnnotatorKinds.Code, "et annotator_kind");
            check(corps.TryGetProperty("start_time", out _) && corps.TryGetProperty("end_time", out _), "et start_time / end_time");
            var result = corps.GetProperty("result");
            check(result.GetProperty("label").GetString() == "conforme", "le result porte label");
            check(result.GetProperty("explanation").GetString() == "tout va bien", "et explanation");
            check(corps.GetProperty("metadata").GetProperty("preuve").GetString() == "citation exacte", "et metadata au niveau racine");
        }
        {
            var handler = new FakeHandler();
            var client = new PhoenixClient(handler, new HarnessOptions());

            var evaluation = new RunEvaluation(
                "run1", "indecis", AnnotatorKinds.Code,
                new AnnotationResult("indeterminable", null, "hors bareme"),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            client.EvaluerRunAsync(evaluation, CancellationToken.None).GetAwaiter().GetResult();

            var result = JsonDocument.Parse(handler.Requests[^1].Body).RootElement.GetProperty("result");
            check(!result.TryGetProperty("score", out _), "un Score null donne un result sans clé score");
        }

        Console.WriteLine("\nErreurs HTTP des méthodes IPhoenixExperiences");
        {
            var handler = new FakeHandler { Repondre = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
            var client = new PhoenixClient(handler, new HarnessOptions());

            check(Leve(() => client.TrouverDatasetAsync("x", CancellationToken.None).GetAwaiter().GetResult()),
                "TrouverDatasetAsync lève quand le serveur répond 500");
            check(Leve(() => client.ListerExemplesAsync("d1", CancellationToken.None).GetAwaiter().GetResult()),
                "ListerExemplesAsync lève quand le serveur répond 500");
            check(Leve(() => client.EvaluerRunAsync(
                    new RunEvaluation("r1", "n", AnnotatorKinds.Code, new AnnotationResult(null, 1.0, "x"),
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    CancellationToken.None).GetAwaiter().GetResult()),
                "EvaluerRunAsync lève quand le serveur répond 500");
        }

        Console.WriteLine("\nIntégration (Phoenix local, facultatif)");
        {
            if (!PortRepond("localhost", 6006, TimeSpan.FromMilliseconds(300)))
            {
                Console.WriteLine("  ignorée : rien ne répond sur localhost:6006 (Phoenix/Docker probablement arrêté)");
            }
            else
            {
                var client = new PhoenixClient(new HttpClient(), new HarnessOptions
                {
                    PhoenixBaseUrl = "http://localhost:6006",
                    AnnotationMaxRetries = 1,
                });

                var leve = false;
                try
                {
                    client.AnnotateAsync([Annotation("integration_span_inconnu")], CancellationToken.None)
                        .GetAwaiter().GetResult();
                    client.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    leve = true;
                }

                check(!leve, "un vrai Phoenix local accepte l'appel sans faire remonter d'exception, même pour un span inconnu");
            }
        }
    }

    private static SpanAnnotation Annotation(string nom)
        => new(nom, "cccc000000000000", AnnotatorKinds.Code, new AnnotationResult(null, 1.0, "essai"));

    private static bool Leve(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    private static bool PortRepond(string hote, int port, TimeSpan delai)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(hote, port).Wait(delai) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Capture chaque requête envoyée par le client, sans jamais toucher au réseau.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Repondre { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"1"}]}""") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.Method, request.RequestUri!, body));
            return Repondre(request);
        }
    }
}
