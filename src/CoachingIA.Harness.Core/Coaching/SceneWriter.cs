using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Fait écrire de nouvelles scènes à partir du corpus.
///
/// L'idée qui rend la chose tenable : <strong>on génère rarement, on range
/// définitivement</strong>. Un appel produit trois scènes qui, une fois relues,
/// serviront pendant des mois. Faire écrire l'image au moment du bilan
/// coûterait un appel par semaine, empêcherait toute relecture, et rendrait le
/// bilan non reproductible — donc inarchivable.
///
/// Le rédacteur ne reçoit jamais de page blanche : il reçoit le registre de
/// voix, les faits du corpus, une punition à mettre en scène, et les scènes
/// déjà écrites pour ne pas les répéter.
/// </summary>
public sealed class SceneWriter
{
    private readonly IClaudeCli _cli;

    public SceneWriter(TimeSpan? timeout = null, string executable = "claude")
        : this(new ClaudeCli(timeout ?? TimeSpan.FromMinutes(3), executable)) { }

    /// <summary>Pour lui donner un autre CLI que celui du poste — c'est ainsi qu'on l'éprouve hors ligne.</summary>
    public SceneWriter(IClaudeCli cli) => _cli = cli;

    private const string Schema = """
        {"type":"object","properties":{
          "scenes":{"type":"array","items":{"type":"string"}}},
         "required":["scenes"]}
        """;

    public List<string> Write(Corpus corpus, string signal, string? race,
                              IReadOnlyList<string> existing, int count, out string? error)
    {
        error = null;
        var spec = SignalSpecs.Find(signal);
        if (spec is null) { error = $"signal inconnu : {signal}"; return []; }

        var punishments = corpus.For(signal).ToList();
        if (punishments.Count == 0)
        {
            error = $"aucune punition pour « {signal} » dans le corpus — "
                  + "il n'y a rien à mettre en scène, et inventer serait exactement ce qu'on veut éviter";
            return [];
        }

        var json = _cli.Demander(Instruction(corpus, spec, race, punishments, existing, count), Schema);
        if (json is null) { error = _cli.DerniereErreur; return []; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("structured_output", out var output)
                || !output.TryGetProperty("scenes", out var scenes)
                || scenes.ValueKind != JsonValueKind.Array)
            {
                error = "réponse sans scènes exploitables";
                return [];
            }

            return scenes.EnumerateArray()
                .Select(s => s.GetString()?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
        }
        catch (JsonException ex)
        {
            error = "réponse illisible : " + ex.Message;
            return [];
        }
    }

    private static string Instruction(Corpus corpus, SignalSpec spec, string? race,
                                      List<Punishment> punishments, IReadOnlyList<string> existing, int count)
    {
        var b = new StringBuilder();

        b.AppendLine("""
            Tu écris des images pour un coach qui aide un développeur à progresser dans
            l'usage des agents de code. Chaque image est une scène de StarCraft II qui
            éclaire un défaut mesuré dans son travail.

            Le ton : un joueur qui a fait mille parties de ladder, fini Or à la sueur de
            son front, et pris des taules face à des rushs qu'il n'avait pas vus venir.
            Précis, un peu meurtri, jamais pédant. Pas quelqu'un qui a vu deux replays et
            se la raconte.
            """);

        b.AppendLine($"\nLe défaut à éclairer : {spec.Complaint}");
        b.AppendLine($"Palier concerné : {spec.Level}.");
        if (race is not null) b.AppendLine($"Le joueur joue {race} : raconte la scène de son côté de la carte.");

        b.AppendLine($"\nLe jeu est au patch {corpus.Patch.Version}. {corpus.Patch.Summary}");
        foreach (var c in corpus.Patch.Changes) b.AppendLine($"- {c}");
        if (corpus.Patch.Obsolete.Length > 0)
        {
            b.AppendLine("\nCe qui est devenu FAUX et ne doit jamais apparaître :");
            foreach (var o in corpus.Patch.Obsolete) b.AppendLine($"- {o}");
        }

        b.AppendLine("\nLes situations à mettre en scène — n'en invente pas d'autres :");
        foreach (var p in punishments)
            b.AppendLine($"- {p.Situation} {p.Missing} {p.Sanction} Coût : {p.Cost}");

        // Seulement les unités utiles : la liste entière noierait l'instruction,
        // et une scène qui pioche au hasard dans cinquante unités sonne faux.
        var relevant = Relevant(corpus, punishments, race).ToList();
        b.AppendLine("\nLes unités que tu peux nommer, avec ce qu'elles font vraiment :");
        foreach (var u in relevant)
        {
            var traits = new List<string>();
            if (u.Air) traits.Add("vole");
            if (u.AntiAir) traits.Add("tire en l'air");
            if (u.Cloaked) traits.Add("invisible ou enterrée");
            if (u.Detector) traits.Add("détecteur");
            var suffix = traits.Count > 0 ? $" [{string.Join(", ", traits)}]" : "";
            b.AppendLine($"- {u.Name} ({u.Race}, {u.Role}){suffix}"
                + (u.Note.Length > 0 ? $" — {u.Note}" : ""));
        }

        b.AppendLine("\nMécaniques utilisables :");
        foreach (var m in corpus.Mechanics.Where(m => m.Level == spec.Level || m.Race == race || m.Race.Length == 0).Take(12))
            b.AppendLine($"- {m.Name} : {m.Definition}");

        if (existing.Count > 0)
        {
            b.AppendLine("\nScènes DÉJÀ écrites pour ce défaut. N'en réécris aucune variante :");
            foreach (var e in existing) b.AppendLine($"- {Shorten(e)}");
        }

        b.AppendLine($"""

            Écris {count} scènes nouvelles. Chacune :
            - fait deux à quatre phrases, en français, en tutoyant le joueur ;
            - situe l'action, nomme l'erreur précise, puis la sanction concrète ;
            - ne nomme que des unités de la liste ci-dessus, et ne leur prête que ce
              qu'elles font réellement ;
            - ne cite jamais un build order en supply absolu ;
            - commence par une minuscule et se termine sans point : elle sera insérée
              après un constat chiffré, comme une suite de phrase ;
            - ne mentionne ni prompt, ni agent, ni contexte, ni test : le parallèle avec
              le métier est fait ailleurs, la scène reste dans le jeu.

            Renvoie « scenes », un tableau de {count} chaînes.
            """);

        return b.ToString();
    }

    /// <summary>Les unités que les punitions mettent en jeu, plus celles du camp du joueur.</summary>
    private static IEnumerable<CorpusUnit> Relevant(Corpus corpus, List<Punishment> punishments, string? race)
    {
        var races = punishments.SelectMany(p => new[] { p.Victim, p.Aggressor })
            .Concat([race ?? ""])
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var named = corpus.Units.Where(u => punishments.Any(p => p.ToString().Contains(u.Name, StringComparison.OrdinalIgnoreCase)));
        var sameSide = races.SelectMany(corpus.Of);
        return named.Concat(sameSide).DistinctBy(u => u.Name).Take(28);
    }

    private static string Shorten(string text)
        => text.Length <= 110 ? text : text[..110] + "…";

}

/// <summary>Une scène proposée, pas encore relue, donc invisible pour le coach.</summary>
public sealed record Proposal(
    [property: JsonPropertyName("signal")] string Signal,
    [property: JsonPropertyName("camp")] string? Race,
    [property: JsonPropertyName("texte")] string Text,
    [property: JsonPropertyName("le")] DateTimeOffset At);

/// <summary>
/// Le sas. Rien de généré n'entre dans la lentille sans passer par ici, et le
/// coach ne lit jamais ce fichier : une scène non relue ne peut donc pas
/// atterrir dans un bilan.
/// </summary>
public sealed class ProposalStore
{
    [JsonPropertyName("propositions")] public List<Proposal> Items { get; init; } = [];

    public static string PathFor(string lensDir, string lensId)
        => Path.Combine(lensDir, "propositions", lensId + ".json");

    public static ProposalStore Load(string lensDir, string lensId)
    {
        var path = PathFor(lensDir, lensId);
        if (!File.Exists(path)) return new ProposalStore();
        try
        {
            return JsonSerializer.Deserialize<ProposalStore>(File.ReadAllText(path), Corpus.Json) ?? new ProposalStore();
        }
        catch (JsonException)
        {
            return new ProposalStore();
        }
    }

    public void Add(string signal, string? race, IEnumerable<string> scenes)
    {
        foreach (var s in scenes)
        {
            if (Items.Any(p => string.Equals(p.Text, s, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(new Proposal(signal, race, s, DateTimeOffset.UtcNow));
        }
    }

    public void Save(string lensDir, string lensId)
    {
        var path = PathFor(lensDir, lensId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Corpus.Json));
    }
}

/// <summary>
/// Insère des scènes acceptées dans un fichier de lentille.
///
/// L'édition passe par l'arbre JSON plutôt que par une sérialisation complète :
/// un pack est écrit à la main, relu à la main, et le réécrire entièrement à
/// chaque ajout brouillerait tout ce qui n'a pas changé.
/// </summary>
public static class LensEditor
{
    public static int Append(string lensPath, IEnumerable<Proposal> accepted)
    {
        var root = JsonNode.Parse(File.ReadAllText(lensPath))?.AsObject()
                   ?? throw new InvalidOperationException($"{lensPath} n'est pas un objet JSON.");

        var added = 0;
        foreach (var group in accepted.GroupBy(p => (p.Race, p.Signal)))
        {
            var signals = Container(root, group.Key.Race);
            var array = signals[group.Key.Signal] switch
            {
                JsonArray existing => existing,
                JsonValue single => new JsonArray(JsonValue.Create(single.GetValue<string>())),
                _ => new JsonArray(),
            };

            foreach (var p in group)
            {
                if (array.Any(n => string.Equals(n?.GetValue<string>(), p.Text, StringComparison.OrdinalIgnoreCase)))
                    continue;
                array.Add(JsonValue.Create(p.Text));
                added++;
            }
            signals[group.Key.Signal] = array;
        }

        File.WriteAllText(lensPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return added;
    }

    private static JsonObject Container(JsonObject root, string? race)
    {
        if (race is null)
        {
            if (root["signals"] is not JsonObject signals) root["signals"] = signals = new JsonObject();
            return signals;
        }

        if (root["races"] is not JsonObject races) root["races"] = races = new JsonObject();
        if (races[race] is not JsonObject pack) races[race] = pack = new JsonObject();
        if (pack["signals"] is not JsonObject raceSignals) pack["signals"] = raceSignals = new JsonObject();
        return raceSignals;
    }
}
