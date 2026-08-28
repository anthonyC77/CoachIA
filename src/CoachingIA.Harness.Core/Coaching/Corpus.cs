using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>Une unité du jeu, réduite à ce qui sert à écrire — et à vérifier — une scène.</summary>
public sealed class CorpusUnit
{
    [JsonPropertyName("nom")] public string Name { get; init; } = "";
    [JsonPropertyName("alias")] public string[] Alias { get; init; } = [];
    [JsonPropertyName("race")] public string Race { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("note")] public string Note { get; init; } = "";

    [JsonPropertyName("aerien")] public bool Air { get; init; }
    [JsonPropertyName("anti_aerien")] public bool AntiAir { get; init; }
    [JsonPropertyName("invisible")] public bool Cloaked { get; init; }
    [JsonPropertyName("detecteur")] public bool Detector { get; init; }

    [JsonPropertyName("contre")] public string[] Counters { get; init; } = [];
    [JsonPropertyName("contre_par")] public string[] CounteredBy { get; init; } = [];

    /// <summary>Chiffres récoltés, laissés à part : ils viennent d'ailleurs et se périment autrement.</summary>
    [JsonPropertyName("chiffres")] public Dictionary<string, string> Figures { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Toutes les façons de la nommer dans une scène, la plus longue d'abord.</summary>
    public IEnumerable<string> AllNames()
        => Alias.Append(Name).Where(n => n.Length > 0).OrderByDescending(n => n.Length);
}

public sealed class CorpusMechanic
{
    [JsonPropertyName("nom")] public string Name { get; init; } = "";
    [JsonPropertyName("race")] public string Race { get; init; } = "";
    [JsonPropertyName("palier")] public int Level { get; init; }
    [JsonPropertyName("definition")] public string Definition { get; init; } = "";
}

public sealed class CorpusTerm
{
    [JsonPropertyName("terme")] public string Term { get; init; } = "";
    [JsonPropertyName("definition")] public string Definition { get; init; } = "";
}

/// <summary>
/// Une punition : le squelette d'une scène. Situation, ce qui manquait, ce qui
/// arrive, ce que ça coûte.
///
/// C'est la partie du corpus qu'aucun wiki ne donne. Les statistiques d'unités
/// se récoltent ; « il n'avait pas scouté, alors le Vaisseau de guerre est
/// arrivé sur une base sans anti-aérien » ne se récolte pas — ça vient des
/// parties qu'on a perdues.
/// </summary>
public sealed class Punishment
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("palier")] public int Level { get; init; }
    [JsonPropertyName("signal")] public string Signal { get; init; } = "";
    [JsonPropertyName("victime")] public string Victim { get; init; } = "";
    [JsonPropertyName("agresseur")] public string Aggressor { get; init; } = "";
    [JsonPropertyName("situation")] public string Situation { get; init; } = "";
    [JsonPropertyName("manque")] public string Missing { get; init; } = "";
    [JsonPropertyName("sanction")] public string Sanction { get; init; } = "";
    [JsonPropertyName("cout")] public string Cost { get; init; } = "";

    public override string ToString()
        => $"{Situation} {Missing} {Sanction} {Cost}";
}

public sealed class CorpusPatch
{
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("resume")] public string Summary { get; init; } = "";
    [JsonPropertyName("changements")] public string[] Changes { get; init; } = [];

    /// <summary>Les formulations que ce patch a rendues fausses.</summary>
    [JsonPropertyName("perime")] public string[] Obsolete { get; init; } = [];
}

/// <summary>
/// Le fond de connaissance d'un univers : des faits, jamais des scènes.
///
/// La séparation est le point important. Les scènes vivent dans la lentille et
/// sont là pour être lues ; le corpus est là pour être <em>vérifié contre</em>.
/// Mélanger les deux donnerait un fichier que personne ne relit et que rien ne
/// contrôle.
/// </summary>
public sealed class Corpus
{
    [JsonPropertyName("jeu")] public string Game { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("patch")] public CorpusPatch Patch { get; init; } = new();
    [JsonPropertyName("unites")] public List<CorpusUnit> Units { get; init; } = [];

    /// <summary>
    /// Les bâtiments. Ils ne se battent pas, mais ils datent les scènes : « le
    /// Fusion Core est sorti pendant que tu regardais ailleurs » raconte une
    /// partie entière en six mots.
    /// </summary>
    [JsonPropertyName("batiments")] public List<CorpusUnit> Buildings { get; init; } = [];
    [JsonPropertyName("mecaniques")] public List<CorpusMechanic> Mechanics { get; init; } = [];
    [JsonPropertyName("vernaculaire")] public List<CorpusTerm> Vernacular { get; init; } = [];
    [JsonPropertyName("punitions")] public List<Punishment> Punishments { get; init; } = [];

    public bool IsEmpty => Units.Count == 0;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Charge le corpus d'une lentille, s'il existe. Son absence n'est pas une erreur.</summary>
    public static Corpus? Load(string? lensDir, string lensId)
    {
        if (lensDir is null) return null;
        var path = PathFor(lensDir, lensId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Corpus>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string PathFor(string lensDir, string lensId)
        => Path.Combine(lensDir, "corpus", lensId + ".json");

    /// <summary>Une unité par son nom ou l'un de ses alias.</summary>
    public CorpusUnit? Find(string name)
        => Units.FirstOrDefault(u => u.AllNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Un bâtiment par son nom ou l'un de ses alias.</summary>
    public CorpusUnit? FindBuilding(string name)
        => Buildings.FirstOrDefault(u => u.AllNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Tout ce qui porte un nom propre dans le jeu : unités et bâtiments.</summary>
    public IEnumerable<CorpusUnit> Named => Units.Concat(Buildings);

    public IEnumerable<CorpusUnit> Of(string race)
        => Units.Where(u => string.Equals(u.Race, race, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Punishment> For(string signal)
        => Punishments.Where(p => string.Equals(p.Signal, signal, StringComparison.Ordinal));
}
