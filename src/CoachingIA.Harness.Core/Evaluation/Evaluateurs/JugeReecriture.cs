using System.Diagnostics;
using System.Text.Json;

namespace CoachingIA.Harness.Core.Evaluation.Evaluateurs;

/// <summary>
/// Le juge : un avis extérieur sur la <strong>même</strong> question qu'un
/// évaluateur du code, et rien de plus.
///
/// <para>Ce qu'il rend n'est pas « la réécriture est-elle bonne » — c'est
/// « le juge et le code disent-ils la même chose ». Un désaccord n'accuse
/// personne : il désigne un endroit où la mesure mécanique et le jugement
/// divergent, donc un endroit à regarder. C'est le seul usage honnête d'un
/// modèle dans une brique qui sert à mesurer un autre modèle.</para>
///
/// <para>Il est <c>Deterministe = false</c>, ce qui suffit à le tenir hors de
/// l'état approuvé et hors de la porte. Une panne du juge rend une indécision,
/// jamais un échec : une coupure réseau n'est pas une régression.</para>
/// </summary>
public sealed class JugeReecriture(IClaudeCli cli, ContrainteReecriture reference) : IEvaluateur
{
    public string Nom => "juge_" + reference.Nom;
    public string Famille => "reecriture";
    public Bareme Bareme => Baremes.Accord;
    public bool Deterministe => false;

    private const string Schema = """
        {"type":"object","properties":{
          "verdict":{"type":"string","enum":["conforme","non_conforme","doute"]},
          "raison":{"type":"string"}},
         "required":["verdict","raison"]}
        """;

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not ReecritureObservee vue) return null;

        // La référence d'abord : sans réponse du code, il n'y a pas d'accord à
        // mesurer, et appeler le juge coûterait un appel pour rien.
        var codeDit = reference.Evaluer(epreuve, production);
        if (codeDit is null || codeDit.Indecis)
            return Bareme.Indecis(Nom, $"« {reference.Nom} » n'a pas tranché : il n'y a rien à confronter");

        var chrono = Stopwatch.StartNew();
        var json = cli.Demander(Instruction(vue), Schema);
        chrono.Stop();

        if (json is null)
            return Bareme.Indecis(Nom, "le juge n'a pas répondu : " + (cli.DerniereErreur ?? "raison inconnue"));

        string jugeDit, raison;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("structured_output", out var sortie))
                return Bareme.Indecis(Nom, "réponse du juge sans sortie structurée");
            jugeDit = Chaine(sortie, "verdict");
            raison = Chaine(sortie, "raison");
        }
        catch (JsonException ex)
        {
            return Bareme.Indecis(Nom, "réponse du juge illisible : " + ex.Message);
        }

        if (jugeDit.Length == 0) return Bareme.Indecis(Nom, "le juge n'a rendu aucun verdict");
        var cout = new CoutAppel(chrono.Elapsed);

        // Le doute est une étiquette de plein droit, sur le modèle du Doute de
        // SceneValidator : on ne force personne à trancher une question de goût.
        if (string.Equals(jugeDit, "doute", StringComparison.OrdinalIgnoreCase))
            return Bareme.Rendre(Nom, "douteux",
                $"le juge ne tranche pas, là où le code dit « {codeDit.Etiquette} ».", Preuve(raison), cout);

        var daccord = string.Equals(jugeDit, codeDit.Etiquette, StringComparison.OrdinalIgnoreCase);
        return Bareme.Rendre(Nom, daccord ? "accord" : "desaccord",
            daccord
                ? $"le juge confirme le « {codeDit.Etiquette} » de « {reference.Nom} »."
                : $"le juge dit « {jugeDit} » là où « {reference.Nom} » dit « {codeDit.Etiquette} ».",
            Preuve(raison), cout);
    }

    /// <summary>
    /// Une seule contrainte par appel, citée mot pour mot depuis l'évaluateur
    /// de référence. Demander un avis global rendrait le désaccord illisible :
    /// on ne saurait pas sur quoi il porte.
    /// </summary>
    private string Instruction(ReecritureObservee vue) => $"""
        Tu juges la réécriture d'un prompt contre UNE contrainte, et rien d'autre.
        Ne juge ni le style, ni l'intérêt de la demande.

        La contrainte : {reference.Contrainte}

        Le prompt d'origine, dont la réécriture est partie :
        <original>
        {vue.Original}
        </original>

        Le titre de la tâche d'où il vient : « {vue.Titre} »

        La réécriture à juger :
        <reecriture>
        {vue.Reecriture}
        </reecriture>

        Réponds « conforme » si la contrainte est respectée, « non_conforme » si elle
        est violée, « doute » si trancher demanderait de l'arbitraire. Dans « raison »,
        cite le fragment exact qui te fait dire cela, en français, en une phrase.
        """;

    private static string Preuve(string raison)
        => raison.Length > 0 ? raison : "le juge n'a pas motivé sa réponse";

    private static string Chaine(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
}
