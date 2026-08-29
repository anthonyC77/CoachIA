using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Une lentille : un vocabulaire posé sur le contenu pédagogique, jamais à sa
/// place. Elle donne des mots pour nommer ce que l'apprenant vit déjà — « tu es
/// supply block à 80 % de contexte » dit la même chose que « ta fenêtre sature »,
/// mais à quelqu'un qui a déjà ressenti la sensation, pas seulement compris le
/// concept.
///
/// Trois règles, tenues par le code plutôt que par la bonne volonté :
///
///   1. Le fait passe toujours devant. Une phrase lentillée s'écrit
///      « fait — image », jamais l'inverse : retirez la lentille, la phrase
///      reste vraie et complète (voir <see cref="LensedMessage"/>).
///   2. La lentille ne mesure rien. Aucun point, aucun rang, aucune ligue :
///      elle nomme, elle ne note pas. Ce qui compte est déjà mesuré ailleurs.
///   3. La lentille neutre est complète. Toute clé absente d'une lentille
///      retombe sur elle. Un pack de vocabulaire incomplet dégrade le style,
///      jamais le fond.
/// </summary>
public sealed class Lens
{
    /// <summary>
    /// Vide par défaut, et non « neutre » : un fichier qui omet son identifiant
    /// prendrait sinon la place de la lentille de secours, et la viderait.
    /// </summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "Neutre";
    [JsonPropertyName("tagline")] public string Tagline { get; init; } = "";

    /// <summary>Comment chaque palier se nomme et s'explique dans cet univers.</summary>
    [JsonPropertyName("levels")] public Dictionary<string, LevelLens> Levels { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Les images d'un signal. Plusieurs par clé : une seule reviendrait chaque
    /// semaine, et une bonne image entendue six fois n'en est plus une.
    /// </summary>
    [JsonPropertyName("signals")]
    [JsonConverter(typeof(VariantsConverter))]
    public Dictionary<string, string[]> Signals { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Une ou plusieurs formulations par défi hebdomadaire.</summary>
    [JsonPropertyName("challenges")]
    [JsonConverter(typeof(VariantsConverter))]
    public Dictionary<string, string[]> Challenges { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Une ou plusieurs phrases par situation détectée en fin de session.</summary>
    [JsonPropertyName("moments")]
    [JsonConverter(typeof(VariantsConverter))]
    public Dictionary<string, string[]> Moments { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// La version du jeu à laquelle ce vocabulaire se réfère. Un patch peut
    /// périmer une scène — le jour où le nombre d'ouvriers de départ change, les
    /// build orders cités au supply près deviennent faux. Mieux vaut dater le
    /// pack que laisser le coach affirmer d'un ton sûr quelque chose qui ne l'est
    /// plus.
    /// </summary>
    [JsonPropertyName("patch")] public string Patch { get; init; } = "";

    /// <summary>
    /// Les variantes par camp. On ne joue pas la même partie selon la race, et
    /// surtout : on ne se fait pas punir de la même façon. Un joueur reconnaît
    /// beaucoup mieux une scène qu'il a vécue de son côté de la carte.
    /// </summary>
    [JsonPropertyName("races")] public Dictionary<string, RaceLens> Races { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public RaceLens? Race(string? id)
        => id is not null && Races.TryGetValue(id, out var r) ? r : null;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>
/// Le camp choisi : ce qu'il change au vocabulaire, et rien de plus. Toute clé
/// absente retombe sur la lentille, qui retombe elle-même sur la neutre — un
/// pack de race n'a donc jamais besoin d'être complet pour être utile.
/// </summary>
public sealed class RaceLens
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tagline")] public string Tagline { get; init; } = "";

    /// <summary>La race qui sert d'antagoniste par défaut dans les scènes.</summary>
    [JsonPropertyName("nemesis")] public string Nemesis { get; init; } = "";

    [JsonPropertyName("levels")] public Dictionary<string, LevelLens> Levels { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("signals")]
    [JsonConverter(typeof(VariantsConverter))]
    public Dictionary<string, string[]> Signals { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("challenges")]
    [JsonConverter(typeof(VariantsConverter))]
    public Dictionary<string, string[]> Challenges { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("moments")]
    [JsonConverter(typeof(VariantsConverter))]
    public Dictionary<string, string[]> Moments { get; init; } = new(StringComparer.Ordinal);
}

public sealed class LevelLens
{
    /// <summary>Le nom du palier dans cet univers. « Build order » pour le palier 1.</summary>
    [JsonPropertyName("term")] public string Term { get; init; } = "";

    /// <summary>Deux ou trois phrases qui ouvrent la leçon.</summary>
    [JsonPropertyName("analogy")] public string Analogy { get; init; } = "";

    /// <summary>La faute de débutant, nommée dans le vocabulaire du jeu.</summary>
    [JsonPropertyName("pitfall")] public string Pitfall { get; init; } = "";
}

/// <summary>
/// Un message en deux morceaux qu'on ne peut pas confondre. <see cref="Fact"/>
/// est ce que les traces disent ; <see cref="Flourish"/> est l'image. Le rendu
/// met toujours le fait d'abord, et l'image seulement si elle existe.
/// </summary>
public readonly record struct LensedMessage(string Fact, string? Flourish)
{
    public override string ToString()
        => string.IsNullOrWhiteSpace(Flourish) ? Fact : $"{Fact} — {Flourish}";

    /// <summary>La version sans image, toujours disponible.</summary>
    public string Plain => Fact;
}

/// <summary>
/// Charge les lentilles depuis des fichiers de données. Elles sont volontairement
/// hors du code : ajouter un univers ne doit rien coûter de plus qu'écrire un
/// fichier JSON et le déposer dans le dossier.
/// </summary>
public sealed class LensCatalog
{
    private readonly Dictionary<string, Lens> _lenses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>La lentille de repli, complète par construction.</summary>
    public Lens Neutral { get; private set; } = BuiltInNeutral();

    public IReadOnlyCollection<Lens> All => _lenses.Values;

    public static LensCatalog Load(string? directory, List<string>? warnings = null)
    {
        var catalog = new LensCatalog();
        catalog._lenses[catalog.Neutral.Id] = catalog.Neutral;

        if (directory is null || !Directory.Exists(directory)) return catalog;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            // Le corpus de maturité vit dans le même dossier sans être une
            // lentille : il décrit ce qu'on apprend, pas les mots pour le dire.
            // L'exclusion est nominative plutôt que devinée — un fichier qui
            // « ne ressemble pas à une lentille » est un critère qui se retourne
            // contre le premier pack un peu pauvre.
            if (string.Equals(Path.GetFileName(file), MaturityCorpus.FileName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var lens = JsonSerializer.Deserialize<Lens>(File.ReadAllText(file), Lens.Json);
                if (lens is null || lens.Id.Length == 0)
                { warnings?.Add($"{Path.GetFileName(file)} : lentille sans identifiant, ignorée."); continue; }
                // La lentille de secours porte tout le contenu quand les autres
                // sont absentes ou partielles : on ne la remplace que par une
                // version complète, sinon le coach perdrait ses mots.
                if (string.Equals(lens.Id, "neutre", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsComplete(lens))
                    {
                        warnings?.Add($"{Path.GetFileName(file)} : lentille neutre incomplète, la version intégrée est conservée.");
                        continue;
                    }
                    catalog.Neutral = lens;
                }
                catalog._lenses[lens.Id] = lens;
            }
            catch (JsonException ex)
            {
                // Un pack de vocabulaire mal formé ne doit jamais empêcher le
                // coach de parler : on le signale et on continue en neutre.
                warnings?.Add($"{Path.GetFileName(file)} : {ex.Message}");
            }
        }
        return catalog;
    }

    /// <summary>Les cinq paliers, chacun avec une explication utilisable telle quelle.</summary>
    private static bool IsComplete(Lens lens)
        => Enumerable.Range(1, 5).All(level =>
            lens.Levels.TryGetValue(level.ToString(), out var l)
            && l.Term.Length > 0 && l.Analogy.Length > 0);

    public Lens Resolve(string? id)
        => id is not null && _lenses.TryGetValue(id, out var lens) ? lens : Neutral;

    public bool Knows(string id) => _lenses.ContainsKey(id);

    /// <summary>
    /// La lentille de secours. Les paliers viennent du corpus de maturité : ils
    /// décrivent ce qu'on apprend, pas la façon de le dire, et les recopier ici
    /// aurait créé deux vérités à tenir d'accord.
    /// </summary>
    private static Lens BuiltInNeutral() => new()
    {
        Id = "neutre",
        Name = "Neutre",
        Tagline = "Sans métaphore. Le métier suffit.",
        Levels = new Dictionary<string, LevelLens>(MaturityCorpus.BuiltIn.Levels, StringComparer.Ordinal),
    };
}

/// <summary>
/// Applique une lentille à un fait mesuré. Cette classe est le seul endroit où
/// une image se colle à un chiffre — ce qui rend la règle « le fait d'abord »
/// vérifiable en un seul point.
/// </summary>
public sealed class LensWriter(Lens lens, string? race = null, int? cycle = null)
{
    public Lens Lens { get; } = lens;

    /// <summary>
    /// Le rang de la semaine dans la rotation des images. Fixé une fois pour
    /// toutes à la construction : deux appels dans le même bilan doivent donner
    /// la même scène, et deux régénérations du même bilan aussi.
    /// </summary>
    public int Cycle { get; } = cycle ?? VariantPicker.CycleOf(DateTimeOffset.UtcNow);

    /// <summary>Le même vocabulaire, calé sur une autre semaine.</summary>
    public LensWriter ForWeek(string? week)
        => new(Lens, SideId, VariantPicker.CycleOfWeek(week));

    /// <summary>Le même vocabulaire, calé sur une date.</summary>
    public LensWriter ForDate(DateOnly day) => new(Lens, SideId, VariantPicker.CycleOf(day));

    /// <summary>Le camp choisi, s'il existe dans cette lentille. Sinon : rien, en silence.</summary>
    public RaceLens? Side { get; } = lens.Race(race);

    /// <summary>L'identifiant du camp effectivement retenu, pour l'afficher honnêtement.</summary>
    public string? SideId { get; } = lens.Race(race) is null ? null : race;

    public LensedMessage ForSignal(string signalKey, string fact)
        => new(fact, Pick(Side?.Signals, Lens.Signals, signalKey));

    /// <summary>
    /// La scène d'une problématique, avec repli sur le signal qui la révèle.
    ///
    /// C'est ce repli qui rend l'arrivée des problématiques indolore : les packs
    /// écrits quand seul le signal existait — une centaine de scènes — continuent
    /// de répondre sans être retouchés, exactement comme
    /// <see cref="VariantsConverter"/> lit encore les packs d'avant les tableaux.
    /// </summary>
    public LensedMessage ForProblem(string? problemId, string signalKey, string fact)
        => new(fact, Pick(Side?.Signals, Lens.Signals, problemId, signalKey));

    public LensedMessage ForChallenge(string signalKey, string statement)
        => new(statement, Pick(Side?.Challenges, Lens.Challenges, signalKey));

    /// <summary>Le défi d'une problématique, avec le même repli que les scènes.</summary>
    public LensedMessage ForChallenge(string? problemId, string signalKey, string statement)
        => new(statement, Pick(Side?.Challenges, Lens.Challenges, problemId, signalKey));

    public LensedMessage ForMoment(string momentKey, string fact)
        => new(fact, Pick(Side?.Moments, Lens.Moments, momentKey));

    /// <summary>Toutes les scènes connues pour une clé — le camp d'abord. Sert aux vérifications.</summary>
    public IReadOnlyList<string> VariantsFor(string signalKey)
    {
        if (Side is not null && Side.Signals.TryGetValue(signalKey, out var mine) && mine.Length > 0) return mine;
        return Lens.Signals.GetValueOrDefault(signalKey) ?? [];
    }

    /// <summary>
    /// Le camp d'abord, la lentille ensuite — et, dans chacun, la clé la plus
    /// précise d'abord.
    ///
    /// L'ordre entre les deux axes n'est pas neutre : on épuise tout le pack du
    /// camp avant de retomber sur le générique. Une scène vécue de son côté de
    /// la carte parle mieux qu'une scène mieux ciblée mais racontée d'ailleurs,
    /// et c'est déjà la règle que suivait le pack de race.
    ///
    /// Une clé vide, absente ou sans scène ne masque jamais la suivante.
    /// </summary>
    private string? Pick(Dictionary<string, string[]>? first, Dictionary<string, string[]> then, params string?[] keys)
    {
        return In(first) ?? In(then);

        string? In(Dictionary<string, string[]>? pack)
        {
            if (pack is null) return null;
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (pack.TryGetValue(key, out var scenes) && scenes.Length > 0)
                    return VariantPicker.Pick(scenes, key, Cycle);
            }
            return null;
        }
    }

    public LevelLens ForLevel(int level)
    {
        var key = level.ToString();
        var side = Side?.Levels.GetValueOrDefault(key);
        var basis = Lens.Levels.GetValueOrDefault(key);
        if (side is null) return basis ?? new LevelLens();
        if (basis is null) return side;

        // Un pack de race peut n'affiner qu'une partie du palier : on complète
        // champ par champ plutôt que de tout remplacer, sinon préciser une
        // analogie ferait disparaître le nom du palier.
        return new LevelLens
        {
            Term = side.Term.Length > 0 ? side.Term : basis.Term,
            Analogy = side.Analogy.Length > 0 ? side.Analogy : basis.Analogy,
            Pitfall = side.Pitfall.Length > 0 ? side.Pitfall : basis.Pitfall,
        };
    }

    /// <summary>Le nom du palier dans cette lentille, ou son nom neutre.</summary>
    public string TermFor(int level, string fallback)
    {
        var term = ForLevel(level).Term;
        return term.Length > 0 ? term : fallback;
    }
}
