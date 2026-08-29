using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>Une observation : un constat, une preuve, une tâche à citer.</summary>
public sealed record Observation(
    int Level, string SignalKey, double Value, string Statement,
    string Evidence, string TaskTitle, DateOnly TaskDate, string Advice,
    string? Flourish = null, string? LevelTerm = null,
    string? ProblemId = null, string? ProblemTitle = null);

/// <summary>Ce qui a progressé depuis les semaines précédentes.</summary>
public sealed record Progress(string SignalKey, int Level, double Before, double After, string Statement);

/// <summary>
/// Le bilan du lundi. Trois observations, une réussite, un défi — et rien de plus.
///
/// La règle qui donne sa valeur au format : <strong>aucune observation sans
/// exemple</strong>. Une moyenne ne fait changer personne&nbsp;; « mardi, sur la
/// tâche X » si. C'est pourquoi chaque observation transporte le titre et la date
/// d'une tâche réelle, et la phrase exacte qui explique le chiffre.
/// </summary>
public sealed class WeeklyReview
{
    public required string Week { get; init; }
    public required DateOnly MondayOf { get; init; }
    public int Tasks { get; init; }
    public int Sessions { get; init; }
    public int ActiveDays { get; init; }
    public TimeSpan ActiveTime { get; init; }

    public List<Observation> Observations { get; } = [];
    public Progress? Win { get; set; }

    /// <summary>Le prompt de la semaine : ce qui a été écrit, et ce qu'il aurait fallu écrire.</summary>
    public PromptCritique? PromptOfTheWeek { get; set; }
    public PromptContext? PromptContext { get; set; }
    public Challenge? Challenge { get; set; }
    public Challenge? PreviousChallenge { get; set; }
    public string? PreviousVerdict { get; set; }
    public WeekUsage? Usage { get; set; }
    public List<UsageAlert> Alerts { get; } = [];

    public bool IsEmpty => Tasks == 0;
}

/// <summary>
/// Assemble le bilan à partir des traces. Aucun juge LLM ici : tout ce qui suit
/// se déduit de compteurs et de la grille de <see cref="SignalSpecs"/>. Le juge
/// viendra plus tard, là où l'heuristique se trompe de façon mesurée — pas avant.
/// </summary>
public sealed class WeeklyReviewBuilder
{
    private readonly TaskSegmenter _segmenter;
    private readonly SignalExtractor _extractor;
    private readonly UsageAnalyzer _usage;
    private readonly ChallengeLibrary _challenges = new();

    /// <summary>Nombre d'observations retenues. Trois : au-delà, plus personne ne retient rien.</summary>
    public int MaxObservations { get; init; } = 3;

    /// <summary>Écart minimal pour parler d'un progrès plutôt que de bruit.</summary>
    public double ProgressFloor { get; init; } = 0.12;

    /// <summary>Qui critique le prompt de la semaine. Null = section omise.</summary>
    public IPromptCritic? Critic { get; init; } = new HeuristicPromptCritic();

    public WeeklyReviewBuilder(SignalExtractor? extractor = null, TaskSegmenter? segmenter = null, UsageAnalyzer? usage = null)
    {
        _extractor = extractor ?? new SignalExtractor();
        _segmenter = segmenter ?? new TaskSegmenter();
        _usage = usage ?? new UsageAnalyzer();
    }

    private sealed record TaskSignals(SegmentedTask Task, Dictionary<string, Signal> Signals);

    public WeeklyReview Build(IEnumerable<TranscriptSession> sessions, string? week, LensWriter writer, bool hooksAvailable = false)
    {
        var perWeek = new Dictionary<string, List<TaskSignals>>(StringComparer.Ordinal);
        var sessionsByWeek = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var session in sessions)
            foreach (var task in _segmenter.Segment(session))
            {
                if (task.Turns.Count == 0) continue;
                var key = UsageAnalyzer.WeekKey(task.StartedAt);
                if (!perWeek.TryGetValue(key, out var list)) perWeek[key] = list = [];
                list.Add(new TaskSignals(task, _extractor.ForTask(task, session)
                    .GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)));
                if (!sessionsByWeek.TryGetValue(key, out var set)) sessionsByWeek[key] = set = new(StringComparer.Ordinal);
                set.Add(session.SessionId);
            }

        // Par défaut on bilan la dernière semaine close : celle en cours est
        // incomplète, et la juger reviendrait à noter une partie non terminée.
        var target = week ?? LastClosedWeek(perWeek.Keys);
        if (target is null || !perWeek.TryGetValue(target, out var tasks) || tasks.Count == 0)
            return new WeeklyReview { Week = target ?? "—", MondayOf = default };

        // Les images tournent d'une semaine à l'autre : on cale le vocabulaire
        // sur la semaine jugée, pas sur le jour où la commande est lancée. Un
        // bilan régénéré en octobre doit raconter ce qu'il racontait en août.
        writer = writer.ForWeek(target);

        var weeks = _usage.ByWeek(sessions, _segmenter);
        var usage = weeks.FirstOrDefault(w => w.Week == target);

        var review = new WeeklyReview
        {
            Week = target,
            MondayOf = usage?.MondayOf ?? DateOnly.FromDateTime(tasks[0].Task.StartedAt.UtcDateTime),
            Tasks = tasks.Count,
            Sessions = sessionsByWeek[target].Count,
            ActiveDays = usage?.ActiveDays.Count ?? 0,
            ActiveTime = tasks.Aggregate(TimeSpan.Zero, (a, t) => a + t.Task.ActiveDuration),
            Usage = usage,
        };
        review.Alerts.AddRange(_usage.Alerts(weeks.Where(w => string.CompareOrdinal(w.Week, target) <= 0).ToList()));

        var current = Averages(tasks);
        AddObservations(review, tasks, current, writer);

        var earlier = perWeek.Where(kv => string.CompareOrdinal(kv.Key, target) < 0)
            .OrderByDescending(kv => kv.Key).Take(3).SelectMany(kv => kv.Value).ToList();
        review.Win = FindWin(current, earlier.Count > 0 ? Averages(earlier) : null);

        review.Challenge = _challenges.Pick(current, writer, hooksAvailable);

        // Le prompt de la semaine : celui qui a coûté le plus cher, réécrit.
        // C'est la partie la plus utile du bilan, et la plus difficile à
        // produire sans juge — d'où la critique par défaut hors ligne.
        if (Critic is not null &&
            PromptPicker.Pick(tasks.Select(t => (t.Task, (IReadOnlyList<Signal>)t.Signals.Values.ToList()))) is { } pick)
        {
            review.PromptContext = pick.Context;
            review.PromptOfTheWeek = Critic.Critique(pick.Task.Turns[0].Prompt, pick.Context);
        }
        return review;
    }

    private static string? LastClosedWeek(IEnumerable<string> keys)
    {
        var thisWeek = UsageAnalyzer.WeekKey(DateTimeOffset.UtcNow);
        return keys.Where(k => string.CompareOrdinal(k, thisWeek) < 0).OrderBy(k => k, StringComparer.Ordinal).LastOrDefault();
    }

    private static Dictionary<string, double> Averages(List<TaskSignals> tasks)
    {
        var buckets = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var t in tasks)
            foreach (var (key, signal) in t.Signals)
            {
                if (double.IsNaN(signal.Value)) continue;
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add(signal.Value);
            }
        return buckets.ToDictionary(kv => kv.Key, kv => kv.Value.Average(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Choisit les observations : les plus gros écarts d'abord, mais jamais deux
    /// fois la même problématique. Trois variations d'un même reproche donnent
    /// l'impression d'un coach qui n'a qu'une idée.
    ///
    /// La déduplication portait autrefois sur le palier, ce qui était une
    /// approximation : « la fenêtre est subie » et « on charge large » sont deux
    /// reproches distincts qui vivent tous deux au palier 2, et n'en retenir
    /// qu'un taisait une moitié du problème. La problématique dit exactement ce
    /// que le palier essayait d'approcher.
    /// </summary>
    private void AddObservations(WeeklyReview review, List<TaskSignals> tasks, Dictionary<string, double> averages, LensWriter writer)
    {
        var corpus = SignalSpecs.Corpus;
        var candidates = new List<(double Gap, SignalSpec Spec, double Value)>();
        foreach (var spec in SignalSpecs.All)
        {
            if (!averages.TryGetValue(spec.Key, out var value)) continue;
            var gap = spec.Gap(value);
            if (double.IsNaN(gap) || gap <= 0) continue;
            candidates.Add((gap, spec, value));
        }

        var usedProblems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, spec, value) in candidates.OrderByDescending(c => c.Gap))
        {
            if (review.Observations.Count >= MaxObservations) break;

            // Un signal sans problématique déclarée retombe sur son palier : le
            // bilan reste lisible même si le corpus est en retard sur la mesure.
            var problemId = corpus.ProblemOf(spec.Key);
            if (!usedProblems.Add(problemId ?? "palier:" + spec.Level)) continue;

            // L'exemple est la tâche la plus représentative du défaut : celle où
            // le signal est au plus mal, pas une prise au hasard.
            var worst = tasks
                .Where(t => t.Signals.TryGetValue(spec.Key, out var s) && !double.IsNaN(s.Value))
                .OrderByDescending(t => spec.Gap(t.Signals[spec.Key].Value))
                .FirstOrDefault();
            if (worst is null) continue;

            // La lentille habille le constat sans jamais le remplacer : le
            // titre reste le fait mesuré, l'image vient dessous, et le palier
            // prend le nom qu'il porte dans l'univers choisi.
            review.Observations.Add(new Observation(
                spec.Level, spec.Key, value, spec.Complaint,
                worst.Signals[spec.Key].Evidence,
                worst.Task.Title,
                DateOnly.FromDateTime(worst.Task.StartedAt.UtcDateTime),
                spec.Advice,
                writer.ForProblem(problemId, spec.Key, spec.Complaint).Flourish,
                writer.TermFor(spec.Level, ""),
                problemId,
                problemId is null ? null : corpus.Problem(problemId)?.Title));
        }
    }

    /// <summary>
    /// La réussite. Elle ouvre le bilan, et elle doit être vraie : on ne
    /// félicite ni le bruit, ni un signal qui reste sous sa cible. Faute de quoi
    /// on ne félicite pas — un compliment de politesse dévalue les autres.
    /// </summary>
    private Progress? FindWin(Dictionary<string, double> current, Dictionary<string, double>? previous)
    {
        if (previous is null) return null;
        Progress? best = null;
        var bestDelta = 0.0;

        foreach (var spec in SignalSpecs.All)
        {
            if (!current.TryGetValue(spec.Key, out var now)) continue;
            if (!previous.TryGetValue(spec.Key, out var before)) continue;

            var scale = spec.IsRatio ? 1.0 : Math.Max(1.0, spec.Target);
            var delta = (spec.HigherIsBetter ? now - before : before - now) / scale;
            if (delta < ProgressFloor) continue;
            if (!spec.Meets(now)) continue;      // progresser sans atteindre la cible n'est pas encore une réussite
            if (delta <= bestDelta) continue;

            bestDelta = delta;
            best = new Progress(spec.Key, spec.Level, before, now, spec.Praise);
        }
        return best;
    }
}
