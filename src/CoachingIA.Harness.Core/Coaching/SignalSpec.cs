namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Ce qu'on attend d'un signal, et comment le dire. C'est le seul endroit où
/// vivent les cibles et le sens de lecture — un signal dont on ignore la
/// direction ne peut ni déclencher une observation, ni compter comme un progrès.
/// </summary>
public sealed record SignalSpec(
    string Key, int Level, bool HigherIsBetter, double Target,
    string Complaint, string Praise, string Advice, bool IsRatio = true)
{
    /// <summary>
    /// L'écart à la cible, normalisé, positif quand le signal est en défaut.
    /// Sert à classer les observations : on parle d'abord de ce qui manque le plus.
    /// </summary>
    public double Gap(double value)
    {
        if (double.IsNaN(value)) return double.NaN;
        var scale = IsRatio ? 1.0 : Math.Max(1.0, Target);
        var gap = HigherIsBetter ? Target - value : value - Target;
        return gap / scale;
    }

    public bool Meets(double value) => !double.IsNaN(value) && Gap(value) <= 0;
}

public static class SignalSpecs
{
    public static readonly IReadOnlyList<SignalSpec> All =
    [
        new("has_acceptance_criteria", 1, true, 0.6,
            "vos prompts initiaux disent rarement à quoi ressemble le résultat attendu",
            "vos prompts disent maintenant à quoi ressemble le résultat attendu",
            "Terminez chaque prompt par une phrase qui commence par « c'est fini quand ». Si vous n'arrivez pas à la finir, c'est que la tâche n'est pas encore définie — et c'est ça qu'il faut régler avant d'envoyer."),

        new("rework_ratio", 1, false, 0.30,
            "il faut souvent reprendre après le premier message",
            "vous reprenez nettement moins après le premier message",
            "Avant d'envoyer, relisez votre prompt en vous demandant ce qu'un collègue qui arrive sur le projet vous demanderait. Ces questions-là sont exactement celles qui reviendront au deuxième message."),

        new("first_try_success", 1, true, 0.50,
            "peu de tâches aboutissent sans relance",
            "davantage de tâches aboutissent sans relance",
            "Découpez. Deux demandes précises aboutissent plus souvent qu'une demande large, et une reprise sur une petite tâche coûte moins cher qu'une reprise sur une grande."),

        new("context_pressure", 2, false, 0.80,
            "vous travaillez près du plafond de la fenêtre",
            "vous laissez plus de marge dans la fenêtre",
            "Compactez vers 60 % d'occupation plutôt qu'en butée, et dites ce qu'il faut garder : les décisions prises et les contraintes, pas le détail des fichiers déjà lus."),

        new("cache_read_ratio", 2, true, 0.50,
            "le cache joue peu : le même contexte est relu et repayé",
            "le cache travaille pour vous",
            "Gardez le début de session stable — même dossier de travail, mêmes fichiers de contexte, mêmes instructions. Chaque changement en tête de conversation fait repayer tout ce qui suit."),

        new("load_precision", 2, true, 0.40,
            "beaucoup de fichiers lus en entier là où une recherche ciblée suffirait",
            "vos lectures sont plus ciblées",
            "Cherchez avant de lire. Un Grep sur le nom du symbole coûte cent fois moins qu'un fichier entier, et il vous dit quelle portion mérite d'être ouverte."),

        new("harness_breadth", 3, true, 4, "l'outillage reste étroit",
            "votre outillage s'est élargi",
            "Prenez une chose que vous réexpliquez souvent et faites-en une skill. C'est le geste qui transforme une habitude en outil, et il ne se fait qu'une fois.", IsRatio: false),

        new("tool_failure_rate", 3, false, 0.15,
            "les outils échouent souvent, ce qui trahit un harnais mal réglé",
            "vos outils échouent moins",
            "Quand un outil échoue deux fois de suite, le problème est dans son cadrage, pas dans le modèle : donnez le chemin exact, la commande complète, le répertoire de travail."),

        new("verification_present", 3, true, 0.70,
            "la plupart des tâches se closent sans qu'un test ait tourné",
            "vos tâches se terminent plus souvent sur une vérification",
            "Finissez par la commande qui prouve. Même imparfaite, une commande qui tourne bat une relecture attentive — et elle se rejoue à chaque fois."),

        new("autonomy_ratio", 4, true, 8, "vous reprenez la main très vite",
            "vous laissez la boucle aller plus loin",
            "Donnez la boucle entière au lieu d'un ordre par étape : « fais X, lance les tests, corrige jusqu'à ce qu'ils passent ». Vous reprendrez la main au résultat, pas à chaque pas.", IsRatio: false),

        new("parallelism_index", 4, true, 0.15,
            "les outils indépendants partent un par un",
            "vous lancez ensemble ce qui est indépendant",
            "Demandez explicitement les lectures indépendantes en une fois : « lis A, B et C, puis dis-moi ». L'agent parallélise s'il sait que rien ne dépend de rien."),

        new("loop_closure", 4, true, 0.60,
            "les boucles s'arrêtent sur votre « stop » plutôt que sur un test vert",
            "vos boucles se ferment plus souvent sur une vérification",
            "Ne dites pas quand vous arrêter, dites à quoi on reconnaît que c'est fini. La différence tient en une phrase et change qui décide de la fin."),

        new("self_correction", 5, true, 0.50,
            "des outils sont rejoués à l'identique après un échec",
            "vous changez de stratégie après un échec",
            "Si la même approche échoue deux fois, nommez le changement d'angle : « autre méthode : … ». Relancer à l'identique n'est pas se corriger, c'est espérer."),
    ];

    private static readonly Dictionary<string, SignalSpec> ByKey =
        All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    public static SignalSpec? Find(string key) => ByKey.GetValueOrDefault(key);
}
