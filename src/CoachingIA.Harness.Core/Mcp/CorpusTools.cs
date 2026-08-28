using System.Text;
using System.Text.Json.Nodes;
using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Core.Mcp;

/// <summary>Ce qu'un outil rend : du texte, et le fait de savoir s'il a échoué.</summary>
public sealed record ToolAnswer(string Text, bool IsError = false);

public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// Les quatre outils que le serveur expose. Ils ne servent que des <em>faits</em> :
/// unités, termes, punitions, patch de référence.
///
/// Les scènes de la lentille restent volontairement dehors. Le coach les fait
/// tourner sans répétition, semaine après semaine ; les rendre accessibles
/// ailleurs les userait plus vite et casserait la seule chose qui les garde
/// vivantes.
/// </summary>
public sealed class CorpusTools(Corpus corpus, string attribution = Attribution.Default)
{
    public Corpus Corpus { get; } = corpus;

    public IReadOnlyList<ToolSpec> Specs =>
    [
        new("chercher_unite",
            "Les faits d'une unité ou d'un bâtiment de StarCraft II : race, rôle, si elle vole, "
            + "tire en l'air, est invisible ou détecte, ce qu'elle contre et ce qui la contre. "
            + "À utiliser avant d'écrire une analogie, pour ne pas lui prêter ce qu'elle ne fait pas.",
            Schema([("nom", "string", "Le nom de l'unité ou du bâtiment. Les alias marchent : « DT », « mutas », « BC ».")], ["nom"])),

        new("chercher_terme",
            "La définition d'un terme du jeu ou du vernaculaire du ladder : inject, supply block, "
            + "cheese, timing attack, tech switch, greedy, punished…",
            Schema([("terme", "string", "Le terme à définir.")], ["terme"])),

        new("punition_pour",
            "Les situations de punition connues pour un signal de coaching ou un palier : la situation, "
            + "ce qui manquait, la sanction et son coût. C'est le squelette d'une scène — "
            + "la matière qu'aucune page de wiki ne donne.",
            Schema([("signal", "string", "Un signal mesuré, par exemple verification_present ou harness_breadth."),
                    ("palier", "integer", "Un palier de 1 à 5, si l'on cherche par niveau plutôt que par signal.")], [])),

        new("patch",
            "La version du jeu à laquelle ce corpus se réfère, ce qu'elle a changé, et les formulations "
            + "qu'elle a rendues fausses. À consulter avant de citer un build order.",
            Schema([], [])),
    ];

    public ToolAnswer Call(string name, JsonObject? arguments) => name switch
    {
        "chercher_unite" => Unite(Text(arguments, "nom")),
        "chercher_terme" => Terme(Text(arguments, "terme")),
        "punition_pour" => Punitions(Text(arguments, "signal"), Number(arguments, "palier")),
        "patch" => Patch(),
        _ => new ToolAnswer($"Outil inconnu : « {name} ».", IsError: true),
    };

    // ------------------------------------------------------------- unités

    private ToolAnswer Unite(string? nom)
    {
        if (string.IsNullOrWhiteSpace(nom))
            return new ToolAnswer("Précisez un nom d'unité.", IsError: true);

        var unit = Corpus.Find(nom) ?? Corpus.FindBuilding(nom);
        if (unit is null)
        {
            // Une suggestion vaut mieux qu'un refus : la plupart des échecs sont
            // des variantes d'orthographe, pas des unités inventées.
            var proches = Corpus.Named
                .Where(u => u.AllNames().Any(n => n.Contains(nom, StringComparison.OrdinalIgnoreCase)
                                               || nom.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .Select(u => u.Name).Distinct().Take(5).ToList();

            return new ToolAnswer(
                $"« {nom} » n'est pas dans le corpus (patch {Corpus.Patch.Version})."
                + (proches.Count > 0 ? $"\nPeut-être : {string.Join(", ", proches)}." : "")
                + "\nNe l'utilisez pas dans une analogie sans l'avoir vérifiée ailleurs.",
                IsError: true);
        }

        var batiment = Corpus.FindBuilding(unit.Name) is not null;
        var b = new StringBuilder();
        b.AppendLine($"{unit.Name} — {unit.Race}, {(batiment ? "bâtiment" : "unité")}");
        if (unit.Role.Length > 0) b.AppendLine($"Rôle : {unit.Role}");

        var traits = new List<string>();
        if (unit.Air) traits.Add("vole");
        if (unit.AntiAir) traits.Add("tire en l'air");
        if (unit.Cloaked) traits.Add("invisible ou enterrée — il faut un détecteur pour la voir");
        if (unit.Detector) traits.Add("détecteur");
        b.AppendLine(traits.Count > 0
            ? $"Caractéristiques : {string.Join(" · ", traits)}"
            : "Caractéristiques : rien de particulier — ni vol, ni détection, ni invisibilité.");

        if (unit.Counters.Length > 0) b.AppendLine($"Contre : {string.Join(", ", unit.Counters)}");
        if (unit.CounteredBy.Length > 0) b.AppendLine($"Contrée par : {string.Join(", ", unit.CounteredBy)}");
        if (unit.Alias.Length > 0) b.AppendLine($"On l'appelle aussi : {string.Join(", ", unit.Alias)}");
        if (unit.Note.Length > 0) b.AppendLine($"\n{unit.Note}");
        if (unit.Figures.Count > 0)
            b.AppendLine($"\nChiffres : {string.Join(", ", unit.Figures.Select(f => $"{f.Key} {f.Value}"))}");

        b.Append($"\n(patch {Corpus.Patch.Version} · {attribution})");
        return new ToolAnswer(b.ToString());
    }

    // -------------------------------------------------------------- termes

    private ToolAnswer Terme(string? terme)
    {
        if (string.IsNullOrWhiteSpace(terme))
            return new ToolAnswer("Précisez un terme.", IsError: true);

        var mecanique = Corpus.Mechanics.FirstOrDefault(m => Matches(m.Name, terme));
        if (mecanique is not null)
            return new ToolAnswer(
                $"{mecanique.Name} — mécanique"
                + (mecanique.Race.Length > 0 ? $" {mecanique.Race}" : "")
                + (mecanique.Level > 0 ? $", rattachée au palier {mecanique.Level}" : "")
                + $"\n{mecanique.Definition}");

        var mot = Corpus.Vernacular.FirstOrDefault(v => Matches(v.Term, terme));
        if (mot is not null)
            return new ToolAnswer($"{mot.Term} — vernaculaire du ladder\n{mot.Definition}");

        // Un terme peut être un nom d'unité mal aiguillé : on rend service
        // plutôt que de renvoyer l'utilisateur vers le bon outil.
        if (Corpus.Find(terme) is not null || Corpus.FindBuilding(terme) is not null)
            return Unite(terme);

        var proches = Corpus.Mechanics.Select(m => m.Name).Concat(Corpus.Vernacular.Select(v => v.Term))
            .Where(n => n.Contains(terme, StringComparison.OrdinalIgnoreCase))
            .Take(5).ToList();

        return new ToolAnswer(
            $"« {terme} » n'est pas dans le corpus."
            + (proches.Count > 0 ? $"\nPeut-être : {string.Join(", ", proches)}." : ""),
            IsError: true);
    }

    private static bool Matches(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || string.Equals(a.Replace("-", " "), b.Replace("-", " "), StringComparison.OrdinalIgnoreCase);

    // ----------------------------------------------------------- punitions

    private ToolAnswer Punitions(string? signal, int? palier)
    {
        var found = Corpus.Punishments.AsEnumerable();
        var quoi = new List<string>();

        if (!string.IsNullOrWhiteSpace(signal))
        {
            found = found.Where(p => string.Equals(p.Signal, signal, StringComparison.OrdinalIgnoreCase));
            quoi.Add($"signal {signal}");
        }
        if (palier is { } n)
        {
            found = found.Where(p => p.Level == n);
            quoi.Add($"palier {n}");
        }
        if (quoi.Count == 0)
            return new ToolAnswer("Précisez un signal ou un palier.", IsError: true);

        var list = found.ToList();
        if (list.Count == 0)
        {
            var connus = Corpus.Punishments.Select(p => p.Signal).Distinct().Order(StringComparer.Ordinal);
            return new ToolAnswer(
                $"Aucune punition pour {string.Join(" et ", quoi)}.\n"
                + $"Signaux couverts : {string.Join(", ", connus)}.\n"
                + "N'en inventez pas : une situation absente du corpus n'a pas été vérifiée.",
                IsError: true);
        }

        var b = new StringBuilder($"{list.Count} situation(s) pour {string.Join(" et ", quoi)} :\n");
        foreach (var p in list)
        {
            b.AppendLine($"\n— {p.Id} (palier {p.Level})");
            if (p.Victim.Length > 0)
                b.AppendLine($"  Camps : {p.Victim} subit"
                    + (p.Aggressor.Length > 0 ? $", {p.Aggressor} punit" : ""));
            b.AppendLine($"  Situation : {p.Situation}");
            b.AppendLine($"  Ce qui manquait : {p.Missing}");
            b.AppendLine($"  Sanction : {p.Sanction}");
            b.AppendLine($"  Coût : {p.Cost}");
        }
        return new ToolAnswer(b.ToString().TrimEnd());
    }

    // --------------------------------------------------------------- patch

    private ToolAnswer Patch()
    {
        var p = Corpus.Patch;
        var b = new StringBuilder($"{Corpus.Game} — patch de référence {p.Version}\n");
        if (p.Summary.Length > 0) b.AppendLine($"\n{p.Summary}");

        if (p.Changes.Length > 0)
        {
            b.AppendLine("\nCe qui a changé :");
            foreach (var c in p.Changes) b.AppendLine($"- {c}");
        }
        if (p.Obsolete.Length > 0)
        {
            b.AppendLine("\nCe que ce patch a rendu FAUX — à ne jamais écrire :");
            foreach (var o in p.Obsolete) b.AppendLine($"- {o}");
        }
        return new ToolAnswer(b.ToString().TrimEnd());
    }

    // -------------------------------------------------------------- outils

    private static string? Text(JsonObject? args, string key)
        => args?[key] is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>().Trim()
            : null;

    private static int? Number(JsonObject? args, string key)
    {
        if (args?[key] is not { } node) return null;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
            System.Text.Json.JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var n) => n,
            _ => null,
        };
    }

    private static JsonObject Schema((string Name, string Type, string Description)[] fields, string[] required)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description) in fields)
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
            schema["required"] = new JsonArray([.. required.Select(r => (JsonNode)JsonValue.Create(r)!)]);
        return schema;
    }
}

public static class Attribution
{
    public const string Default = "faits relus à la main, chiffres Liquipedia CC BY-SA 3.0";
}
