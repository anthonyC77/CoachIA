using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

public sealed record Challenge(
    int Level, string SignalKey, string Statement, string? Flourish,
    string Why, string Verification, string? ProblemId = null)
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
    /// <summary>
    /// Choisit un défi, et un seul. La règle de sélection suit la cumulativité
    /// des paliers : on vise le plus bas palier dont le signal est encore sous
    /// sa cible, parce qu'un exercice de palier 5 ne sert à rien tant que le
    /// palier 1 fuit.
    /// </summary>
    public Challenge? Pick(IReadOnlyDictionary<string, double> signalAverages, LensWriter writer, bool hooksAvailable = false)
    {
        foreach (var problem in SignalSpecs.Corpus.Problems.OrderBy(p => p.Level))
        {
            if (problem.Challenge is not { } defi) continue;
            if (defi.NeedsHooks && !hooksAvailable) continue;
            if (!signalAverages.TryGetValue(defi.SignalKey, out var value)) continue;
            if (double.IsNaN(value)) continue;

            // Le défi porte sa propre cible quand il en déclare une : un exercice
            // d'une semaine ne se juge pas au seuil d'une tendance de fond, et
            // certains défis visent un signal que les transcripts ne mesurent pas.
            var spec = SignalSpecs.Find(defi.SignalKey);
            var target = defi.Target ?? spec?.Target;
            if (target is null) continue;
            var higherIsBetter = defi.HigherIsBetter ?? spec?.HigherIsBetter ?? true;

            var below = higherIsBetter ? value < target : value > target;
            if (!below) continue;

            var lensed = writer.ForChallenge(problem.Id, defi.SignalKey, defi.Statement);
            return new Challenge(problem.Level, defi.SignalKey, defi.Statement, lensed.Flourish,
                defi.Why, defi.Verification, problem.Id);
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
            return Say(writer, "rework", ("tache", title), ("reprises", task.ReworkTurns.ToString()));

        if (!task.Completed && !task.InProgress)
            return Say(writer, "abandon", ("tache", title));

        var failures = Value("tool_failure_rate");
        if (!double.IsNaN(failures) && failures > ToolFailureThreshold && task.ToolCalls >= 8)
            return Say(writer, "tool_failures", ("tache", title), ("preuve", Why("tool_failure_rate")));

        if (Value("verification_present") == 0 && Value("loop_closure") == 0 && task.ToolCalls >= 10)
            return Say(writer, "no_verification", ("tache", title));

        return null;
    }

    /// <summary>
    /// Le texte vient du corpus, les marques sont remplies ici. Un moment absent
    /// du corpus rend simplement le silence : mieux vaut ne rien dire qu'afficher
    /// une phrase à trous.
    /// </summary>
    private static LensedMessage? Say(LensWriter writer, string key, params (string Name, string Value)[] values)
    {
        var text = SignalSpecs.Corpus.MomentText(key, values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));
        return text is null ? null : writer.ForMoment(key, text);
    }

    private static string Shorten(string title)
        => title.Length <= 48 ? title : title[..47] + "…";
}
