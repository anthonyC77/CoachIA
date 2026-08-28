using System.Text.RegularExpressions;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>Une tâche reconstituée : un objectif, un ou plusieurs tours.</summary>
public sealed class SegmentedTask
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public string Title { get; set; } = "";
    /// <summary>Les tours de la conversation principale. Eux seuls comptent comme des reprises.</summary>
    public List<Turn> Turns { get; } = [];

    /// <summary>
    /// Les tours de sous-agents rattachés à cette tâche. Tenus à part : ils ne
    /// sont ni des prompts humains ni des reprises, mais leur travail doit
    /// apparaître dans les traces — sans quoi le palier 5 n'a rien à mesurer.
    /// </summary>
    public List<Turn> AgentTurns { get; } = [];

    /// <summary>D'où vient la frontière. Aujourd'hui toujours l'heuristique.</summary>
    public string Source { get; set; } = "heuristic";

    /// <summary>La tâche a utilisé TaskCreate / TaskUpdate — un indice de décomposition explicite.</summary>
    public bool HasTaskEvents { get; set; }

    /// <summary>Pourquoi chaque tour a été rattaché ici. Sert à auditer la découpe.</summary>
    public List<string> Decisions { get; } = [];

    public DateTimeOffset StartedAt => Turns.Count == 0 ? default : Turns[0].StartedAt;
    public DateTimeOffset EndedAt => Turns.Count == 0 ? default : Turns[^1].EndedAt;

    /// <summary>
    /// Du premier prompt au dernier événement — le temps passé au mur, nuit
    /// comprise. À n'utiliser que pour situer la tâche dans le calendrier.
    /// </summary>
    public TimeSpan WallDuration => EndedAt - StartedAt;

    /// <summary>
    /// Le temps réellement passé à travailler : la somme des tours, sans les
    /// silences entre eux. Une tâche reprise le lendemain a duré vingt minutes,
    /// pas douze heures — et c'est cette durée-là qui doit nourrir les signaux.
    /// </summary>
    public TimeSpan ActiveDuration => Turns.Aggregate(TimeSpan.Zero, (acc, t) => acc + t.ActiveDuration);

    /// <summary>Le dernier tour n'a pas rendu la main : la tâche est encore ouverte.</summary>
    public bool InProgress => Turns.Count > 0 && Turns[^1].StopReason is null or "tool_use";

    /// <summary>Tours correctifs : le premier prompt ne comptait pas, les suivants réparent.</summary>
    public int ReworkTurns => Math.Max(0, Turns.Count - 1);
    public int ToolCalls => Turns.Sum(t => t.ToolCalls.Count) + AgentTurns.Sum(t => t.ToolCalls.Count);
    public bool Completed => Turns.Count > 0 && Turns[^1].Completed;
}

/// <summary>
/// Le pari algorithmique du projet. La télémétrie connaît des tours ; le coach
/// raisonne en tâches. Entre les deux, il faut décider si un nouveau prompt
/// ouvre un sujet ou répare le précédent.
///
/// Deux sources, dans cet ordre :
///   1. les événements TaskCreate / TaskUpdate, quand la session s'en sert :
///      ce sont des frontières déclarées, pas devinées ;
///   2. à défaut, une heuristique lexicale et temporelle, dont chaque décision
///      est journalisée pour pouvoir être relue et corrigée à la main.
///
/// Cette classe est faite pour être fausse au début. C'est pour cela qu'elle
/// explique ses choix plutôt que de se contenter de découper.
/// </summary>
public sealed class TaskSegmenter
{
    /// <summary>Ouvertures qui annoncent une correction du tour précédent.</summary>
    private static readonly string[] CorrectiveOpeners =
    [
        "non", "non,", "en fait", "plutôt", "au lieu", "corrige", "corriges", "reprends",
        "refais", "reformule", "ça ne marche", "ca ne marche", "ça marche pas", "toujours pas",
        "il manque", "tu as oublié", "pas tout à fait", "presque", "essaie", "essaye",
        "peux-tu plutôt", "j'ai une erreur", "erreur", "ça plante", "ca plante", "revois",
    ];

    /// <summary>Ouvertures de continuation : même sujet, on avance.</summary>
    private static readonly string[] ContinuationOpeners =
    [
        "continue", "poursuis", "vas-y", "ok", "oui", "d'accord", "parfait", "super",
        "et maintenant", "ensuite", "maintenant", "aussi", "et ", "pareil",
    ];

    private static readonly Regex Referential =
        new(@"\b(ça|ca|cela|celui|celle|ceux|le même|la même|ce|cette|cet|il|elle|ils|elles|y|en)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Au-delà de ce silence, un nouveau prompt ouvre un nouveau sujet.</summary>
    public TimeSpan ContinuationWindow { get; init; } = TimeSpan.FromMinutes(25);

    /// <summary>En dessous de cette longueur, un prompt ressemble à un ajustement.</summary>
    public int ShortPromptChars { get; init; } = 160;

    public List<SegmentedTask> Segment(TranscriptSession session)
    {
        // Les tours de sous-agent appartiennent à la tâche de leur parent :
        // ils ne sont jamais des frontières.
        var turns = session.Turns.Where(t => !t.IsSidechain).ToList();
        var tasks = new List<SegmentedTask>();
        SegmentedTask? currentTask = null;
        var index = 0;

        foreach (var turn in turns)
        {
            var previous = currentTask?.Turns[^1];
            var (isNew, reason) = IsNewTask(turn, previous);

            if (isNew || currentTask is null)
            {
                currentTask = new SegmentedTask
                {
                    Id = $"{session.SessionId}#{++index}",
                    SessionId = session.SessionId,
                    Title = MakeTitle(turn.Prompt),
                };
                tasks.Add(currentTask);
                currentTask.Decisions.Add($"ouverture — {reason}");
            }
            else
            {
                currentTask.Decisions.Add($"rattaché — {reason}");
            }
            currentTask.Turns.Add(turn);

            // La tâche s'est appuyée sur le système de tâches de l'agent. On le
            // note, sans prétendre que la frontière en vient : tant que la
            // segmentation reste lexicale, la dire « déclarée » serait faux.
            if (turn.ToolCalls.Any(c => c.Name is "TaskUpdate" or "TaskCreate"))
                currentTask.HasTaskEvents = true;
        }

        foreach (var t in tasks)
            if (t.Title.Length == 0) t.Title = "Travail sans prompt initial";

        AttachAgentTurns(session, tasks);
        return tasks;
    }

    /// <summary>
    /// Un sous-agent appartient à la tâche en cours au moment où il tourne. On
    /// le rattache par le temps plutôt que par un identifiant : les transcripts
    /// ne relient pas explicitement une sidechain à son tour parent.
    /// </summary>
    private static void AttachAgentTurns(TranscriptSession session, List<SegmentedTask> tasks)
    {
        if (tasks.Count == 0) return;
        foreach (var agentTurn in session.Turns.Where(t => t.IsSidechain))
        {
            SegmentedTask? host = null;
            foreach (var task in tasks)
            {
                if (task.Turns.Count == 0) continue;
                if (task.Turns[0].StartedAt <= agentTurn.StartedAt) host = task;
                else break;
            }
            (host ?? tasks[0]).AgentTurns.Add(agentTurn);
        }
    }

    /// <summary>
    /// La décision, et sa justification. Rendre le « pourquoi » obligatoire est
    /// ce qui permettra de corriger l'heuristique sur des cas réels plutôt que
    /// sur des intuitions.
    /// </summary>
    public (bool IsNew, string Reason) IsNewTask(Turn turn, Turn? previous)
    {
        if (previous is null) return (true, "premier tour de la session");

        var prompt = turn.Prompt.Trim();
        if (prompt.Length == 0) return (false, "tour de continuation sans prompt");

        var gap = turn.StartedAt - previous.EndedAt;
        if (gap > ContinuationWindow)
            return (true, $"silence de {gap.TotalMinutes:F0} min avant le prompt");

        var lower = prompt.ToLowerInvariant();

        foreach (var opener in CorrectiveOpeners)
            if (lower.StartsWith(opener, StringComparison.Ordinal))
                return (false, $"ouverture corrective « {opener} »");

        // Un tour qui n'a pas abouti et qu'on relance immédiatement répare,
        // il n'ouvre pas.
        if (!previous.Completed && gap < TimeSpan.FromMinutes(5))
            return (false, "le tour précédent n'avait pas rendu la main");

        foreach (var opener in ContinuationOpeners)
            if (lower.StartsWith(opener, StringComparison.Ordinal))
                return (false, $"ouverture de continuation « {opener} »");

        if (prompt.Length < ShortPromptChars && Referential.IsMatch(lower))
            return (false, $"prompt court ({prompt.Length} car.) et référentiel");

        return (true, "prompt autonome");
    }

    private static string MakeTitle(string prompt)
    {
        var text = prompt.Trim();
        if (text.Length == 0) return "";
        var stop = text.IndexOfAny(['.', '?', '!', '\n']);
        if (stop > 12) text = text[..stop];
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 80 ? text : text[..79] + "…";
    }
}
