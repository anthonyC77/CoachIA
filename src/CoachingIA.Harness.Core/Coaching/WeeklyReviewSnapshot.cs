using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Sérialise et relit une revue hebdomadaire, ou sa seule critique de prompt,
/// dans un fichier JSON local. C'est ce qui permettra à une activity Temporal
/// de préparer la revue, à une autre de la critiquer et à une troisième de
/// l'archiver, sans se passer autre chose que des chemins de fichiers.
///
/// Un seul obstacle à un <c>JsonSerializer.Serialize</c> ordinaire : <see
/// cref="RubricItem"/> porte un <see cref="System.Text.RegularExpressions.Regex"/>
/// qui ne se sérialise pas. On le remplace donc par sa <see cref="RubricItem.Key"/>
/// à l'écriture, et on le réhydrate à la lecture depuis <see
/// cref="PromptRubric.Items"/> — jamais une copie, toujours l'instance canonique.
/// </summary>
public static class WeeklyReviewSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new RubricItemConverter() },
        // Les listes, ensembles et dictionnaires exposés en lecture seule
        // (Observations, Alerts, WeekUsage.ActiveDays…) ne se repeuplent pas
        // par défaut : sans ce réglage, ils reviennent vides de la relecture.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    public static string Serialiser(WeeklyReview revue) => JsonSerializer.Serialize(revue, Options);

    public static WeeklyReview Deserialiser(string json)
        => JsonSerializer.Deserialize<WeeklyReview>(json, Options)
           ?? throw new InvalidDataException("Le bilan sérialisé est vide.");

    public static string SerialiserCritique(PromptCritique critique) => JsonSerializer.Serialize(critique, Options);

    public static PromptCritique DeserialiserCritique(string json)
        => JsonSerializer.Deserialize<PromptCritique>(json, Options)
           ?? throw new InvalidDataException("La critique sérialisée est vide.");

    /// <summary>Un <see cref="RubricItem"/> se réduit à sa clé : le reste se retrouve dans <see cref="PromptRubric.Items"/>.</summary>
    private sealed class RubricItemConverter : JsonConverter<RubricItem>
    {
        public override RubricItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var key = reader.GetString();
            return PromptRubric.Items.FirstOrDefault(i => i.Key == key)
                ?? throw new InvalidDataException($"Critère de grille inconnu : « {key} ».");
        }

        public override void Write(Utf8JsonWriter writer, RubricItem value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Key);
    }
}
