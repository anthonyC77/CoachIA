using System.Globalization;
using CoachingIA.Harness.Core.Phoenix;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Projette les signaux de <see cref="SignalExtractor"/> en annotations Phoenix
/// de type CODE, posées sur le span de tâche (spec phoenix-visualisation §4.1).
///
/// Fonction pure : aucun appel réseau, aucun effet de bord. C'est
/// <see cref="TranscriptIngestor"/> qui envoie le résultat via
/// <see cref="IPhoenixClient"/> — cette classe ne fait que la mise en forme.
///
/// Deux règles portent la projection :
/// - le NaN reste un NaN : un signal indéterminé rend une annotation sans
///   score mais avec son explication, jamais un zéro qui mentirait dans les
///   moyennes de Phoenix ;
/// - pas de label : les signaux sont des ratios continus sans seuil validé,
///   des bandes de couleur inventées auraient l'air d'un jugement sans en
///   avoir la base.
///
/// Les attributs `signal.{clé}` écrits par TranscriptIngestor restent en
/// place : l'annotation s'ajoute, elle ne les remplace pas.
/// </summary>
public static class SignalAnnotations
{
    public static IReadOnlyList<SpanAnnotation> FromSignals(
        IReadOnlyList<Signal> signaux, string spanIdHex, HarnessOptions options)
    {
        var annotations = new List<SpanAnnotation>(signaux.Count);

        foreach (var signal in signaux)
        {
            annotations.Add(new SpanAnnotation(
                Name: signal.Key,
                SpanId: spanIdHex,
                AnnotatorKind: AnnotatorKinds.Code,
                Result: new AnnotationResult(
                    Label: null,
                    Score: double.IsNaN(signal.Value) ? null : signal.Value,
                    Explication: Cut(signal.Evidence, options.MaxValueChars)),
                Metadata: new Dictionary<string, string>
                {
                    ["level"] = signal.Level.ToString(CultureInfo.InvariantCulture),
                    ["source"] = "transcript",
                },
                Identifier: signal.Key));
        }

        return annotations;
    }

    /// <summary>Même troncature que TranscriptIngestor.Cut et SpanFactory.Cut.</summary>
    private static string Cut(string value, int maxChars)
        => value.Length <= maxChars
            ? value
            : string.Concat(value.AsSpan(0, maxChars), "… [tronqué]");
}
