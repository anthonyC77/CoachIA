using System.Text;
using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Mcp;

// Le serveur MCP du corpus : des faits sur StarCraft II, disponibles depuis
// n'importe quelle session Claude Code plutôt que depuis le seul bilan.
//
// Il ne sert que le corpus local. Rien ne part sur le réseau : Liquipedia
// impose de mettre en cache et de ne pas redemander la même donnée, et un
// serveur qu'on interroge librement ferait exactement l'inverse. La récolte
// reste un geste explicite, « coachingia corpus --recolter ».

var lensDir = Arg("--lenses") ?? DefaultLensDir();
var lensId = Arg("--lens") ?? "starcraft2";

var corpus = Corpus.Load(lensDir, lensId);
if (corpus is null)
{
    // Le journal va sur stderr : stdout appartient au protocole, une seule
    // ligne parasite et le client décroche.
    Console.Error.WriteLine($"[coachingia-mcp] corpus introuvable : {Corpus.PathFor(lensDir, lensId)}");
    return 1;
}

Console.Error.WriteLine(
    $"[coachingia-mcp] {corpus.Game}, patch {corpus.Patch.Version} — "
    + $"{corpus.Units.Count} unités, {corpus.Buildings.Count} bâtiments, "
    + $"{corpus.Mechanics.Count} mécaniques, {corpus.Punishments.Count} punitions.");

var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

new McpServer(new CorpusTools(corpus)).Pump(input, output);
return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string DefaultLensDir()
{
    // Le serveur est lancé par le client MCP, depuis un répertoire quelconque :
    // on remonte depuis le binaire jusqu'au dossier « lenses » du dépôt.
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "lenses");
        if (Directory.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    return Path.Combine(Environment.CurrentDirectory, "lenses");
}
