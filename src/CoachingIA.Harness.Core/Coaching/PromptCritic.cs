using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>La critique d'un prompt réel : ce qui manquait, et ce qu'il aurait fallu écrire.</summary>
public sealed record PromptCritique(
    string Original, string Rewrite, List<RubricItem> Missing, List<RubricItem> Present,
    string Cost, string Source);

/// <summary>Le contexte que la critique doit connaître : ce que ce prompt a coûté.</summary>
public sealed record PromptContext(
    string Title, int ReworkTurns, int ToolCalls, bool Completed,
    bool VerificationRan, IReadOnlyList<string> FailedTools, DateOnly Date);

public interface IPromptCritic
{
    PromptCritique? Critique(string prompt, PromptContext context);
}

/// <summary>
/// La critique hors ligne. Elle ne réécrit pas votre prompt — elle en dessine la
/// forme manquante, critère par critère, avec des emplacements à remplir.
///
/// C'est volontairement moins bon qu'un modèle, et c'est assumé : le squelette
/// enseigne la structure, ce qui est déjà l'essentiel du palier&nbsp;1. La
/// version rédigée demande <c>claude -p</c>, qui n'est pas toujours là.
/// </summary>
public sealed class HeuristicPromptCritic : IPromptCritic
{
    public PromptCritique? Critique(string prompt, PromptContext context)
    {
        var missing = PromptRubric.Missing(prompt);
        if (missing.Count == 0) return null;

        var b = new StringBuilder();
        b.AppendLine(prompt.Trim());
        b.AppendLine();
        foreach (var item in missing)
            b.AppendLine($"- {item.Label} : [{Placeholder(item.Key)}]");

        return new PromptCritique(prompt, b.ToString().TrimEnd(),
            missing, PromptRubric.Present(prompt), Cost(context), "heuristique");
    }

    private static string Placeholder(string key) => key switch
    {
        "objectif" => "le verbe et l'objet précis de la demande",
        "perimetre" => "le fichier ou le module concerné, et ce qu'il ne faut pas toucher",
        "acceptation" => "à quoi vous saurez que c'est fini",
        "contraintes" => "ce qui ne doit pas bouger",
        "sortie" => "ce que vous attendez en retour",
        "contexte" => "ce que l'agent ne peut pas deviner",
        "autonomie" => "jusqu'où aller sans vous demander",
        _ => "à compléter",
    };

    internal static string Cost(PromptContext c)
    {
        var parts = new List<string>();
        if (c.ReworkTurns > 0) parts.Add($"{c.ReworkTurns} reprise(s)");
        if (c.ToolCalls > 0) parts.Add($"{c.ToolCalls} appels d'outils");
        if (!c.VerificationRan) parts.Add("aucune vérification");
        if (c.FailedTools.Count > 0) parts.Add($"{c.FailedTools.Count} échec(s) d'outil");
        if (!c.Completed) parts.Add("tâche non aboutie");
        return parts.Count == 0 ? "aucun coût mesuré" : string.Join(", ", parts);
    }
}

/// <summary>
/// La critique rédigée, confiée à Claude via le CLI en mode non interactif.
/// Elle utilise votre session déjà authentifiée — pas de clé d'API à part — et
/// <c>--json-schema</c> pour que la réponse arrive structurée plutôt que
/// devinée dans du texte libre.
///
/// Trois précautions, parce qu'un outil de coaching ne doit jamais devenir un
/// point de panne : on vérifie que le binaire existe, on borne le temps, et
/// toute erreur rend la main à la critique hors ligne.
/// </summary>
public sealed class ClaudePromptCritic(IPromptCritic fallback, TimeSpan? timeout = null, string executable = "claude")
    : IPromptCritic
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(90);

    /// <summary>Dernière erreur rencontrée, pour l'expliquer plutôt que la taire.</summary>
    public string? LastError { get; private set; }

    private const string Schema = """
        {"type":"object","properties":{
          "rewrite":{"type":"string"},
          "missing":{"type":"array","items":{"type":"string"}},
          "note":{"type":"string"}},
         "required":["rewrite","missing"]}
        """;

    public PromptCritique? Critique(string prompt, PromptContext context)
    {
        var offline = fallback.Critique(prompt, context);
        if (offline is null) return null;      // le prompt est déjà complet

        try
        {
            var json = Run(BuildInstruction(prompt, context, offline.Missing));
            if (json is null) return offline;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("structured_output", out var output)) return offline;
            if (!output.TryGetProperty("rewrite", out var rewrite) || rewrite.ValueKind != JsonValueKind.String)
                return offline;

            var text = rewrite.GetString();
            if (string.IsNullOrWhiteSpace(text)) return offline;

            return offline with { Rewrite = text.Trim(), Source = "claude -p" };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return offline;
        }
    }

    private static string BuildInstruction(string prompt, PromptContext c, List<RubricItem> missing)
    {
        var lacks = string.Join("\n", missing.Select(m => $"- {m.Label} : {m.Question}"));
        return $"""
            Tu es un coach en rédaction de prompts pour agents de code. Réécris le prompt
            ci-dessous pour qu'il n'ait pas besoin d'être repris.

            Le prompt d'origine :
            <prompt>
            {prompt}
            </prompt>

            Ce qu'il a coûté dans la réalité : {HeuristicPromptCritic.Cost(c)}.

            Ce qui semble manquer :
            {lacks}

            Contraintes de réécriture, à respecter strictement :
            - Reste en français, dans le registre de l'auteur, sans le vouvoyer ni le tutoyer.
            - N'invente aucun détail technique absent du prompt d'origine ou de son titre
              (« {c.Title} »). Si une information manque, écris un emplacement entre crochets.
            - Ne rallonge pas inutilement : vise deux fois la longueur d'origine au maximum.
            - Le résultat doit être un prompt prêt à copier, pas une explication.

            Renvoie « rewrite » (le prompt réécrit), « missing » (les éléments ajoutés,
            en quelques mots chacun) et « note » (une phrase sur ce qui change).
            """;
    }

    private string? Run(string instruction)
    {
        if (!Exists(executable)) { LastError = $"« {executable} » introuvable dans le PATH"; return null; }

        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        // Surtout pas --bare : ce mode ignore les identifiants d'abonnement et
        // exigerait une clé d'API facturée séparément.
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(instruction);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--json-schema"); psi.ArgumentList.Add(Schema);

        using var process = Process.Start(psi);
        if (process is null) { LastError = "le processus n'a pas démarré"; return null; }
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* au mieux */ }
            LastError = $"délai dépassé ({_timeout.TotalSeconds:F0} s)";
            return null;
        }
        if (process.ExitCode != 0)
        {
            LastError = $"code de sortie {process.ExitCode} : {process.StandardError.ReadToEnd().Trim()}";
            return null;
        }
        return stdout.GetAwaiter().GetResult();
    }

    private static bool Exists(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name);
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [""];
        return paths.Any(dir => extensions.Any(ext =>
            !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, name + ext))));
    }
}

/// <summary>
/// Choisit le prompt à critiquer. Un seul par bilan : celui qui a coûté le plus
/// cher. Critiquer trois prompts d'un coup, c'est un devoir corrigé au stylo
/// rouge — ce qui ne fait progresser personne.
/// </summary>
public static class PromptPicker
{
    public static (SegmentedTask Task, PromptContext Context)? Pick(
        IEnumerable<(SegmentedTask Task, IReadOnlyList<Signal> Signals)> tasks)
    {
        (SegmentedTask, PromptContext)? best = null;
        var bestCost = 0.0;

        foreach (var (task, signals) in tasks)
        {
            if (task.Turns.Count == 0 || task.Turns[0].Prompt.Length < 12) continue;

            var verification = signals.FirstOrDefault(s => s.Key == "verification_present")?.Value ?? 0;
            var failed = task.Turns.SelectMany(t => t.ToolCalls)
                .Where(c => c.Failed).Select(c => c.Name).Distinct().ToList();

            // Le coût observé : les reprises d'abord, puis l'abandon, puis les
            // échecs d'outil. Le volume d'outils ne compte pas — une longue
            // tâche réussie n'est pas un problème.
            var observed = task.ReworkTurns * 3.0
                         + (task.Completed || task.InProgress ? 0 : 4.0)
                         + failed.Count * 0.5;

            // Un prompt qui n'a rien coûté n'est pas critiqué, même s'il est
            // pauvre sur le papier. Reprocher sa forme à une demande qui a
            // marché du premier coup, c'est de la correction au stylo rouge —
            // le pendant exact de la félicitation de politesse.
            if (observed <= 0) continue;

            var cost = observed + (1 - PromptRubric.Coverage(task.Turns[0].Prompt)) * 2.0;
            if (cost <= bestCost) continue;

            bestCost = cost;
            best = (task, new PromptContext(
                task.Title, task.ReworkTurns, task.ToolCalls, task.Completed,
                verification > 0, failed, DateOnly.FromDateTime(task.StartedAt.UtcDateTime)));
        }
        return best is null || bestCost < 1.0 ? null : best;
    }
}
