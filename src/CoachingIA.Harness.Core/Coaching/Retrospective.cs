using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>Ce qu'une trajectoire raconte, une fois les semaines mises bout à bout.</summary>
public enum TrendVerdict
{
    /// <summary>Sous la cible au départ, au-dessus depuis assez longtemps pour que ce ne soit plus un hasard.</summary>
    Acquis,
    EnProgres,
    Stable,
    EnRecul,
    /// <summary>Trop peu de semaines mesurées pour dire quoi que ce soit d'honnête.</summary>
    TropPeuDeDonnees,
}

/// <summary>Un point de mesure : une semaine où le signal a pu être calculé.</summary>
public sealed record TrailPoint(string Week, DateOnly MondayOf, double Value, int Tasks, bool MeetsTarget);

/// <summary>
/// La trajectoire d'un signal sur toute la période. Les points ne couvrent que
/// les semaines <em>mesurées</em> : une semaine sans travail n'est pas un zéro,
/// c'est un trou, et le confondre avec une régression serait le pire reproche
/// qu'un coach puisse faire.
/// </summary>
public sealed record SignalTrail(
    string Key, int Level, IReadOnlyList<TrailPoint> Points,
    double Early, double Late, double Delta,
    string? CrossedAt, DateOnly? CrossedOn, int StreakWeeks,
    TrendVerdict Verdict, string Statement, string Advice,
    string LevelTerm, string? Flourish)
{
    public bool IsRatio => SignalSpecs.Find(Key)?.IsRatio ?? true;
    public double Target => SignalSpecs.Find(Key)?.Target ?? 0;
    public bool HigherIsBetter => SignalSpecs.Find(Key)?.HigherIsBetter ?? true;
}

/// <summary>Un mois : le volume, le mélange de modèles, et où en étaient les paliers.</summary>
public sealed record MonthBand(
    string Month, DateOnly FirstDay, int ActiveWeeks, int SilentWeeks,
    int Tasks, int Sessions, TimeSpan ActiveTime, long TokensRead,
    double Haiku, double Sonnet, double Opus,
    IReadOnlyDictionary<int, double> LevelScores,
    IReadOnlyDictionary<WorkKind, int> Work);

/// <summary>Une bascule datée : la semaine où quelque chose a changé pour de bon.</summary>
public sealed record Milestone(string Week, DateOnly On, int Level, string SignalKey, string Statement, string? Flourish);

/// <summary>
/// La rétrospective : ce que les mois écoulés disent, là où un bilan hebdomadaire
/// ne voit qu'une semaine. Elle ne remplace pas le bilan — elle répond à une
/// autre question. Le bilan demande « qu'est-ce que je corrige lundi ? » ; la
/// rétrospective demande « est-ce que je progresse vraiment, ou est-ce que je
/// tourne ? ». La seconde question ne se pose qu'avec du recul.
/// </summary>
public sealed class Retrospective
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public int WeeksCovered { get; init; }
    public int WeeksActive { get; init; }
    public int Tasks { get; init; }
    public int Sessions { get; init; }
    public TimeSpan ActiveTime { get; init; }
    public long TokensRead { get; init; }

    public List<SignalTrail> Trails { get; } = [];
    public List<MonthBand> Months { get; } = [];
    public List<Milestone> Milestones { get; } = [];

    /// <summary>Les plus longues périodes sans aucune trace, en semaines.</summary>
    public List<(DateOnly From, DateOnly To, int Weeks)> Silences { get; } = [];

    public int WeeksSilent => WeeksCovered - WeeksActive;
    public bool IsEmpty => Tasks == 0;

    public IEnumerable<SignalTrail> With(TrendVerdict verdict) => Trails.Where(t => t.Verdict == verdict);

    /// <summary>Le score d'un palier sur le dernier mois qui l'a mesuré.</summary>
    public double? LatestLevelScore(int level)
    {
        for (var i = Months.Count - 1; i >= 0; i--)
            if (Months[i].LevelScores.TryGetValue(level, out var s)) return s;
        return null;
    }
}

/// <summary>
/// Rejoue l'historique semaine par semaine. Aucun juge, aucune extrapolation :
/// on n'invente pas les semaines manquantes, on les nomme.
/// </summary>
public sealed class RetrospectiveBuilder
{
    private readonly TaskSegmenter _segmenter;
    private readonly SignalExtractor _extractor;
    private readonly UsageAnalyzer _usage;

    /// <summary>En dessous, on ne prononce pas de verdict : trois points ne font pas une tendance.</summary>
    public int MinPointsForVerdict { get; init; } = 3;

    /// <summary>Semaines consécutives au-dessus de la cible avant de parler d'acquis.</summary>
    public int StreakForAcquired { get; init; } = 3;

    /// <summary>Écart normalisé en dessous duquel on parle de stabilité, pas de mouvement.</summary>
    public double MovementFloor { get; init; } = 0.10;

    public RetrospectiveBuilder(SignalExtractor? extractor = null, TaskSegmenter? segmenter = null, UsageAnalyzer? usage = null)
    {
        _extractor = extractor ?? new SignalExtractor();
        _segmenter = segmenter ?? new TaskSegmenter();
        _usage = usage ?? new UsageAnalyzer();
    }

    public Retrospective Build(IEnumerable<TranscriptSession> sessions, LensWriter writer, DateOnly? since = null, DateOnly? until = null)
    {
        var list = sessions as IList<TranscriptSession> ?? sessions.ToList();

        // 1. Les mesures, semaine par semaine.
        var perWeek = new SortedDictionary<string, List<Dictionary<string, Signal>>>(StringComparer.Ordinal);
        var mondays = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        foreach (var session in list)
            foreach (var task in _segmenter.Segment(session))
            {
                if (task.Turns.Count == 0) continue;
                var day = DateOnly.FromDateTime(task.StartedAt.UtcDateTime);
                if (since is { } s && day < s) continue;
                if (until is { } u && day > u) continue;

                var key = UsageAnalyzer.WeekKey(task.StartedAt);
                if (!perWeek.TryGetValue(key, out var bucket)) perWeek[key] = bucket = [];
                bucket.Add(_extractor.ForTask(task, session)
                    .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
                mondays[key] = Monday(day);
            }

        if (perWeek.Count == 0)
            return new Retrospective { From = since ?? default, To = until ?? default };

        // 2. Le volume, repris de l'analyseur d'usage pour ne pas recompter deux
        //    fois la même chose de deux façons différentes.
        var weeks = _usage.ByWeek(list, _segmenter)
            .Where(w => perWeek.ContainsKey(w.Week))
            .ToDictionary(w => w.Week, StringComparer.Ordinal);

        var first = mondays[perWeek.Keys.First()];
        var last = mondays[perWeek.Keys.Last()].AddDays(6);

        // Comme pour le bilan : le vocabulaire est calé sur la fin de la période
        // rejouée, pas sur aujourd'hui. Une rétrospective regénérée plus tard
        // doit rester la même page.
        writer = writer.ForDate(last);

        var retro = new Retrospective
        {
            From = first,
            To = last,
            WeeksCovered = (int)Math.Round((last.DayNumber - first.DayNumber + 1) / 7.0),
            WeeksActive = perWeek.Count,
            Tasks = weeks.Values.Sum(w => w.Tasks),
            Sessions = weeks.Values.SelectMany(w => w.Sessions).Distinct(StringComparer.Ordinal).Count(),
            ActiveTime = weeks.Values.Aggregate(TimeSpan.Zero, (a, w) => a + w.ActiveTime),
            TokensRead = weeks.Values.Sum(w => w.TotalRead),
        };

        // 3. Une moyenne par semaine et par signal.
        var series = new Dictionary<string, List<TrailPoint>>(StringComparer.Ordinal);
        foreach (var (week, tasks) in perWeek)
        {
            var monday = mondays[week];
            foreach (var spec in SignalSpecs.All)
            {
                var values = tasks
                    .Where(t => t.TryGetValue(spec.Key, out var sig) && !double.IsNaN(sig.Value))
                    .Select(t => t[spec.Key].Value).ToList();
                if (values.Count == 0) continue;
                var mean = values.Average();
                if (!series.TryGetValue(spec.Key, out var points)) series[spec.Key] = points = [];
                points.Add(new TrailPoint(week, monday, mean, tasks.Count, spec.Meets(mean)));
            }
        }

        foreach (var spec in SignalSpecs.All)
        {
            if (!series.TryGetValue(spec.Key, out var points) || points.Count == 0) continue;
            retro.Trails.Add(BuildTrail(spec, points, writer));
        }

        // Le plus parlant d'abord : ce qui vient d'être acquis, puis ce qui recule.
        retro.Trails.Sort((a, b) =>
        {
            var rank = Rank(a.Verdict).CompareTo(Rank(b.Verdict));
            if (rank != 0) return rank;
            return Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta));
        });

        retro.Milestones.AddRange(retro.Trails
            .Where(t => t.CrossedAt is not null && t.CrossedOn is not null)
            .Select(t => new Milestone(t.CrossedAt!, t.CrossedOn!.Value, t.Level, t.Key,
                SignalSpecs.Find(t.Key)!.Praise, t.Flourish))
            .OrderBy(m => m.On));

        retro.Months.AddRange(BuildMonths(perWeek, mondays, weeks, series));
        retro.Silences.AddRange(FindSilences(mondays.Values.OrderBy(d => d).ToList()));
        return retro;
    }

    private static int Rank(TrendVerdict v) => v switch
    {
        TrendVerdict.Acquis => 0,
        TrendVerdict.EnRecul => 1,
        TrendVerdict.EnProgres => 2,
        TrendVerdict.Stable => 3,
        _ => 4,
    };

    private SignalTrail BuildTrail(SignalSpec spec, List<TrailPoint> points, LensWriter writer)
    {
        // Début et fin comparés par tiers plutôt que par points isolés : une
        // semaine chargée ou creuse ne doit pas décider à elle seule d'un verdict.
        var third = Math.Max(1, points.Count / 3);
        var early = points.Take(third).Average(p => p.Value);
        var late = points.TakeLast(third).Average(p => p.Value);

        var scale = spec.IsRatio ? 1.0 : Math.Max(1.0, spec.Target);
        var delta = (spec.HigherIsBetter ? late - early : early - late) / scale;

        // Le franchissement : la première semaine à partir de laquelle la cible
        // n'a plus jamais été perdue, à condition qu'elle ait été perdue avant.
        string? crossedAt = null;
        DateOnly? crossedOn = null;
        var streak = 0;
        for (var i = points.Count - 1; i >= 0; i--)
        {
            if (!points[i].MeetsTarget) break;
            streak++;
        }
        var firstOfStreak = points.Count - streak;
        if (streak > 0 && firstOfStreak > 0 && points.Take(firstOfStreak).Any(p => !p.MeetsTarget))
        {
            crossedAt = points[firstOfStreak].Week;
            crossedOn = points[firstOfStreak].MondayOf;
        }

        var verdict =
            points.Count < MinPointsForVerdict ? TrendVerdict.TropPeuDeDonnees
            : streak >= StreakForAcquired ? TrendVerdict.Acquis
            : delta >= MovementFloor ? TrendVerdict.EnProgres
            : delta <= -MovementFloor ? TrendVerdict.EnRecul
            : TrendVerdict.Stable;

        var statement = Say(spec, verdict, early, late, streak, points.Count);
        var lensed = writer.ForSignal(spec.Key, statement);

        return new SignalTrail(
            spec.Key, spec.Level, points, early, late, delta,
            crossedAt, crossedOn, streak, verdict, statement, spec.Advice,
            writer.TermFor(spec.Level, ""), lensed.Flourish);
    }

    private static string Say(SignalSpec spec, TrendVerdict verdict, double early, double late, int streak, int count)
    {
        var from = Fmt(spec, early);
        var to = Fmt(spec, late);
        return verdict switch
        {
            TrendVerdict.TropPeuDeDonnees =>
                $"{count} semaine{(count > 1 ? "s" : "")} mesurée{(count > 1 ? "s" : "")} seulement — pas de quoi conclure",
            TrendVerdict.Acquis when streak == count =>
                $"{spec.Praise} — et c'était déjà vrai au début de la période",
            TrendVerdict.Acquis =>
                $"{spec.Praise} — tenu {streak} semaines d'affilée",
            TrendVerdict.EnProgres =>
                $"en progrès : {from} → {to}, la cible est à {Fmt(spec, spec.Target)}",
            TrendVerdict.EnRecul =>
                $"{spec.Complaint} — et c'était mieux avant : {from} → {to}",
            _ when spec.Meets(late) =>
                $"stable au-dessus de la cible ({to})",
            _ =>
                $"{spec.Complaint} — inchangé sur {count} semaines ({from} → {to})",
        };
    }

    public static string Fmt(SignalSpec spec, double value)
        => spec.IsRatio ? $"{value * 100:F0} %" : $"{value:F1}";

    private List<MonthBand> BuildMonths(
        SortedDictionary<string, List<Dictionary<string, Signal>>> perWeek,
        Dictionary<string, DateOnly> mondays,
        Dictionary<string, WeekUsage> weeks,
        Dictionary<string, List<TrailPoint>> series)
    {
        var bands = new List<MonthBand>();
        var byMonth = perWeek.Keys.GroupBy(w => $"{mondays[w].Year:0000}-{mondays[w].Month:00}")
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byMonth)
        {
            var keys = group.ToList();
            var used = keys.Select(k => weeks.GetValueOrDefault(k)).Where(w => w is not null).ToList()!;
            var first = mondays[keys[0]];
            var monthStart = new DateOnly(first.Year, first.Month, 1);
            var weeksInMonth = (int)Math.Ceiling(DateTime.DaysInMonth(first.Year, first.Month) / 7.0);

            var read = used.Sum(w => w!.TotalRead);
            double Share(ModelFamily f) => read == 0 ? 0
                : (double)used.Sum(w => w!.Models.Values.Where(m => m.Family == f).Sum(m => m.TotalRead)) / read;

            var work = new Dictionary<WorkKind, int>();
            foreach (var w in used)
                foreach (var (kind, n) in w!.Work)
                    work[kind] = work.TryGetValue(kind, out var acc) ? acc + n : n;

            // Le score d'un palier : la part de ses signaux qui tiennent la cible,
            // avec un crédit partiel — sans quoi la courbe ne bouge qu'aux passages
            // de seuil et donne l'impression qu'il ne se passe rien pendant des mois.
            var scores = new Dictionary<int, double>();
            foreach (var level in SignalSpecs.All.Select(s => s.Level).Distinct().OrderBy(l => l))
            {
                var parts = new List<double>();
                foreach (var spec in SignalSpecs.All.Where(s => s.Level == level))
                {
                    if (!series.TryGetValue(spec.Key, out var pts)) continue;
                    var inMonth = pts.Where(p => keys.Contains(p.Week, StringComparer.Ordinal)).ToList();
                    if (inMonth.Count == 0) continue;
                    var mean = inMonth.Average(p => p.Value);
                    parts.Add(Math.Clamp(1 - spec.Gap(mean), 0, 1));
                }
                if (parts.Count > 0) scores[level] = parts.Average();
            }

            bands.Add(new MonthBand(
                group.Key, monthStart, keys.Count, Math.Max(0, weeksInMonth - keys.Count),
                used.Sum(w => w!.Tasks),
                used.SelectMany(w => w!.Sessions).Distinct(StringComparer.Ordinal).Count(),
                used.Aggregate(TimeSpan.Zero, (a, w) => a + w!.ActiveTime),
                read, Share(ModelFamily.Haiku), Share(ModelFamily.Sonnet), Share(ModelFamily.Opus),
                scores, work));
        }
        return bands;
    }

    /// <summary>Les trous de deux semaines ou plus. En dessous, c'est une semaine de congé, pas un signal.</summary>
    private static List<(DateOnly From, DateOnly To, int Weeks)> FindSilences(List<DateOnly> activeMondays)
    {
        var gaps = new List<(DateOnly, DateOnly, int)>();
        for (var i = 1; i < activeMondays.Count; i++)
        {
            var weeks = (activeMondays[i].DayNumber - activeMondays[i - 1].DayNumber) / 7 - 1;
            if (weeks >= 2)
                gaps.Add((activeMondays[i - 1].AddDays(7), activeMondays[i].AddDays(-1), weeks));
        }
        return gaps.OrderByDescending(g => g.Item3).Take(3).ToList();
    }

    private static DateOnly Monday(DateOnly day)
        => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
