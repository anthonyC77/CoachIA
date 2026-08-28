using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoachingIA.Harness.Core.Transcripts;

public sealed record Signal(string Key, int Level, double Value, string Evidence);

/// <summary>
/// Les signaux calculables sans juge LLM, directement depuis les transcripts.
///
/// Découverte de la phase 00 : les transcripts portent le bloc `usage` de
/// chaque appel au modèle — input_tokens, cache_read, cache_creation. Les
/// signaux de contexte (pression de fenêtre, taux de cache) qu'on croyait
/// réservés à la télémétrie OpenTelemetry sont donc disponibles ici. Cela
/// retire une dépendance entière à la voie de collecte principale.
///
/// Chaque signal porte son « evidence » : la phrase qui explique d'où vient le
/// chiffre. Sans elle, un bilan ne peut pas citer, et un coach qui ne cite pas
/// n'est qu'un tableau de bord.
/// </summary>
public sealed class SignalExtractor
{
    /// <summary>
    /// Fenêtre de référence pour la pression de contexte. C'est une HYPOTHÈSE :
    /// elle dépend du modèle, et un mauvais réglage produit des taux au-dessus
    /// de 100 % sans rien casser d'autre. L'hypothèse est donc rappelée dans
    /// l'evidence du signal, pour qu'une erreur de réglage se voie au lieu de
    /// se propager en silence.
    /// </summary>
    public long ContextWindow { get; init; } = 200_000;

    private static readonly Regex VerificationCmd =
        new(@"\b(dotnet\s+test|npm\s+(run\s+)?test|npm\s+run\s+build|ng\s+test|ng\s+build|pytest|jest|vitest|cargo\s+test|go\s+test|dotnet\s+build|eslint|ruff|mypy)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AcceptanceCue =
        new(@"\b(doit|devra|critère|attendu|il faut que|jusqu'à ce que|test|tests? (vert|passe)|vérifie|valide|sans (casser|régression)|renvoie|retourne)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<Signal> ForTask(SegmentedTask task, TranscriptSession session)
    {
        var signals = new List<Signal>();
        var turns = task.Turns;
        var calls = turns.SelectMany(t => t.ToolCalls).ToList();
        var steps = turns.SelectMany(t => t.Steps).ToList();
        var agentCalls = task.AgentTurns.SelectMany(t => t.ToolCalls).Count();
        void Add(string key, int level, double value, string evidence)
            => signals.Add(new Signal(key, level, value, evidence));

        // ---------- palier 1 : le prompt ----------
        var first = turns[0].Prompt;
        Add("rework_ratio", 1, turns.Count <= 1 ? 0 : (double)task.ReworkTurns / turns.Count,
            task.ReworkTurns == 0
                ? "aucune reprise : le premier prompt a suffi"
                : $"{task.ReworkTurns} reprise(s) après le prompt initial");

        Add("prompts_per_task", 1, turns.Count, $"{turns.Count} prompt(s) pour cette tâche");
        // Une tâche encore en cours n'a pas d'issue : la compter comme un échec
        // punirait le simple fait d'être en train de travailler. La dernière
        // tâche de la session la plus récente est presque toujours dans ce cas.
        if (task.InProgress)
            Add("first_try_success", 1, double.NaN, "tâche encore en cours — issue inconnue");
        else
            Add("first_try_success", 1, task.ReworkTurns == 0 && task.Completed ? 1 : 0,
                task.ReworkTurns == 0 && task.Completed ? "close sans reprise" : "a demandé au moins une relance");

        var hasCriteria = AcceptanceCue.IsMatch(first);
        Add("has_acceptance_criteria", 1, hasCriteria ? 1 : 0,
            hasCriteria
                ? "le prompt initial dit à quoi ressemble le résultat attendu"
                : "le prompt initial ne dit pas comment savoir que c'est fini");

        // ---------- palier 2 : le contexte ----------
        var peak = turns.Max(t => t.PeakInputTokens);
        var pressure = ContextWindow == 0 ? 0 : (double)peak / ContextWindow;
        Add("context_pressure", 2, pressure,
            pressure > 1
                ? $"pic de contexte lu : {Fr(peak)} jetons — au-dessus de la fenêtre supposée ({Fr(ContextWindow)}), vérifiez le réglage"
                : $"pic de contexte lu : {Fr(peak)} jetons ({pressure:P0} d'une fenêtre de {Fr(ContextWindow)})");

        var totalIn = steps.Sum(s => s.TotalInputTokens);
        var cached = steps.Sum(s => s.CacheReadTokens);
        Add("cache_read_ratio", 2, totalIn == 0 ? 0 : (double)cached / totalIn,
            totalIn == 0 ? "aucun appel mesuré"
                         : $"{(double)cached / totalIn:P0} du contexte relu venait du cache");

        var reads = calls.Count(c => c.Name == "Read");
        var wholeFileReads = calls.Count(c => c.Name == "Read" && !HasAny(c.InputJson, "offset", "limit"));
        var greps = calls.Count(c => c.Name is "Grep" or "Glob");
        var precisionBase = wholeFileReads + greps;
        Add("load_precision", 2, precisionBase == 0 ? 1 : (double)greps / precisionBase,
            precisionBase == 0 ? "aucune lecture de fichier"
                               : $"{greps} recherche(s) ciblée(s) contre {wholeFileReads} fichier(s) lu(s) en entier");

        var sidechains = session.Turns.Count(t => t.IsSidechain);
        Add("context_isolation", 2, sidechains, $"{sidechains} tour(s) délégué(s) à un sous-agent");

        // ---------- palier 3 : le harnais ----------
        var distinct = calls.Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList();
        Add("harness_breadth", 3, distinct.Count,
            distinct.Count == 0 ? "aucun outil" : $"{distinct.Count} outils distincts : {string.Join(", ", distinct.Take(6))}");

        var skills = turns.SelectMany(t => t.Skills).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var skillCalls = calls.Count(c => c.Name == "Skill") + skills.Count;
        Add("skill_usage_rate", 3, calls.Count == 0 ? 0 : (double)skillCalls / calls.Count,
            skills.Count > 0 ? $"skills mobilisées : {string.Join(", ", skills)}" : "aucune skill mobilisée");

        var mcp = calls.Count(c => c.IsMcp);
        Add("mcp_utilization", 3, calls.Count == 0 ? 0 : (double)mcp / calls.Count,
            mcp == 0 ? "aucun serveur MCP appelé"
                     : $"{mcp} appel(s) MCP vers {string.Join(", ", calls.Where(c => c.IsMcp).Select(c => c.McpServer).Distinct())}");

        var failed = calls.Count(c => c.Failed);
        Add("tool_failure_rate", 3, calls.Count == 0 ? 0 : (double)failed / calls.Count,
            failed == 0 ? "aucun échec d'outil" : $"{failed} échec(s) sur {calls.Count} appels");

        var verifications = calls.Where(IsVerification).ToList();
        Add("verification_present", 3, verifications.Count > 0 ? 1 : 0,
            verifications.Count > 0
                ? $"vérification lancée : {Snippet(verifications[0].InputJson)}"
                : "aucun test, build ou lint pendant la tâche");

        // ---------- palier 4 : la boucle ----------
        Add("autonomy_ratio", 4, turns.Count == 0 ? 0 : (double)calls.Count / turns.Count,
            $"{calls.Count} appels d'outils pour {turns.Count} intervention(s) humaine(s)");

        var parallel = steps.Count(s => s.ToolUseBlocks > 1);
        Add("parallelism_index", 4, steps.Count == 0 ? 0 : (double)parallel / steps.Count,
            parallel == 0 ? "aucun lot d'outils lancé en parallèle"
                          : $"{parallel} étape(s) où plusieurs outils sont partis ensemble");

        var lastVerified = verifications.Count > 0 && !verifications[^1].Failed
                           && calls.LastIndexOf(verifications[^1]) >= calls.Count - 3;
        if (task.InProgress)
            Add("loop_closure", 4, double.NaN, "tâche encore en cours — clôture inconnue");
        else
        Add("loop_closure", 4, lastVerified ? 1 : 0,
            lastVerified ? "la tâche se termine sur une vérification réussie"
                         : "la tâche se termine sans vérification finale");

        Add("plan_structure", 4, calls.Any(c => c.Name is "TaskCreate") ? 1 : 0,
            calls.Any(c => c.Name is "TaskCreate") ? "la tâche a été décomposée explicitement"
                                                  : "aucune décomposition explicite");

        // ---------- palier 5 : le graphe ----------
        var agents = Math.Max(calls.Count(c => c.Name is "Agent" or "Task"), task.AgentTurns.Count);
        Add("graph_depth", 5, agents,
            agents == 0 ? "aucun sous-agent lancé"
                        : $"{agents} sous-agent(s), {agentCalls} appel(s) d'outil délégué(s)");

        var retries = CountRetries(calls);
        Add("self_correction", 5, retries.Different + retries.Identical == 0 ? 0
                : (double)retries.Different / (retries.Different + retries.Identical),
            retries.Identical + retries.Different == 0
                ? "aucun outil rejoué après échec"
                : $"{retries.Different} rejeu(x) avec une stratégie différente, {retries.Identical} à l'identique");

        return signals;
    }

    /// <summary>Milliers séparés par une espace fine : le rapport se lit en français.</summary>
    private static string Fr(long value)
    {
        var digits = Math.Abs(value).ToString();
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) b.Append('\u202f');
            b.Append(digits[i]);
        }
        return (value < 0 ? "-" : "") + b;
    }

    private static bool IsVerification(ToolCall c)
    {
        if (c.Name is "Bash" && c.InputJson is { } json) return VerificationCmd.IsMatch(json);
        return false;
    }

    /// <summary>
    /// Rejouer un outil qui vient d'échouer avec exactement les mêmes paramètres
    /// n'est pas se corriger : c'est espérer. Le signal distingue les deux.
    /// </summary>
    private static (int Identical, int Different) CountRetries(List<ToolCall> calls)
    {
        int identical = 0, different = 0;
        for (var i = 0; i < calls.Count; i++)
        {
            if (!calls[i].Failed) continue;
            for (var j = i + 1; j < Math.Min(calls.Count, i + 4); j++)
            {
                if (calls[j].Name != calls[i].Name) continue;
                if (string.Equals(calls[j].InputJson, calls[i].InputJson, StringComparison.Ordinal)) identical++;
                else different++;
                break;
            }
        }
        return (identical, different);
    }

    private static bool HasAny(string? json, params string[] names)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var n in names)
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(n, out _)) return true;
        }
        catch (JsonException) { /* entrée illisible : on répond non plutôt que d'échouer */ }
        return false;
    }

    private static string Snippet(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "(sans détail)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            {
                var s = cmd.GetString() ?? "";
                return s.Length <= 60 ? s : s[..59] + "…";
            }
        }
        catch (JsonException) { }
        return "(sans détail)";
    }
}
