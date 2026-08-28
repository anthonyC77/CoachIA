using System.Globalization;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>Familles de modèles, du moins cher au plus capable.</summary>
public enum ModelFamily { Haiku, Sonnet, Opus, Autre }

public sealed record ModelUsage(ModelFamily Family, string Name)
{
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Tout ce que le modèle a lu, cache compris. C'est ce qui pèse sur le quota.</summary>
    public long TotalRead => InputTokens + CacheReadTokens + CacheWriteTokens;
    public long Total => TotalRead + OutputTokens;
}

/// <summary>
/// Ce qu'une tâche cherchait à faire, déduit des outils qu'elle a mobilisés.
/// L'ordre de priorité encode une idée simple : lire pour ensuite écrire, c'est
/// de l'édition ; lancer un agent, c'est de l'orchestration quoi qu'il fasse.
/// </summary>
public enum WorkKind { Orchestration, Edition, Execution, Recherche, Exploration, Conversation }

public sealed class WeekUsage
{
    public required string Week { get; init; }          // 2026-W34
    public required DateOnly MondayOf { get; init; }
    public HashSet<DateOnly> ActiveDays { get; } = [];
    public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
    public int Tasks { get; set; }
    public int Turns { get; set; }
    public int ToolCalls { get; set; }
    public Dictionary<string, ModelUsage> Models { get; } = new(StringComparer.Ordinal);
    public Dictionary<WorkKind, int> Work { get; } = [];
    public TimeSpan ActiveTime { get; set; }

    public long TotalRead => Models.Values.Sum(m => m.TotalRead);
    public long TotalOutput => Models.Values.Sum(m => m.OutputTokens);
    public long CacheRead => Models.Values.Sum(m => m.CacheReadTokens);
    public double CacheRatio => TotalRead == 0 ? 0 : (double)CacheRead / TotalRead;

    /// <summary>Part de chaque famille dans les jetons lus.</summary>
    public double ShareOf(ModelFamily family)
        => TotalRead == 0 ? 0
         : (double)Models.Values.Where(m => m.Family == family).Sum(m => m.TotalRead) / TotalRead;

    public double CallShareOf(ModelFamily family)
    {
        var calls = Models.Values.Sum(m => m.Calls);
        return calls == 0 ? 0 : (double)Models.Values.Where(m => m.Family == family).Sum(m => m.Calls) / calls;
    }
}

public sealed record UsageAlert(string Key, string Severity, string Title, string Detail);

/// <summary>
/// Les indicateurs minimaux d'usage : combien de jetons par semaine, avec quels
/// modèles, pour quel type de travail.
///
/// Le point de départ est économique. Sur un abonnement au siège, la capacité
/// non consommée d'une semaine ne se reporte pas : elle est perdue. Un
/// utilisateur à 50 % de son enveloppe paie donc plein tarif pour la moitié de
/// l'outil — un problème d'accompagnement bien plus que de facturation, et
/// invisible tant que personne ne le mesure.
///
/// Aucune limite n'est codée en dur ici : les plafonds réels ne sont pas
/// publiés et changent. L'enveloppe hebdomadaire est une VALEUR DE RÉFÉRENCE
/// que l'utilisateur renseigne d'après ce que /usage lui montre. Sans elle, les
/// tendances et les comparaisons d'une semaine à l'autre restent lisibles.
/// </summary>
public sealed class UsageAnalyzer
{
    /// <summary>Enveloppe hebdomadaire de référence, en jetons lus. 0 = pas de référence.</summary>
    public long WeeklyTokenBudget { get; init; }

    /// <summary>En dessous, on considère le siège sous-utilisé.</summary>
    public double UnderUseThreshold { get; init; } = 0.5;

    /// <summary>Au-dessus, l'utilisateur va buter sur son plafond en cours de semaine.</summary>
    public double OverUseThreshold { get; init; } = 0.9;

    public static ModelFamily FamilyOf(string? model)
    {
        if (string.IsNullOrEmpty(model)) return ModelFamily.Autre;
        var m = model.ToLowerInvariant();
        if (m.Contains("haiku")) return ModelFamily.Haiku;
        if (m.Contains("sonnet")) return ModelFamily.Sonnet;
        if (m.Contains("opus")) return ModelFamily.Opus;
        return ModelFamily.Autre;
    }

    public static string WeekKey(DateTimeOffset at)
    {
        var d = at.UtcDateTime.Date;
        var week = ISOWeek.GetWeekOfYear(d);
        return $"{ISOWeek.GetYear(d)}-W{week:00}";
    }

    private static DateOnly MondayOf(DateTimeOffset at)
    {
        var d = at.UtcDateTime.Date;
        var delta = ((int)d.DayOfWeek + 6) % 7;   // lundi = 0
        return DateOnly.FromDateTime(d.AddDays(-delta));
    }

    /// <summary>
    /// Classe une tâche par ce qu'elle a réellement mobilisé. Un signal grossier,
    /// mais qui répond à une vraie question : est-ce que j'utilise l'outil pour
    /// produire, ou seulement pour lire et discuter ?
    /// </summary>
    public static WorkKind Classify(SegmentedTask task)
    {
        var names = task.Turns.SelectMany(t => t.ToolCalls).Select(c => c.Name)
            .Concat(task.AgentTurns.SelectMany(t => t.ToolCalls).Select(c => c.Name))
            .ToList();
        if (names.Count == 0 && task.AgentTurns.Count == 0) return WorkKind.Conversation;

        if (task.AgentTurns.Count > 0 || names.Any(n => n is "Agent" or "Task" or "Skill"))
            return WorkKind.Orchestration;
        if (names.Any(n => n is "Edit" or "Write" or "NotebookEdit" or "MultiEdit"))
            return WorkKind.Edition;
        if (names.Count(n => n == "Bash") * 2 >= names.Count)
            return WorkKind.Execution;
        if (names.Any(n => n is "WebSearch" or "WebFetch"))
            return WorkKind.Recherche;
        if (names.Any(n => n is "Read" or "Grep" or "Glob"))
            return WorkKind.Exploration;
        return WorkKind.Conversation;
    }

    public List<WeekUsage> ByWeek(IEnumerable<TranscriptSession> sessions, TaskSegmenter? segmenter = null)
    {
        segmenter ??= new TaskSegmenter();
        var weeks = new Dictionary<string, WeekUsage>(StringComparer.Ordinal);

        WeekUsage Bucket(DateTimeOffset at)
        {
            var key = WeekKey(at);
            if (!weeks.TryGetValue(key, out var w))
                weeks[key] = w = new WeekUsage { Week = key, MondayOf = MondayOf(at) };
            return w;
        }

        foreach (var session in sessions)
        {
            foreach (var task in segmenter.Segment(session))
            {
                if (task.Turns.Count == 0) continue;
                var week = Bucket(task.StartedAt);
                week.Tasks++;
                week.Sessions.Add(session.SessionId);
                var kind = Classify(task);
                week.Work[kind] = week.Work.TryGetValue(kind, out var n) ? n + 1 : 1;

                foreach (var turn in task.Turns.Concat(task.AgentTurns))
                {
                    // Un tour est compté dans la semaine où il a commencé : une
                    // session à cheval sur dimanche minuit ne se coupe pas en deux.
                    var w = Bucket(turn.StartedAt);
                    w.Turns++;
                    w.ToolCalls += turn.ToolCalls.Count;
                    w.ActiveTime += turn.ActiveDuration;
                    w.ActiveDays.Add(DateOnly.FromDateTime(turn.StartedAt.UtcDateTime));
                    w.Sessions.Add(session.SessionId);

                    foreach (var step in turn.Steps)
                    {
                        var name = step.Model ?? "inconnu";
                        if (!w.Models.TryGetValue(name, out var usage))
                            w.Models[name] = usage = new ModelUsage(FamilyOf(name), name);
                        usage.Calls++;
                        usage.InputTokens += step.InputTokens;
                        usage.CacheReadTokens += step.CacheReadTokens;
                        usage.CacheWriteTokens += step.CacheCreationTokens;
                        usage.OutputTokens += step.OutputTokens;
                    }
                }
            }
        }

        return [.. weeks.Values.OrderBy(w => w.MondayOf)];
    }

    /// <summary>
    /// Les alertes. Chacune vise une décision, pas une curiosité : renoncer à un
    /// siège, changer de modèle par défaut, ou aller voir quelqu'un qui décroche.
    /// </summary>
    public List<UsageAlert> Alerts(IReadOnlyList<WeekUsage> weeks)
    {
        var alerts = new List<UsageAlert>();
        if (weeks.Count == 0) return alerts;

        // La semaine en cours est incomplète : la comparer à une enveloppe
        // hebdomadaire pleine produirait une fausse alerte tous les lundis.
        var thisMonday = MondayOf(DateTimeOffset.UtcNow);
        var closed = weeks.Where(w => w.MondayOf < thisMonday).ToList();
        var recent = closed.TakeLast(4).ToList();
        if (recent.Count == 0) return alerts;

        if (WeeklyTokenBudget > 0)
        {
            var under = recent.TakeLast(2)
                .Where(w => (double)w.TotalRead / WeeklyTokenBudget < UnderUseThreshold).ToList();
            if (under.Count == 2)
                alerts.Add(new UsageAlert("sous_utilisation", "attention",
                    "Siège sous-utilisé deux semaines de suite",
                    string.Join(" · ", under.Select(w =>
                        $"{w.Week} à {(double)w.TotalRead / WeeklyTokenBudget:P0}")) +
                    ". La capacité non consommée ne se reporte pas : elle est perdue."));

            var over = recent.LastOrDefault();
            if (over is not null && (double)over.TotalRead / WeeklyTokenBudget > OverUseThreshold)
                alerts.Add(new UsageAlert("plafond_proche", "attention",
                    "Enveloppe presque épuisée",
                    $"{over.Week} à {(double)over.TotalRead / WeeklyTokenBudget:P0}. " +
                    "Attendez-vous à des coupures en fin de semaine."));
        }

        var last = recent[^1];

        if (last.ActiveDays.Count <= 1)
            alerts.Add(new UsageAlert("adoption_faible", "attention",
                "Un seul jour d'activité sur la semaine",
                $"{last.Week} : {last.ActiveDays.Count} jour actif. " +
                "L'adoption se joue sur la régularité, pas sur le volume."));

        if (last.TotalRead > 0 && last.CacheRatio < 0.3)
            alerts.Add(new UsageAlert("cache_faible", "attention",
                "Le cache ne joue pas son rôle",
                $"{last.CacheRatio:P0} du contexte relu venait du cache en {last.Week}. " +
                "Un préfixe instable fait repayer le même contexte à chaque tour."));

        var opusShare = last.ShareOf(ModelFamily.Opus);
        var haikuShare = last.ShareOf(ModelFamily.Haiku);
        if (opusShare > 0.95 && last.Tasks >= 5)
            alerts.Add(new UsageAlert("monoculture_opus", "info",
                "Tout passe par le modèle le plus lourd",
                $"{opusShare:P0} des jetons sur Opus en {last.Week}. " +
                "Les tâches mécaniques — relecture, reformatage, recherche — coûtent " +
                "beaucoup moins cher sur un modèle plus léger, à qualité égale."));
        else if (haikuShare > 0.9 && last.Tasks >= 5)
            alerts.Add(new UsageAlert("monoculture_haiku", "info",
                "Le modèle le plus léger fait tout le travail",
                $"{haikuShare:P0} des jetons sur Haiku en {last.Week}. " +
                "Sur les tâches de conception, un modèle plus capable évite souvent " +
                "plusieurs reprises — et coûte moins cher au total."));

        if (closed.Count >= 2)
        {
            var previous = closed[^2];
            if (previous.TotalRead > 0)
            {
                var delta = (double)(last.TotalRead - previous.TotalRead) / previous.TotalRead;
                if (delta < -0.5)
                    alerts.Add(new UsageAlert("decrochage", "attention",
                        "Usage divisé par deux d'une semaine à l'autre",
                        $"{previous.Week} → {last.Week} : {delta:P0}. " +
                        "Un décrochage soudain vaut une conversation, pas un tableau."));
            }
        }

        return alerts;
    }
}
