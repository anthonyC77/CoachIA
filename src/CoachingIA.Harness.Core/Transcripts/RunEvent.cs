using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Le contrat d'événement du lecteur de runs (« Ruche »). C'est la forme
/// publique : ce qu'un lecteur — 3D, tableau, terminal — a le droit de voir.
///
/// Trois décisions y sont gravées, et ce sont elles qui coûtent cher si on les
/// rate :
///
/// 1. <see cref="Seq"/> est l'ordre LOGIQUE, <see cref="T"/> l'horloge vécue.
///    Avec cinq sous-agents en parallèle, les horodatages n'ordonnent rien de
///    fiable ; c'est le numéro de séquence qui rend le rejeu déterministe.
///    Attention : exporté depuis un transcript, <see cref="Seq"/> est DÉRIVÉ
///    des horodatages, faute de compteur global dans le fichier de Claude Code.
///    Un journal produit en direct par les hooks portera, lui, un vrai compteur.
/// 2. <see cref="Ref"/> porte le volumineux (prompt, sortie, diff) par
///    empreinte, jamais par valeur. Sans ça le journal fait des centaines de
///    mégaoctets et aucun lecteur ne le charge.
/// 3. <see cref="Origin"/> sépare l'observé (jamais recalculé) du dérivé (les
///    drapeaux des détecteurs) et de l'humain. Mélanger les trois interdit de
///    rejouer un vieux run avec un détecteur neuf — l'usage le plus utile.
///
/// Le miroir TypeScript vit dans <c>web/ruche/src/core/events.ts</c>. Les deux
/// décrivent la même chose : <see cref="Kinds"/> existe pour qu'une divergence
/// casse un test au lieu de se découvrir à l'écran.
/// </summary>
public sealed class RunEvent
{
    [JsonPropertyName("v")] public int V { get; init; } = 1;
    [JsonPropertyName("seq")] public int Seq { get; set; }

    /// <summary>Millisecondes depuis le début du run.</summary>
    [JsonPropertyName("t")] public long T { get; init; }

    [JsonPropertyName("run")] public required string Run { get; init; }
    [JsonPropertyName("actor")] public required string Actor { get; init; }
    [JsonPropertyName("origin")] public required string Origin { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; init; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunCost? Cost { get; init; }

    /// <summary>
    /// La charge utile propre au type d'événement (tool, args, summary, to…).
    /// Un dictionnaire plutôt qu'une hiérarchie de classes : le lecteur est
    /// tolérant à l'inconnu, et ajouter un champ ne doit casser personne.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?> Payload { get; } = [];

    /// <summary>Les types d'événements que le lecteur sait interpréter.</summary>
    public static readonly string[] Kinds =
    [
        "run.start", "run.end",
        "agent.spawn", "agent.end", "agent.stop.attempt",
        "think.start", "think.end",
        "tool.start", "tool.end",
        "hook.allow", "hook.deny",
        "wait.start", "wait.end",
        "msg", "human",
        "flag", "flag.clear",
    ];

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Ce qu'une étape a coûté. Les € restent à zéro quand la source ne les porte pas.</summary>
public sealed class RunCost
{
    [JsonPropertyName("calls")] public int Calls { get; init; }
    [JsonPropertyName("tok_in")] public long TokensIn { get; init; }
    [JsonPropertyName("tok_out")] public long TokensOut { get; init; }
    [JsonPropertyName("eur")] public double Eur { get; init; }
}

/// <summary>Un agent déclaré dans l'en-tête du fichier de run.</summary>
public sealed class RunAgent
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("depth")] public required int Depth { get; init; }
}
