using System.Text;
using CoachingIA.Harness.Core.Evaluation.Evaluateurs;

namespace CoachingIA.Harness.Core.Evaluation;

/// <summary>
/// La composition de la campagne : qui produit, qui évalue.
///
/// Elle vit ici et nulle part ailleurs. La porte du harnais et la commande
/// <c>coachingia evaluer</c> la prennent au même endroit — deux listes
/// recopiées auraient divergé au premier évaluateur ajouté, et la commande
/// aurait mesuré autre chose que ce que la porte garde.
/// </summary>
public static class CampagneStandard
{
    public static IReadOnlyList<IProducteur> Producteurs(string lensDir) =>
    [
        new ProducteurSegmentation(),
        new ProducteurBilan(lensDir),
        new ProducteurReecriture(),
        new ProducteurScene(lensDir),
    ];

    /// <summary>
    /// Les évaluateurs de la porte : déterministes, hors ligne, en
    /// millisecondes. Ce sont les seuls qui entrent dans l'état approuvé.
    /// </summary>
    public static IReadOnlyList<IEvaluateur> Porte() =>
    [
        new EvaluateurFrontieres(),
        new EvaluateurMotifs(),
        new ObservationAvecExemple(),
        new PasDeFelicitationPolie(),
        new FaitAvantImage(),
        new ImageSeulementSiDefaut(),
        new LentilleNeMesureRien(),
        new LentilleNeutreComplete(),
        new TroisObservationsAuPlus(),
        new BilanReproductible(),
        new SansInvention(),
        new LongueurBornee(),
        new ResteFrancais(),
        new SansAdresseDirecte(),
        new PromptPasExplication(),
        new CouvertureRubrique(),
        new SceneRegleAttendue(),
        new PackSansErreur(),
    ];

    /// <summary>
    /// Ceux qui demandent un avis à <c>claude -p</c>. Jamais dans la porte :
    /// ils coûtent un appel, ils changent d'avis, et ce qu'ils mesurent n'est
    /// pas le composant mais l'accord entre le composant et un tiers.
    /// </summary>
    public static IReadOnlyList<IEvaluateur> Juges(IClaudeCli cli) =>
    [
        new JugeReecriture(cli, new SansInvention()),
    ];
}

/// <summary>Ce que la porte a conclu, et le code de sortie qui va avec.</summary>
/// <param name="Code">0 rien n'a bougé, 1 un écart à approuver ou à corriger, 2 la campagne n'a pas pu conclure.</param>
public sealed record RapportPorte(int Code, IReadOnlyList<Ecart> Ecarts, IReadOnlyList<string> Alertes);

/// <summary>
/// La porte : comparer la campagne à l'état approuvé, et en tirer un code de
/// sortie que la CI comme un humain lisent de la même façon.
///
/// <para>Le 2 n'est pas décoratif. Une campagne qui n'a rien mesuré — jeu
/// absent, état approuvé absent, fichier illisible — ne doit surtout pas rendre
/// 0 : une porte verte parce qu'aucun évaluateur ne s'est appliqué est pire
/// qu'une porte rouge, puisqu'elle rassure.</para>
/// </summary>
public static class Porte
{
    public static RapportPorte Juger(
        ResultatCampagne resultat,
        IReadOnlyDictionary<string, string> approuve,
        IReadOnlyList<IEvaluateur> evaluateurs)
    {
        var alertes = new List<string>(resultat.Avertissements);
        var deterministes = evaluateurs.Where(e => e.Deterministe).Select(e => e.Nom).ToHashSet(StringComparer.Ordinal);
        var mesures = resultat.Verdicts.Count(l => deterministes.Contains(l.Verdict.Evaluateur));

        if (mesures == 0) alertes.Add("aucun verdict déterministe : la campagne n'a rien mesuré");
        if (approuve.Count == 0) alertes.Add("aucun état approuvé : il n'y a rien à quoi comparer");

        var ecarts = VerdictApprouve.Comparer(approuve, resultat, evaluateurs);
        var code = alertes.Count > 0 ? 2 : ecarts.Count > 0 ? 1 : 0;
        return new RapportPorte(code, ecarts, alertes);
    }

    /// <summary>Le rapport lisible. Ce qui ne passe pas dit pourquoi, ici comme dans le harnais.</summary>
    public static string Rendre(ResultatCampagne resultat, RapportPorte rapport)
    {
        var b = new StringBuilder();

        foreach (var a in rapport.Alertes) b.AppendLine("  ⚠ " + a);
        if (rapport.Alertes.Count > 0) b.AppendLine();

        b.AppendLine($"  Jeu {resultat.EmpreinteJeu} — {resultat.Verdicts.Count} verdict(s), "
                   + $"{resultat.Echoues} échec(s), {resultat.Indecis} indécis");
        b.AppendLine();

        foreach (var r in resultat.ParEvaluateur())
            b.AppendLine($"    {r.Nom,-28} {r.Reussis}/{r.Epreuves} réussis, {r.Echoues} échec(s), {r.Indecis} indécis");

        if (rapport.Ecarts.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("  Ce qui a bougé depuis l'état approuvé");
            foreach (var e in rapport.Ecarts)
                b.AppendLine($"    {e.Sens,-11} {e.Epreuve} / {e.Evaluateur} — {e.Avant} → {e.Apres}");
        }

        var aExpliquer = resultat.Verdicts.Where(l => !l.Verdict.Reussi).ToList();
        if (aExpliquer.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("  Ce qui ne passe pas, et pourquoi");
            foreach (var l in aExpliquer.OrderBy(l => l.Epreuve, StringComparer.Ordinal))
                b.AppendLine($"    {l.Epreuve}/{l.Verdict.Evaluateur} — {l.Verdict.Explication}");
        }

        foreach (var d in resultat.Diagnostics.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            b.AppendLine($"    {d.Key} : {d.Value}");

        return b.ToString();
    }
}
