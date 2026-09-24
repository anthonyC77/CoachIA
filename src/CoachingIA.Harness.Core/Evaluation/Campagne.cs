using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace CoachingIA.Harness.Core.Evaluation;

/// <summary>Un verdict, rattaché à l'épreuve qui l'a produit.</summary>
public sealed record LigneVerdict(string Epreuve, string Famille, Verdict Verdict)
{
    /// <summary>La clé stable d'un verdict dans le temps. C'est elle qui se compare d'une campagne à l'autre.</summary>
    public string Cle => Epreuve + "|" + Verdict.Evaluateur;
}

/// <summary>
/// Ce qu'un évaluateur a donné sur l'ensemble du jeu.
///
/// Les indécis sont comptés <strong>à part</strong>, jamais fondus dans les
/// échecs : un évaluateur qui n'a pas pu conclure n'a pas constaté de défaut.
/// Les confondre transformerait une panne de juge en mauvaise note.
/// </summary>
public sealed record ResumeEvaluateur(string Nom, int Epreuves, int Reussis, int Echoues, int Indecis)
{
    /// <summary>Le taux, sur les seules épreuves tranchées. NaN si aucune ne l'a été.</summary>
    public double Taux => Reussis + Echoues == 0 ? double.NaN : (double)Reussis / (Reussis + Echoues);
}

/// <summary>Un écart entre le verdict approuvé et celui de la campagne.</summary>
public sealed record Ecart(string Epreuve, string Evaluateur, string Avant, string Apres, string Sens);

/// <summary>Le résultat d'une campagne : les verdicts, et de quoi les lire.</summary>
public sealed class ResultatCampagne
{
    public required string EmpreinteJeu { get; init; }
    public List<LigneVerdict> Verdicts { get; } = [];
    public List<string> Avertissements { get; } = [];

    /// <summary>
    /// Ce que chaque épreuve a produit, par identifiant — exactement l'objet que
    /// les évaluateurs ont jugé. Le garder évite à qui publie la campagne de
    /// rejouer les producteurs : un rejeu ne rendrait la même sortie que tant
    /// qu'ils restent déterministes, et recopierait leur gestion de panne.
    /// Absent de <see cref="Campagne.Serialiser"/> : l'état approuvé ne porte
    /// que des étiquettes.
    /// </summary>
    public Dictionary<string, Production> Productions { get; } = new(StringComparer.Ordinal);

    /// <summary>Les diagnostics chiffrés qui ne sont pas des scores : Pk, répartition des motifs…</summary>
    public Dictionary<string, string> Diagnostics { get; } = [];

    public IReadOnlyList<ResumeEvaluateur> ParEvaluateur()
        => [.. Verdicts
            .GroupBy(l => l.Verdict.Evaluateur, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ResumeEvaluateur(
                g.Key,
                g.Count(),
                g.Count(l => l.Verdict.Reussi),
                g.Count(l => !l.Verdict.Reussi && !l.Verdict.Indecis),
                g.Count(l => l.Verdict.Indecis)))];

    public int Indecis => Verdicts.Count(l => l.Verdict.Indecis);
    public int Echoues => Verdicts.Count(l => !l.Verdict.Reussi && !l.Verdict.Indecis);
}

/// <summary>
/// Joue un jeu d'épreuves : chaque épreuve est produite par le producteur de sa
/// famille, puis soumise à tous les évaluateurs de cette famille.
///
/// Séquentielle et sans parallélisme, volontairement. Le jeu tient en quelques
/// dizaines de cas et tourne en millisecondes ; paralléliser n'apporterait que
/// des résultats dont l'ordre varie, donc un fichier de verdict qui bouge sans
/// que rien n'ait changé.
/// </summary>
public sealed class Campagne
{
    private readonly Dictionary<string, IProducteur> _producteurs;
    private readonly List<IEvaluateur> _evaluateurs;

    public Campagne(IEnumerable<IProducteur> producteurs, IEnumerable<IEvaluateur> evaluateurs)
    {
        _producteurs = producteurs.ToDictionary(p => p.Famille, StringComparer.Ordinal);
        _evaluateurs = [.. evaluateurs];
    }

    public ResultatCampagne Jouer(JeuEpreuves jeu)
    {
        var resultat = new ResultatCampagne { EmpreinteJeu = jeu.Empreinte };
        resultat.Avertissements.AddRange(jeu.Avertissements);

        foreach (var epreuve in jeu.Epreuves.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!_producteurs.TryGetValue(epreuve.Famille, out var producteur))
            {
                resultat.Avertissements.Add(
                    "aucun producteur pour la famille « " + epreuve.Famille + " » (épreuve " + epreuve.Id + ")");
                continue;
            }

            Production production;
            try
            {
                production = producteur.Produire(epreuve);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Une épreuve qui fait lever le producteur ne doit pas emporter
                // la campagne : on l'enregistre comme panne et on continue.
                production = new Production("", producteur.Famille)
                {
                    Panne = ex.GetType().Name + " : " + ex.Message,
                };
            }
            resultat.Productions[epreuve.Id] = production;

            foreach (var evaluateur in _evaluateurs.Where(e => e.Famille == epreuve.Famille))
            {
                var verdict = evaluateur.Evaluer(epreuve, production);
                if (verdict is null) continue;      // hors de son ressort : hors du dénominateur
                resultat.Verdicts.Add(new LigneVerdict(epreuve.Id, epreuve.Famille, verdict));
            }
        }

        return resultat;
    }
}

/// <summary>
/// L'état approuvé des évaluateurs déterministes, versionné dans le dépôt.
///
/// <para>Une porte à seuil flottant (« au moins 85 % ») se négocie à la baisse
/// le jour où elle gêne. Une porte qui compare au verdict approuvé ne se
/// négocie pas : elle montre ce qui a bougé, dans les deux sens. Une épreuve
/// qui se met à passer fait échouer aussi — il faut alors approuver le progrès,
/// ce qui laisse une trace dans <c>git diff</c>.</para>
///
/// <para>Seuls les évaluateurs déterministes y entrent. Y laisser une sortie de
/// juge ferait bouger le fichier à chaque exécution, et <c>ReviewArchive</c> se
/// remettrait à tourner : la précaution retournée contre elle-même.</para>
/// </summary>
public static class VerdictApprouve
{
    private const string Note =
        "État approuvé des évaluateurs déterministes. Un écart fait échouer la porte, "
        + "dans les deux sens : une régression comme un progrès non approuvé.";

    public static string Serialiser(ResultatCampagne resultat, IReadOnlyList<IEvaluateur> evaluateurs)
    {
        var deterministes = evaluateurs
            .Where(e => e.Deterministe)
            .Select(e => e.Nom)
            .ToHashSet(StringComparer.Ordinal);

        var verdicts = new JsonObject();
        foreach (var ligne in resultat.Verdicts
                     .Where(l => deterministes.Contains(l.Verdict.Evaluateur))
                     .OrderBy(l => l.Cle, StringComparer.Ordinal))
            verdicts[ligne.Cle] = ligne.Verdict.Etiquette;

        var racine = new JsonObject
        {
            ["//"] = Note,
            ["empreinte_jeu"] = resultat.EmpreinteJeu,
            ["verdicts"] = verdicts,
        };

        // Les accents s'écrivent en clair : ce fichier n'existe que pour être lu
        // dans un « git diff ». Une régression déguisée en « é » ne se
        // repère pas d'un coup d'œil, et c'est tout ce qu'on lui demande.
        return racine.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        }) + "\n";
    }

    /// <summary>Lit l'état approuvé. Un fichier absent rend un état vide plutôt qu'une erreur.</summary>
    public static Dictionary<string, string> Lire(string chemin, List<string> avertissements)
    {
        var etat = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(chemin))
        {
            avertissements.Add("aucun verdict approuvé sous " + chemin + " : la campagne ne peut rien comparer");
            return etat;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(chemin));
            if (doc.RootElement.TryGetProperty("verdicts", out var v) && v.ValueKind == JsonValueKind.Object)
                foreach (var p in v.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        etat[p.Name] = p.Value.GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            avertissements.Add("verdict approuvé illisible (" + ex.Message + ")");
        }

        return etat;
    }

    /// <summary>
    /// Ce qui a bougé. Les indécis sont ignorés : une panne de juge n'est pas un
    /// écart de comportement, et la faire échouer transformerait une coupure
    /// réseau en régression.
    /// </summary>
    public static List<Ecart> Comparer(
        IReadOnlyDictionary<string, string> approuve, ResultatCampagne courant, IReadOnlyList<IEvaluateur> evaluateurs)
    {
        var deterministes = evaluateurs.Where(e => e.Deterministe).Select(e => e.Nom).ToHashSet(StringComparer.Ordinal);
        var reussites = evaluateurs.ToDictionary(e => e.Nom, e => e.Bareme, StringComparer.Ordinal);
        var ecarts = new List<Ecart>();
        var vus = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ligne in courant.Verdicts
                     .Where(l => deterministes.Contains(l.Verdict.Evaluateur))
                     .OrderBy(l => l.Cle, StringComparer.Ordinal))
        {
            vus.Add(ligne.Cle);
            if (ligne.Verdict.Indecis) continue;
            if (!approuve.TryGetValue(ligne.Cle, out var avant))
            {
                ecarts.Add(new Ecart(ligne.Epreuve, ligne.Verdict.Evaluateur, "—", ligne.Verdict.Etiquette, "nouveau"));
                continue;
            }
            if (string.Equals(avant, ligne.Verdict.Etiquette, StringComparison.Ordinal)) continue;

            var bareme = reussites.GetValueOrDefault(ligne.Verdict.Evaluateur);
            var etaitReussi = bareme is not null && bareme.ScoreDe(avant) >= 1.0;
            ecarts.Add(new Ecart(ligne.Epreuve, ligne.Verdict.Evaluateur, avant, ligne.Verdict.Etiquette,
                etaitReussi ? "régression" : "progrès"));
        }

        foreach (var (cle, avant) in approuve.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (vus.Contains(cle)) continue;
            var morceaux = cle.Split('|');
            ecarts.Add(new Ecart(morceaux[0], morceaux.Length > 1 ? morceaux[1] : "?", avant, "—", "disparu"));
        }

        return ecarts;
    }
}
