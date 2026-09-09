using System.Text.Json;
using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Core.Evaluation.Evaluateurs;

/// <summary>Ce que le validateur a dit d'une scène, ou d'un pack entier.</summary>
public sealed record SceneObservee(IReadOnlyList<SceneReport> Rapports, bool EstPack, string Quoi);

/// <summary>
/// Soumet une scène — ou un pack de lentille entier — au <see cref="SceneValidator"/>.
///
/// <para><strong>On enveloppe, on ne réécrit rien.</strong> Le validateur porte
/// déjà six règles nommées, un rail à deux niveaux (erreur / doute) et une
/// explication qui cite le texte fautif : c'est un évaluateur de code complet,
/// écrit avant que la brique d'évaluation n'existe. Ce qui manquait n'était pas
/// un évaluateur, c'était la <em>mesure du taux</em> — combien de scènes
/// passent, et lesquelles ne passent plus après un changement.</para>
///
/// <para>Deux formes d'épreuve : une scène écrite à la main (« cette scène
/// doit déclencher telle règle, et elle seule »), ou le pack réellement livré
/// (« starcraft2.json ne doit contenir aucune erreur »). La seconde mesure le
/// contenu qui part chez l'apprenant, pas un échantillon de laboratoire.</para>
/// </summary>
public sealed class ProducteurScene(string lensDir) : IProducteur
{
    public string Famille => "scene";

    public Production Produire(Epreuve epreuve)
    {
        if (epreuve.Entree.ValueKind != JsonValueKind.Object)
            return Panne(epreuve, "l'épreuve n'a pas d'entrée");

        var lentille = Chaine(epreuve.Entree, "pack");
        var corpusId = lentille.Length > 0 ? lentille : Chaine(epreuve.Entree, "corpus");
        if (corpusId.Length == 0) corpusId = "starcraft2";

        var corpus = Corpus.Load(lensDir, corpusId);
        if (corpus is null || corpus.IsEmpty)
            return Panne(epreuve, "le corpus « " + corpusId + " » est introuvable ou vide sous " + lensDir);

        var validateur = new SceneValidator(corpus);

        if (lentille.Length > 0)
        {
            var lens = LensCatalog.Load(lensDir).Resolve(lentille);
            var rapports = validateur.CheckLens(lens);
            return new Production(
                rapports.Count + " scène(s) relues dans le pack " + lentille, "validateur",
                Sujet: new SceneObservee(rapports, true, "le pack " + lentille));
        }

        var texte = Chaine(epreuve.Entree, "texte");
        if (texte.Length == 0) return Panne(epreuve, "l'épreuve ne porte ni « pack » ni « texte »");

        var race = Chaine(epreuve.Entree, "race");
        var cle = Chaine(epreuve.Entree, "cle");
        var rapport = validateur.Check(cle.Length > 0 ? cle : epreuve.Id, race.Length > 0 ? race : null, texte);

        return new Production(texte, "validateur",
            Sujet: new SceneObservee([rapport], false, "la scène"));
    }

    private static Production Panne(Epreuve epreuve, string pourquoi)
        => new("épreuve " + epreuve.Id + " non jouable", "validateur") { Panne = pourquoi };

    private static string Chaine(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>
/// La scène déclenche-t-elle exactement les règles attendues ?
///
/// Comparer l'ensemble des règles, et non leur seul nombre, est ce qui rend
/// l'épreuve utile : une scène qui déclenche « sans ancrage » là où on attendait
/// « mauvais camp » signale que le validateur a cessé d'attraper ce qu'il
/// prétend attraper, alors qu'un simple compte dirait que tout va bien.
///
/// Une liste attendue vide veut dire « cette scène doit passer sans un mot ».
/// </summary>
public sealed class SceneRegleAttendue : IEvaluateur
{
    public string Nom => "scene_regle_attendue";
    public string Famille => "scene";
    public Bareme Bareme => Baremes.Conformite;

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        if (epreuve.Attendu.ValueKind != JsonValueKind.Object) return null;
        if (!epreuve.Attendu.TryGetProperty("regles", out var r) || r.ValueKind != JsonValueKind.Array) return null;
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not SceneObservee vue || vue.EstPack) return null;

        var attendues = r.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        var obtenues = vue.Rapports.SelectMany(x => x.Issues)
            .Select(i => i.Rule)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (attendues.SequenceEqual(obtenues, StringComparer.Ordinal))
            return Bareme.Rendre(Nom, "conforme",
                attendues.Count == 0
                    ? "la scène passe le validateur sans un reproche."
                    : "la scène déclenche exactement la ou les règles attendues : " + string.Join(", ", attendues) + ".");

        var manquantes = attendues.Except(obtenues, StringComparer.Ordinal).ToList();
        var superflues = obtenues.Except(attendues, StringComparer.Ordinal).ToList();

        var detail = vue.Rapports.SelectMany(x => x.Issues).FirstOrDefault();
        return Bareme.Rendre(Nom, "non_conforme",
            "le validateur ne dit pas ce que la scène était censée lui faire dire"
            + (manquantes.Count > 0 ? " (règle(s) non déclenchée(s) : " + string.Join(", ", manquantes) + ")" : "")
            + (superflues.Count > 0 ? " (règle(s) en trop : " + string.Join(", ", superflues) + ")" : "") + ".",
            detail is null
                ? "attendu [" + string.Join(", ", attendues) + "], aucun reproche obtenu"
                : detail.ToString());
    }
}

/// <summary>
/// Le pack livré ne contient aucune erreur.
///
/// Les doutes sont comptés et affichés, jamais bloquants : le validateur
/// lui-même refuse de bloquer un pack sur une question de goût, et un
/// évaluateur qui durcirait cette position par-dessus son dos réécrirait la
/// règle au lieu de la mesurer.
/// </summary>
public sealed class PackSansErreur : IEvaluateur
{
    public string Nom => "pack_sans_erreur";
    public string Famille => "scene";
    public Bareme Bareme => Baremes.Conformite;

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not SceneObservee vue || !vue.EstPack) return null;

        var erreurs = vue.Rapports.SelectMany(r => r.Issues.Where(i => i.Level == SceneIssueLevel.Erreur)).ToList();
        var doutes = vue.Rapports.Sum(r => r.Issues.Count(i => i.Level == SceneIssueLevel.Doute));
        var propres = vue.Rapports.Count(r => r.IsClean);

        var chiffres = vue.Rapports.Count + " scène(s), " + propres + " sans un reproche, "
                     + erreurs.Count + " erreur(s), " + doutes + " doute(s)";

        if (erreurs.Count == 0)
            return Bareme.Rendre(Nom, "conforme", vue.Quoi + " ne porte aucune erreur (" + chiffres + ").");

        var premiere = vue.Rapports.First(r => r.Issues.Any(i => i.Level == SceneIssueLevel.Erreur));
        var faute = premiere.Issues.First(i => i.Level == SceneIssueLevel.Erreur);

        return Bareme.Rendre(Nom, "non_conforme",
            vue.Quoi + " porte des erreurs (" + chiffres + ").",
            "[" + premiere.Key + (premiere.Race is null ? "" : "/" + premiere.Race) + "] " + faute);
    }
}
