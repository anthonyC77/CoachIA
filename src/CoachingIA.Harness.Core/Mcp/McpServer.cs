using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoachingIA.Harness.Core.Mcp;

/// <summary>
/// Un serveur MCP écrit à la main : JSON-RPC 2.0, une requête par ligne, sur
/// l'entrée et la sortie standard.
///
/// Pourquoi sans SDK. Le projet tient une règle depuis le début — aucune
/// dépendance dans le cœur — et elle a une raison pratique : ce qui n'a pas de
/// paquet se compile et s'éprouve partout, y compris là où le registre est
/// fermé. Le protocole tient ici en deux cents lignes ; un SDK aurait coûté
/// plus en surface qu'il n'aurait fait gagner en code.
///
/// <see cref="Handle"/> est une fonction : une requête entre, une réponse sort,
/// rien n'est partagé entre les deux. C'est ce qui rend le protocole vérifiable
/// sans lancer de processus ni ouvrir de tuyau.
/// </summary>
public sealed class McpServer(CorpusTools tools, string name = "coachingia-corpus", string version = "0.1.0")
{
    /// <summary>Les révisions du protocole que ce serveur sait parler.</summary>
    public static readonly string[] Supported = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;

    public CorpusTools Tools { get; } = tools;

    /// <summary>Vrai une fois que le client a dit bonjour. Sert à refuser de travailler avant.</summary>
    public bool Initialized { get; private set; }

    /// <summary>
    /// Traite un message. Rend <c>null</c> pour une notification — un message
    /// sans identifiant n'attend pas de réponse, et en envoyer une casse les
    /// clients stricts.
    /// </summary>
    public JsonNode? Handle(JsonNode? message)
    {
        if (message is not JsonObject request)
            return Error(null, InvalidRequest, "Message JSON-RPC attendu.");

        var id = request["id"];
        var method = request["method"]?.GetValue<string>();

        if (method is null)
            return id is null ? null : Error(id, InvalidRequest, "Méthode absente.");

        // Les notifications n'ont pas d'identifiant : on agit, on ne répond pas.
        if (id is null)
        {
            if (method == "notifications/initialized") Initialized = true;
            return null;
        }

        return method switch
        {
            "initialize" => Initialize(id, request["params"] as JsonObject),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => ListTools(id),
            "tools/call" => CallTool(id, request["params"] as JsonObject),
            "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
            "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
            _ => Error(id, MethodNotFound, $"Méthode inconnue : {method}."),
        };
    }

    private JsonNode Initialize(JsonNode id, JsonObject? parameters)
    {
        // On répond dans la révision demandée si on la connaît, sinon dans la
        // nôtre. Refuser sur ce point ferait échouer la poignée de main pour une
        // différence qui, ici, ne change rien.
        var asked = parameters?["protocolVersion"]?.GetValue<string>();
        var revision = asked is not null && Supported.Contains(asked) ? asked : Supported[0];

        Initialized = true;
        return Result(id, new JsonObject
        {
            ["protocolVersion"] = revision,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = version },
            ["instructions"] =
                "Des faits vérifiés sur StarCraft II, pour écrire des analogies justes : unités, "
                + "termes du ladder, situations de punition, et le patch de référence. "
                + "Vérifiez une unité avant de lui prêter un comportement — surtout la détection, "
                + "l'anti-aérien et l'invisibilité, où les contresens sont les plus fréquents.",
        });
    }

    private JsonNode ListTools(JsonNode id)
    {
        var list = new JsonArray();
        foreach (var spec in Tools.Specs)
            list.Add(new JsonObject
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description,
                ["inputSchema"] = spec.InputSchema.DeepClone(),
            });
        return Result(id, new JsonObject { ["tools"] = list });
    }

    private JsonNode CallTool(JsonNode id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return Error(id, InvalidParams, "Nom d'outil absent.");

        ToolAnswer answer;
        try
        {
            answer = Tools.Call(name, parameters?["arguments"] as JsonObject);
        }
        catch (Exception ex)
        {
            // Un outil qui lève ne doit pas tuer la session : le protocole
            // prévoit de rapporter l'échec dans le résultat, pas en erreur.
            answer = new ToolAnswer($"L'outil a échoué : {ex.Message}", IsError: true);
        }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer.Text }),
            ["isError"] = answer.IsError,
        });
    }

    private static JsonNode Result(JsonNode id, JsonObject result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonNode Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    /// <summary>
    /// La boucle stdio. Une ligne, un message : le transport stdio de MCP
    /// n'admet pas de retour à la ligne à l'intérieur d'un message, ce qui rend
    /// le découpage trivial et le rend surtout indépendant de l'encodage.
    /// </summary>
    public void Pump(TextReader input, TextWriter output)
    {
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;

            JsonNode? response;
            try
            {
                response = Handle(JsonNode.Parse(line));
            }
            catch (JsonException ex)
            {
                response = Error(null, ParseError, "JSON illisible : " + ex.Message);
            }

            if (response is null) continue;
            output.WriteLine(response.ToJsonString(Compact));
            output.Flush();
        }
    }

    /// <summary>Une ligne par message : surtout pas d'indentation, elle contient des retours à la ligne.</summary>
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
