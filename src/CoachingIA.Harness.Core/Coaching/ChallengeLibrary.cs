using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

public sealed record Challenge(
    int Level, string SignalKey, string Statement, string? Flourish,
    string Why, string Verification)
{
    /// <summary>Le fait d'abord, l'image ensuite — jamais l'inverse.</summary>
    public string Render() => new LensedMessage(Statement, Flourish).ToString();
}

/// <summary>
/// Le catalogue des défis hebdomadaires. Un défi n'est pas un conseil : c'est un
/// geste précis, tenu sur une semaine, dont la réussite se lit dans les traces.
/// Sans cette dernière propriété, il n'y aurait aucun moyen de dire au bilan
/// suivant si l'exercice a porté — et un exercice qu'on ne peut pas corriger
/// n'est pas un exercice.
/// </summary>
public sealed class ChallengeLibrary
{
    private sealed record Entry(
        int Level, string SignalKey, string Statement, string Why,
        string Verification, bool HigherIsBetter, double Target, bool NeedsHooks);

    private static readonly Entry[] Catalogue =
    [
        new(1, "has_acceptance_criteria",
            "Lancez trois tâches d'affilée avec, dans le premier message, de quoi savoir que c'est fini.",
            "Vos prompts initiaux ne disent presque jamais à quoi ressemble le résultat attendu : la reprise arrive au deuxième message.",
            "has_acceptance_criteria sur les tâches de la semaine", true, 0.6, false),

        new(2, "compaction_mode",
            "Compactez deux fois avant la saturation, en disant ce qu'il faut garder.",
            "Vos compactions sont subies plutôt que choisies : elles arrivent quand la fenêtre est déjà pleine.",
            "PreCompact déclenché manuellement", true, 0.7, true),

        new(3, "verification_present",
            "Terminez chaque tâche de la semaine par une vérification automatique, pas par une relecture.",
            "La plupart de vos tâches se closent sans qu'un test, un build ou un lint n'ait tourné.",
            "verification_present sur les tâches d'édition", true, 0.7, false),

        new(4, "loop_closure",
            "Fermez trois boucles sur un test vert plutôt que sur votre propre « stop ».",
            "Vos boucles s'arrêtent quand vous reprenez la main, pas quand quelque chose a confirmé que ça marche.",
            "loop_closure sur les tâches closes", true, 0.6, false),

        new(5, "self_correction",
            "Après un échec, changez de stratégie : aucun nœud rejoué à l'identique cette semaine.",
            "Certains outils sont relancés avec exactement les mêmes paramètres après avoir échoué.",
            "self_correction sur les rejeux", true, 0.5, false),
    ];

    /// <summary>
    /// Choisit un défi, et un seul. La règle de sélection suit la cumulativité
    /// des paliers : on vise le plus bas palier dont le signal est encore sous
    /// sa cible, parce qu'un exercice de palier 5 ne sert à rien tant que le
    /// palier 1 fuit.
    /// </summary>
    public Challenge? Pick(IReadOnlyDictionary<string, double> signalAverages, LensWriter writer, bool hooksAvailable = false)
    {
        foreach (var entry in Catalogue.OrderBy(e => e.Level))
        {
            if (entry.NeedsHooks && !hooksAvailable) continue;
            if (!signalAverages.TryGetValue(entry.SignalKey, out var value)) continue;
            if (double.IsNaN(value)) continue;

            var below = entry.HigherIsBetter ? value < entry.Target : value > entry.Target;
            if (!below) continue;

            var lensed = writer.ForChallenge(entry.SignalKey, entry.Statement);
            return new Challenge(entry.Level, entry.SignalKey, entry.Statement, lensed.Flourish,
                entry.Why, entry.Verification);
        }
        return null;
    }

    /// <summary>Moyenne des signaux sur un lot de tâches, indéterminés exclus.</summary>
    public static Dictionary<string, double> Average(
        IEnumerable<SegmentedTask> tasks, TranscriptSession session, SignalExtractor extractor)
    {
        var buckets = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var task in tasks)
            foreach (var signal in extractor.ForTask(task, session))
            {
                if (double.IsNaN(signal.Value)) continue;
                if (!buckets.TryGetValue(signal.Key, out var list)) buckets[signal.Key] = list = [];
                list.Add(signal.Value);
            }
        return buckets.ToDictionary(kv => kv.Key, kv => kv.Value.Average(), StringComparer.Ordinal);
    }
}

/// <summary>
/// Le moment opportun : une observation d'une ligne, préparée à la fin d'une
/// session notable, à glisser au début de la suivante.
///
/// La contrainte est la retenue. Une seule observation, jamais pendant le
/// travail, et seulement si la session sort vraiment de l'ordinaire — sinon le
/// canal devient du bruit et se fait couper, ce qui emporte aussi les fois où
/// il aurait été utile.
/// </summary>
public sealed class MomentDetector
{
    public int ReworkThreshold { get; init; } = 3;
    public double ToolFailureThreshold { get; init; } = 0.25;

    public LensedMessage? Detect(SegmentedTask task, IReadOnlyList<Signal> signals, LensWriter writer)
    {
        double Value(string key) => signals.FirstOrDefault(s => s.Key == key)?.Value ?? double.NaN;
        string Why(string key) => signals.FirstOrDefault(s => s.Key == key)?.Evidence ?? "";

        var title = Shorten(task.Title);

        if (task.ReworkTurns >= ReworkThreshold)
            return writer.ForMoment("rework",
                $"Hier, « {title} » a demandé {task.ReworkTurns} reprises. " +
                "Un critère d'acceptation dans le premier message en aurait probablement évité deux.");

        if (!task.Completed && !task.InProgress)
            return writer.ForMoment("abandon",
                $"« {title} » s'est arrêtée sans aboutir. Reprendre par ce qui a bloqué vaut mieux que repartir de zéro.");

        var failures = Value("tool_failure_rate");
        if (!double.IsNaN(failures) && failures > ToolFailureThreshold && task.ToolCalls >= 8)
            return writer.ForMoment("tool_failures",
                $"Sur « {title} », {Why("tool_failure_rate")}. Souvent le signe d'un harnais mal réglé plutôt que d'un modèle distrait.");

        if (Value("verification_present") == 0 && Value("loop_closure") == 0 && task.ToolCalls >= 10)
            return writer.ForMoment("no_verification",
                $"« {title} » s'est terminée sans qu'aucun test n'ait tourné. " +
                "C'est le point de bascule entre laisser l'agent travailler et devoir tout relire.");

        return null;
    }

    private static string Shorten(string title)
        => title.Length <= 48 ? title : title[..47] + "…";
}
