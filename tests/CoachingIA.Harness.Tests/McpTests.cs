using System.Text.Json.Nodes;
using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Mcp;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du serveur MCP. Il est écrit à la main, donc c'est ici que se
/// vérifie tout ce qu'un SDK aurait garanti : la poignée de main, le silence sur
/// les notifications, une ligne par message, et le fait qu'un outil qui échoue
/// rapporte son échec sans tuer la session.
/// </summary>
public static class McpTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        var corpus = Corpus.Load(lensDir, "starcraft2")!;
        var server = new McpServer(new CorpusTools(corpus));

        Console.WriteLine("Poignée de main");
        var init = Ask(server, 1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["clientInfo"] = new JsonObject { ["name"] = "essai", ["version"] = "1" },
        })!;
        check(init["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18",
              "la révision demandée est renvoyée telle quelle quand on la connaît");
        check(init["result"]?["capabilities"]?["tools"] is not null, "le serveur annonce ses outils");
        check(init["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "coachingia-corpus",
              "et se nomme");
        check(init["result"]?["instructions"] is not null,
              "des instructions accompagnent le serveur : elles disent de vérifier avant d'affirmer");

        var vieux = Ask(new McpServer(new CorpusTools(corpus)), 1, "initialize",
            new JsonObject { ["protocolVersion"] = "1999-01-01" })!;
        check(vieux["result"]?["protocolVersion"]?.GetValue<string>() == McpServer.Supported[0],
              "une révision inconnue ne fait pas échouer la poignée de main : on répond dans la nôtre");

        Console.WriteLine("\nNotifications");
        var rien = server.Handle(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        check(rien is null, "un message sans identifiant ne reçoit pas de réponse");
        check(server.Initialized, "mais il est bien pris en compte");

        Console.WriteLine("\nCatalogue d'outils");
        var list = Ask(server, 2, "tools/list", null)!;
        var tools = list["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        check(names.Count == 4, $"quatre outils exposés ({string.Join(", ", names)})");
        check(names.Contains("chercher_unite") && names.Contains("chercher_terme")
              && names.Contains("punition_pour") && names.Contains("patch"),
              "et ce sont les bons");
        check(tools.All(t => t!["inputSchema"]?["type"]?.GetValue<string>() == "object"),
              "chacun décrit ses paramètres");
        check(tools.First(t => t!["name"]!.GetValue<string>() == "chercher_unite")!["inputSchema"]!["required"]!
                  .AsArray().Count == 1,
              "chercher_unite exige son nom");
        check(!names.Contains("scenes_pour"),
              "aucune scène n'est exposée : la rotation du coach doit rester la sienne");

        Console.WriteLine("\nLes faits sortent justes");
        var dt = Call(server, "chercher_unite", new JsonObject { ["nom"] = "DT" });
        check(!dt.Error, "un alias trouve son unité");
        check(dt.Text.Contains("Dark Templar") && dt.Text.Contains("protoss"), "avec sa race");
        check(dt.Text.Contains("invisible"), "et le trait qui compte");
        check(dt.Text.Contains("5.0.16"), "le patch de référence accompagne la réponse");

        var hydra = Call(server, "chercher_unite", new JsonObject { ["nom"] = "Hydralisk" });
        check(!hydra.Text.Contains("détecteur"), "une unité qui ne détecte pas n'est pas présentée comme détecteur");
        check(hydra.Text.Contains("tire en l'air"), "mais son anti-aérien est dit");

        var inventee = Call(server, "chercher_unite", new JsonObject { ["nom"] = "Hydraviper" });
        check(inventee.Error, "une unité inventée est refusée");
        check(inventee.Text.Contains("Hydralisk") || inventee.Text.Contains("Viper"),
              "avec une suggestion plutôt qu'un mur");

        var terme = Call(server, "chercher_terme", new JsonObject { ["terme"] = "inject" });
        check(!terme.Error && terme.Text.Contains("larves"), "un terme de mécanique est défini");

        // « cheese » figure aussi dans les mécaniques : on prend un mot qui
        // n'existe que dans l'argot, sinon on ne teste pas le bon chemin.
        var argot = Call(server, "chercher_terme", new JsonObject { ["terme"] = "punished" });
        check(!argot.Error && argot.Text.Contains("vernaculaire"), "un mot du ladder aussi");

        var aiguillage = Call(server, "chercher_terme", new JsonObject { ["terme"] = "Overseer" });
        check(!aiguillage.Error && aiguillage.Text.Contains("détecteur"),
              "un nom d'unité posé au mauvais outil est réaiguillé plutôt que refusé");

        var punitions = Call(server, "punition_pour", new JsonObject { ["signal"] = "verification_present" });
        check(!punitions.Error, "les punitions d'un signal sortent");
        check(punitions.Text.Contains("Situation") && punitions.Text.Contains("Coût"),
              "avec le squelette complet d'une scène");

        var parPalier = Call(server, "punition_pour", new JsonObject { ["palier"] = 2 });
        check(!parPalier.Error, "on peut aussi chercher par palier");

        var vide = Call(server, "punition_pour", new JsonObject { ["signal"] = "signal_qui_nexiste_pas" });
        check(vide.Error && vide.Text.Contains("N'en inventez pas"),
              "un signal sans matière dit de ne rien inventer");

        var patch = Call(server, "patch", null);
        check(!patch.Error && patch.Text.Contains("8 ouvriers"), "le patch dit ce qui a changé");
        check(patch.Text.Contains("FAUX"), "et surtout ce qu'il a rendu faux");

        Console.WriteLine("\nCe qui ne doit pas casser la session");
        var inconnu = Call(server, "outil_inexistant", null);
        check(inconnu.Error, "un outil inconnu rapporte son échec dans le résultat, pas en erreur de protocole");

        var sansNom = Ask(server, 9, "tools/call", new JsonObject())!;
        check(sansNom["error"]?["code"]?.GetValue<int>() == -32602,
              "un appel sans nom d'outil est une erreur de paramètres");

        var methode = Ask(server, 10, "methode/inconnue", null)!;
        check(methode["error"]?["code"]?.GetValue<int>() == -32601, "une méthode inconnue rend -32601");

        var ping = Ask(server, 11, "ping", null)!;
        check(ping["result"] is not null, "le ping répond");

        Console.WriteLine("\nLa boucle stdio");
        var entree = string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"patch"}}""",
            "pas du json");

        var sortie = new StringWriter();
        new McpServer(new CorpusTools(corpus)).Pump(new StringReader(entree), sortie);
        var lignes = sortie.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToList();

        check(lignes.Count == 3,
              $"trois réponses : la notification et la ligne vide n'en produisent aucune (obtenu {lignes.Count})");
        check(lignes.All(l => JsonNode.Parse(l) is not null), "chaque ligne est un message complet");
        check(lignes[2].Contains("-32700"), "le JSON illisible est signalé sans interrompre la boucle");
        check(lignes.All(l => !l.Contains('\n')),
              "aucun message ne contient de retour à la ligne — le transport stdio l'interdit");
    }

    private static JsonNode? Ask(McpServer server, int id, string method, JsonObject? parameters)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) request["params"] = parameters;
        return server.Handle(request);
    }

    private static (string Text, bool Error) Call(McpServer server, string tool, JsonObject? arguments)
    {
        var parameters = new JsonObject { ["name"] = tool };
        if (arguments is not null) parameters["arguments"] = arguments;
        var response = Ask(server, 99, "tools/call", parameters)!;
        var result = response["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }
}
