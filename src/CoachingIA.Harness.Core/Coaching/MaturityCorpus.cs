using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Une problématique : l'unité de <em>discours</em> du coach, là où le signal est
/// l'unité de <em>mesure</em>.
///
/// La distinction porte tout le reste. « Vos prompts ne disent pas quand c'est
/// fini », « il faut souvent reprendre » et « peu de tâches aboutissent du
/// premier coup » sont trois chiffres, mais un seul reproche — et c'est au
/// reproche qu'on attache une image, pas au ratio. C'est ce qui permet d'avoir
/// plusieurs jargons pour « maîtriser l'orchestrateur » sans devoir inventer
/// trois métriques pour les porter.
/// </summary>
public sealed class Problematique
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("palier")] public int Level { get; init; }
    [JsonPropertyName("titre")] public string Title { get; init; } = "";

    /// <summary>Les signaux mesurés qui, ensemble, révèlent cette problématique.</summary>
    [JsonPropertyName("signaux")] public string[] Signals { get; init; } = [];

    [JsonPropertyName("defi")] public DefiEntry? Challenge { get; init; }
}

/// <summary>
/// Le défi d'une problématique. Il porte sa propre cible, volontairement
/// détachée de celle du signal : un exercice d'une semaine ne se juge pas au
/// même seuil qu'une tendance de fond, et certains défis visent un signal que
/// les transcripts ne mesurent pas encore.
/// </summary>
public sealed class DefiEntry
{
    [JsonPropertyName("signal")] public string SignalKey { get; init; } = "";
    [JsonPropertyName("enonce")] public string Statement { get; init; } = "";
    [JsonPropertyName("pourquoi")] public string Why { get; init; } = "";
    [JsonPropertyName("verification")] public string Verification { get; init; } = "";
    [JsonPropertyName("besoin_hooks")] public bool NeedsHooks { get; init; }
    [JsonPropertyName("cible")] public double? Target { get; init; }
    [JsonPropertyName("plus_haut_est_mieux")] public bool? HigherIsBetter { get; init; }
}

/// <summary>
/// Un signal déclaré. <see cref="Target"/> absente signifie « mesuré, jamais
/// reproché » : le signal s'affiche et compte dans les tendances, mais ne
/// déclenche aucune observation.
///
/// C'est le garde-fou anti-Goodhart écrit dans le format lui-même. Donner une
/// cible à <c>mcp_utilization</c> ferait gronder quelqu'un pour ne pas appeler
/// MCP, donc pousserait à l'appeler pour rien. Un chiffre dont on ne sait pas
/// dire honnêtement où il devrait être n'a pas de cible — et le dire coûte un
/// champ absent, pas un débat.
/// </summary>
public sealed class SignalEntry
{
    [JsonPropertyName("cle")] public string Key { get; init; } = "";
    [JsonPropertyName("palier")] public int Level { get; init; }
    [JsonPropertyName("cible")] public double? Target { get; init; }
    [JsonPropertyName("plus_haut_est_mieux")] public bool HigherIsBetter { get; init; } = true;

    /// <summary>Faux pour une valeur absolue (un nombre d'outils, pas une part).</summary>
    [JsonPropertyName("ratio")] public bool IsRatio { get; init; } = true;

    /// <summary>« transcripts » par défaut ; « hooks » pour ce que le lot ne peut pas voir.</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "transcripts";

    [JsonPropertyName("constat")] public string Complaint { get; init; } = "";
    [JsonPropertyName("eloge")] public string Praise { get; init; } = "";
    [JsonPropertyName("conseil")] public string Advice { get; init; } = "";

    /// <summary>Pourquoi ce signal n'a pas de cible. Lu par les humains, jamais par le code.</summary>
    [JsonPropertyName("commentaire")] public string Note { get; init; } = "";

    public bool IsGraded => Target is not null;

    public SignalSpec ToSpec() => new(
        Key, Level, HigherIsBetter, Target ?? 0,
        Complaint, Praise, Advice, IsRatio);
}

public sealed class MomentEntry
{
    [JsonPropertyName("cle")] public string Key { get; init; } = "";
    [JsonPropertyName("texte")] public string Text { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "transcripts";
}

/// <summary>
/// Le corpus de maturité : ce qu'on apprend, indépendamment des mots pour le
/// dire. Les lentilles posent un vocabulaire par-dessus et ne touchent ni aux
/// cibles, ni aux signaux, ni aux conseils.
///
/// Il vit en données pour la même raison que les lentilles : ajouter une
/// problématique ne doit pas demander de recompiler. La version intégrée
/// ci-dessous n'est pas un doublon de confort — c'est ce qui garantit qu'un
/// fichier absent, mal formé ou incomplet dégrade le contenu sans jamais faire
/// taire le coach, exactement comme <see cref="LensCatalog.Neutral"/>.
/// </summary>
public sealed class MaturityCorpus
{
    [JsonPropertyName("version")] public string Version { get; init; } = "1";
    [JsonPropertyName("paliers")] public Dictionary<string, LevelLens> Levels { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("problematiques")] public List<Problematique> Problems { get; init; } = [];
    [JsonPropertyName("signaux")] public List<SignalEntry> Signals { get; init; } = [];
    [JsonPropertyName("moments")] public List<MomentEntry> Moments { get; init; } = [];

    public const string FileName = "maturite.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ------------------------------------------------------------- lectures

    /// <summary>Les signaux notés, dans l'ordre du fichier. C'est ce que voit le bilan.</summary>
    public IReadOnlyList<SignalSpec> Specs =>
        _specs ??= [.. Signals.Where(s => s.IsGraded).Select(s => s.ToSpec())];
    private IReadOnlyList<SignalSpec>? _specs;

    private Dictionary<string, SignalEntry>? _byKey;
    private Dictionary<string, SignalSpec>? _specByKey;
    private Dictionary<string, string>? _problemOfSignal;

    public SignalEntry? Signal(string key)
        => (_byKey ??= Signals.ToDictionary(s => s.Key, StringComparer.Ordinal)).GetValueOrDefault(key);

    /// <summary>La grille d'un signal noté. Null pour un signal mesuré sans cible.</summary>
    public SignalSpec? Spec(string key)
        => (_specByKey ??= Specs.ToDictionary(s => s.Key, StringComparer.Ordinal)).GetValueOrDefault(key);

    /// <summary>La problématique à laquelle un signal appartient, s'il en a une.</summary>
    public string? ProblemOf(string signalKey)
        => (_problemOfSignal ??= Problems
                .SelectMany(p => p.Signals.Select(s => (Signal: s, p.Id)))
                .GroupBy(x => x.Signal, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal))
            .GetValueOrDefault(signalKey);

    public Problematique? Problem(string id)
        => Problems.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public MomentEntry? Moment(string key)
        => Moments.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    /// <summary>Le texte d'un moment, ses marques remplies. Rend null si le moment n'existe pas.</summary>
    public string? MomentText(string key, IReadOnlyDictionary<string, string> values)
    {
        var text = Moment(key)?.Text;
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return text;
    }

    // ------------------------------------------------------------ chargement

    /// <summary>
    /// Charge le corpus depuis le dossier des lentilles. Un fichier absent ou
    /// illisible n'est pas une erreur : on retombe sur la version intégrée, et
    /// on le signale.
    /// </summary>
    public static MaturityCorpus Load(string? lensDir, List<string>? warnings = null)
    {
        if (lensDir is null) return BuiltIn;
        var path = Path.Combine(lensDir, FileName);
        if (!File.Exists(path)) return BuiltIn;

        try
        {
            var corpus = JsonSerializer.Deserialize<MaturityCorpus>(File.ReadAllText(path), Json);
            if (corpus is null)
            {
                warnings?.Add($"{FileName} : fichier vide — la version intégrée est conservée.");
                return BuiltIn;
            }
            if (!corpus.IsUsable(out var why))
            {
                warnings?.Add($"{FileName} : {why} — la version intégrée est conservée.");
                return BuiltIn;
            }
            return corpus;
        }
        catch (JsonException ex)
        {
            warnings?.Add($"{FileName} : {ex.Message} — la version intégrée est conservée.");
            return BuiltIn;
        }
    }

    /// <summary>
    /// Un corpus partiel prendrait la place du complet et retirerait des mots au
    /// coach sans prévenir. On refuse donc de le monter s'il ne tient pas debout.
    /// </summary>
    private bool IsUsable(out string why)
    {
        if (Signals.Count == 0) { why = "aucun signal déclaré"; return false; }
        if (Problems.Count == 0) { why = "aucune problématique déclarée"; return false; }
        if (!Signals.Any(s => s.IsGraded)) { why = "aucun signal noté : le bilan n'aurait rien à dire"; return false; }

        for (var level = 1; level <= 5; level++)
            if (!Levels.TryGetValue(level.ToString(), out var l) || l.Term.Length == 0 || l.Analogy.Length == 0)
            { why = $"palier {level} incomplet"; return false; }

        why = "";
        return true;
    }

    /// <summary>
    /// La version intégrée. Elle ne porte que ce dont le coach ne peut pas se
    /// passer — les cinq paliers et les signaux notés — et laisse volontairement
    /// de côté les signaux sans cible et les commentaires, qui sont du contenu
    /// et non de la mécanique.
    /// </summary>
    public static MaturityCorpus BuiltIn { get; } = new()
    {
        Levels = new Dictionary<string, LevelLens>(StringComparer.Ordinal)
        {
            ["1"] = new() { Term = "Prompt", Analogy = "Formuler une intention que le modèle peut exécuter sans deviner.", Pitfall = "Lancer sans dire à quoi ressemble le résultat attendu." },
            ["2"] = new() { Term = "Contexte", Analogy = "Traiter la fenêtre comme une ressource rare.", Pitfall = "Attendre la saturation au lieu de compacter." },
            ["3"] = new() { Term = "Harnais", Analogy = "Équiper l'agent plutôt que le corriger.", Pitfall = "Tout faire à la main quand un outil existe." },
            ["4"] = new() { Term = "Boucle", Analogy = "Laisser l'agent tourner jusqu'à un critère vérifiable.", Pitfall = "Reprendre la main à chaque étape." },
            ["5"] = new() { Term = "Graphe", Analogy = "Orchestrer des agents spécialisés qui se passent le relais.", Pitfall = "Rejouer un nœud échoué à l'identique." },
        },
        Problems =
        [
            new() { Id = "demande_non_executable", Level = 1, Title = "La demande n'est pas exécutable telle quelle",
                    Signals = ["has_acceptance_criteria", "rework_ratio", "first_try_success", "prompts_per_task"],
                    Challenge = new() { SignalKey = "has_acceptance_criteria", Target = 0.6, HigherIsBetter = true,
                        Statement = "Lancez trois tâches d'affilée avec, dans le premier message, de quoi savoir que c'est fini.",
                        Why = "Vos prompts initiaux ne disent presque jamais à quoi ressemble le résultat attendu : la reprise arrive au deuxième message.",
                        Verification = "has_acceptance_criteria sur les tâches de la semaine" } },

            new() { Id = "fenetre_subie", Level = 2, Title = "La fenêtre est subie, pas pilotée",
                    Signals = ["context_pressure", "cache_read_ratio", "compaction_mode", "context_isolation"],
                    Challenge = new() { SignalKey = "compaction_mode", Target = 0.7, HigherIsBetter = true, NeedsHooks = true,
                        Statement = "Compactez deux fois avant la saturation, en disant ce qu'il faut garder.",
                        Why = "Vos compactions sont subies plutôt que choisies : elles arrivent quand la fenêtre est déjà pleine.",
                        Verification = "PreCompact déclenché manuellement" } },

            new() { Id = "chargement_large", Level = 2, Title = "On charge large là où on pourrait viser",
                    Signals = ["load_precision"],
                    Challenge = new() { SignalKey = "load_precision", Target = 0.4, HigherIsBetter = true,
                        Statement = "Avant chaque lecture de fichier entier, lancez d'abord une recherche ciblée.",
                        Why = "Beaucoup de fichiers sont lus en entier là où une recherche aurait suffi : la fenêtre se remplit de lignes que personne ne relira.",
                        Verification = "load_precision sur les tâches de la semaine" } },

            new() { Id = "agent_non_equipe", Level = 3, Title = "L'agent est corrigé au lieu d'être équipé",
                    Signals = ["harness_breadth", "tool_failure_rate", "skill_usage_rate", "mcp_utilization"],
                    Challenge = new() { SignalKey = "harness_breadth", Target = 4, HigherIsBetter = true,
                        Statement = "Prenez la chose que vous réexpliquez le plus souvent, et faites-en une skill cette semaine.",
                        Why = "Votre outillage reste étroit : les mêmes explications repartent à chaque session au lieu d'être outillées une fois pour toutes.",
                        Verification = "harness_breadth sur les tâches de la semaine" } },

            new() { Id = "rien_ne_prouve", Level = 3, Title = "Rien ne prouve que ça marche",
                    Signals = ["verification_present"],
                    Challenge = new() { SignalKey = "verification_present", Target = 0.7, HigherIsBetter = true,
                        Statement = "Terminez chaque tâche de la semaine par une vérification automatique, pas par une relecture.",
                        Why = "La plupart de vos tâches se closent sans qu'un test, un build ou un lint n'ait tourné.",
                        Verification = "verification_present sur les tâches d'édition" } },

            new() { Id = "boucle_incomplete", Level = 4, Title = "La boucle ne va pas au bout toute seule",
                    Signals = ["autonomy_ratio", "loop_closure", "parallelism_index", "plan_structure"],
                    Challenge = new() { SignalKey = "loop_closure", Target = 0.6, HigherIsBetter = true,
                        Statement = "Fermez trois boucles sur un test vert plutôt que sur votre propre « stop ».",
                        Why = "Vos boucles s'arrêtent quand vous reprenez la main, pas quand quelque chose a confirmé que ça marche.",
                        Verification = "loop_closure sur les tâches closes" } },

            new() { Id = "echec_sans_lecon", Level = 5, Title = "Un échec ne change pas la stratégie",
                    Signals = ["self_correction", "graph_depth"],
                    Challenge = new() { SignalKey = "self_correction", Target = 0.5, HigherIsBetter = true,
                        Statement = "Après un échec, changez de stratégie : aucun nœud rejoué à l'identique cette semaine.",
                        Why = "Certains outils sont relancés avec exactement les mêmes paramètres après avoir échoué.",
                        Verification = "self_correction sur les rejeux" } },
        ],
        Signals =
        [
            new() { Key = "has_acceptance_criteria", Level = 1, Target = 0.6, HigherIsBetter = true,
                Complaint = "vos prompts initiaux disent rarement à quoi ressemble le résultat attendu",
                Praise = "vos prompts disent maintenant à quoi ressemble le résultat attendu",
                Advice = "Terminez chaque prompt par une phrase qui commence par « c'est fini quand ». Si vous n'arrivez pas à la finir, c'est que la tâche n'est pas encore définie — et c'est ça qu'il faut régler avant d'envoyer." },

            new() { Key = "rework_ratio", Level = 1, Target = 0.30, HigherIsBetter = false,
                Complaint = "il faut souvent reprendre après le premier message",
                Praise = "vous reprenez nettement moins après le premier message",
                Advice = "Avant d'envoyer, relisez votre prompt en vous demandant ce qu'un collègue qui arrive sur le projet vous demanderait. Ces questions-là sont exactement celles qui reviendront au deuxième message." },

            new() { Key = "first_try_success", Level = 1, Target = 0.50, HigherIsBetter = true,
                Complaint = "peu de tâches aboutissent sans relance",
                Praise = "davantage de tâches aboutissent sans relance",
                Advice = "Découpez. Deux demandes précises aboutissent plus souvent qu'une demande large, et une reprise sur une petite tâche coûte moins cher qu'une reprise sur une grande." },

            new() { Key = "prompts_per_task", Level = 1, IsRatio = false, Complaint = "nombre de prompts par tâche" },

            new() { Key = "context_pressure", Level = 2, Target = 0.80, HigherIsBetter = false,
                Complaint = "vous travaillez près du plafond de la fenêtre",
                Praise = "vous laissez plus de marge dans la fenêtre",
                Advice = "Compactez vers 60 % d'occupation plutôt qu'en butée, et dites ce qu'il faut garder : les décisions prises et les contraintes, pas le détail des fichiers déjà lus." },

            new() { Key = "cache_read_ratio", Level = 2, Target = 0.50, HigherIsBetter = true,
                Complaint = "le cache joue peu : le même contexte est relu et repayé",
                Praise = "le cache travaille pour vous",
                Advice = "Gardez le début de session stable — même dossier de travail, mêmes fichiers de contexte, mêmes instructions. Chaque changement en tête de conversation fait repayer tout ce qui suit." },

            new() { Key = "load_precision", Level = 2, Target = 0.40, HigherIsBetter = true,
                Complaint = "beaucoup de fichiers lus en entier là où une recherche ciblée suffirait",
                Praise = "vos lectures sont plus ciblées",
                Advice = "Cherchez avant de lire. Un Grep sur le nom du symbole coûte cent fois moins qu'un fichier entier, et il vous dit quelle portion mérite d'être ouverte." },

            new() { Key = "context_isolation", Level = 2, IsRatio = false, Complaint = "tours délégués à un sous-agent" },

            new() { Key = "compaction_mode", Level = 2, Source = "hooks",
                Complaint = "les compactions sont subies plutôt que choisies",
                Praise = "vous compactez avant d'y être forcé",
                Advice = "Compactez vous-même quand vous changez de sujet, en disant ce qu'il faut garder. Une compaction subie garde ce qu'elle peut ; une compaction choisie garde ce qui compte." },

            new() { Key = "harness_breadth", Level = 3, Target = 4, HigherIsBetter = true, IsRatio = false,
                Complaint = "l'outillage reste étroit",
                Praise = "votre outillage s'est élargi",
                Advice = "Prenez une chose que vous réexpliquez souvent et faites-en une skill. C'est le geste qui transforme une habitude en outil, et il ne se fait qu'une fois." },

            new() { Key = "tool_failure_rate", Level = 3, Target = 0.15, HigherIsBetter = false,
                Complaint = "les outils échouent souvent, ce qui trahit un harnais mal réglé",
                Praise = "vos outils échouent moins",
                Advice = "Quand un outil échoue deux fois de suite, le problème est dans son cadrage, pas dans le modèle : donnez le chemin exact, la commande complète, le répertoire de travail." },

            new() { Key = "verification_present", Level = 3, Target = 0.70, HigherIsBetter = true,
                Complaint = "la plupart des tâches se closent sans qu'un test ait tourné",
                Praise = "vos tâches se terminent plus souvent sur une vérification",
                Advice = "Finissez par la commande qui prouve. Même imparfaite, une commande qui tourne bat une relecture attentive — et elle se rejoue à chaque fois." },

            new() { Key = "skill_usage_rate", Level = 3, Complaint = "part des appels qui passent par une skill" },
            new() { Key = "mcp_utilization", Level = 3, Complaint = "part des appels qui vont vers un serveur MCP" },

            new() { Key = "autonomy_ratio", Level = 4, Target = 8, HigherIsBetter = true, IsRatio = false,
                Complaint = "vous reprenez la main très vite",
                Praise = "vous laissez la boucle aller plus loin",
                Advice = "Donnez la boucle entière au lieu d'un ordre par étape : « fais X, lance les tests, corrige jusqu'à ce qu'ils passent ». Vous reprendrez la main au résultat, pas à chaque pas." },

            new() { Key = "parallelism_index", Level = 4, Target = 0.15, HigherIsBetter = true,
                Complaint = "les outils indépendants partent un par un",
                Praise = "vous lancez ensemble ce qui est indépendant",
                Advice = "Demandez explicitement les lectures indépendantes en une fois : « lis A, B et C, puis dis-moi ». L'agent parallélise s'il sait que rien ne dépend de rien." },

            new() { Key = "loop_closure", Level = 4, Target = 0.60, HigherIsBetter = true,
                Complaint = "les boucles s'arrêtent sur votre « stop » plutôt que sur un test vert",
                Praise = "vos boucles se ferment plus souvent sur une vérification",
                Advice = "Ne dites pas quand vous arrêter, dites à quoi on reconnaît que c'est fini. La différence tient en une phrase et change qui décide de la fin." },

            new() { Key = "plan_structure", Level = 4, Complaint = "part des tâches décomposées explicitement" },
            new() { Key = "graph_depth", Level = 5, IsRatio = false, Complaint = "le travail reste porté par un seul agent" },

            new() { Key = "self_correction", Level = 5, Target = 0.50, HigherIsBetter = true,
                Complaint = "des outils sont rejoués à l'identique après un échec",
                Praise = "vous changez de stratégie après un échec",
                Advice = "Si la même approche échoue deux fois, nommez le changement d'angle : « autre méthode : … ». Relancer à l'identique n'est pas se corriger, c'est espérer." },
        ],
        Moments =
        [
            new() { Key = "rework", Text = "Hier, « {tache} » a demandé {reprises} reprises. Un critère d'acceptation dans le premier message en aurait probablement évité deux." },
            new() { Key = "abandon", Text = "« {tache} » s'est arrêtée sans aboutir. Reprendre par ce qui a bloqué vaut mieux que repartir de zéro." },
            new() { Key = "tool_failures", Text = "Sur « {tache} », {preuve}. Souvent le signe d'un harnais mal réglé plutôt que d'un modèle distrait." },
            new() { Key = "no_verification", Text = "« {tache} » s'est terminée sans qu'aucun test n'ait tourné. C'est le point de bascule entre laisser l'agent travailler et devoir tout relire." },
            new() { Key = "compaction_subie", Source = "hooks", Text = "La compaction est arrivée pendant que vous travailliez, pas quand vous l'avez décidée. Ce qu'elle a gardé, ce n'est pas vous qui l'avez choisi." },
        ],
    };
}
