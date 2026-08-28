using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Lit indifféremment <c>"clé": "une scène"</c> et <c>"clé": ["une", "autre"]</c>.
///
/// Une lentille écrite avant l'arrivée des variantes reste donc valable telle
/// quelle : c'est la condition pour que le format des packs puisse évoluer sans
/// obliger quiconque à réécrire les siens.
/// </summary>
public sealed class VariantsConverter : JsonConverter<Dictionary<string, string[]>>
{
    public override Dictionary<string, string[]> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (reader.TokenType == JsonTokenType.Null) return result;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Un bloc de vocabulaire doit être un objet.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Clé attendue.");

            var key = reader.GetString()!;
            reader.Read();

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var single = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(single)) result[key] = [single];
                    break;

                case JsonTokenType.StartArray:
                    var list = new List<string>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.String)
                            throw new JsonException($"« {key} » : le tableau ne doit contenir que du texte.");
                        var item = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(item)) list.Add(item);
                    }
                    if (list.Count > 0) result[key] = [.. list];
                    break;

                case JsonTokenType.Null:
                    break;   // une clé mise à null retombe simplement sur la lentille de dessous

                default:
                    throw new JsonException($"« {key} » : texte ou tableau de textes attendu.");
            }
        }
        throw new JsonException("Objet non terminé.");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string[]> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, variants) in value)
        {
            writer.WritePropertyName(key);
            // Une seule scène se réécrit comme une chaîne : le fichier reste lisible
            // à la main, ce qui est le seul intérêt d'un pack en JSON.
            if (variants.Length == 1) { writer.WriteStringValue(variants[0]); continue; }
            writer.WriteStartArray();
            foreach (var v in variants) writer.WriteStringValue(v);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// Choisit une scène parmi les variantes d'une clé.
///
/// Le tirage est <strong>déterministe</strong>, et ce n'est pas un détail de
/// confort : le bilan est archivé par comparaison d'empreinte. Un tirage au sort
/// ferait apparaître une version « différente » à chaque régénération, et
/// l'historique se remplirait de bilans identiques au mot près sauf l'image.
///
/// Il est aussi <strong>sans état</strong>. Pas de fichier « déjà vues » à écrire :
/// il divergerait d'un poste à l'autre, et deux machines raconteraient deux
/// histoires pour la même semaine. Le rang dans la rotation se déduit de la
/// semaine, que les deux machines connaissent déjà.
/// </summary>
public static class VariantPicker
{
    /// <summary>Le lundi de référence. Le choix de la date n'a pas d'importance ; sa stabilité, si.</summary>
    private static readonly DateOnly Epoch = new(2020, 1, 6);

    /// <summary>Le numéro de cycle d'une semaine : son rang depuis l'origine.</summary>
    public static int CycleOf(DateOnly day) => (day.DayNumber - Epoch.DayNumber) / 7;

    public static int CycleOf(DateTimeOffset at) => CycleOf(DateOnly.FromDateTime(at.UtcDateTime));

    /// <summary>Le cycle d'une semaine ISO écrite « 2026-W34 ». Retombe sur aujourd'hui si illisible.</summary>
    public static int CycleOfWeek(string? week)
    {
        if (week is null || week.Length < 8 || week[4] != '-' || (week[5] is not ('W' or 'w'))
            || !int.TryParse(week[..4], out var year)
            || !int.TryParse(week[6..], out var number))
            return CycleOf(DateTimeOffset.UtcNow);

        // On repasse par une vraie date : compter « année × 52 » ferait sauter la
        // rotation d'un cran au passage des années à 53 semaines.
        try
        {
            var monday = System.Globalization.ISOWeek.ToDateTime(year, number, DayOfWeek.Monday);
            return CycleOf(DateOnly.FromDateTime(monday));
        }
        catch (ArgumentOutOfRangeException)
        {
            return CycleOf(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// La scène du cycle. Le décalage par clé évite que tous les signaux
    /// avancent au même pas — sans quoi deux images voisines resteraient
    /// éternellement voisines, et la variété se verrait moins qu'elle n'existe.
    /// </summary>
    public static string? Pick(string[]? variants, string key, int cycle)
    {
        if (variants is null || variants.Length == 0) return null;
        if (variants.Length == 1) return variants[0];

        var offset = Offset(key);
        var index = (int)(((long)cycle + offset) % variants.Length);
        if (index < 0) index += variants.Length;
        return variants[index];
    }

    /// <summary>Un décalage stable par clé. Volontairement simple : il doit être reproductible partout, pas imprévisible.</summary>
    private static int Offset(string key)
    {
        var sum = 0;
        foreach (var c in key) sum = (sum * 31 + c) & 0x7FFFFFF;
        return sum;
    }
}
