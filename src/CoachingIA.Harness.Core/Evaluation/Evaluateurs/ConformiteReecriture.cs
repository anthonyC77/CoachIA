using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoachingIA.Harness.Core.Evaluation.Evaluateurs;

/// <summary>Une réécriture enregistrée, et ce dont elle est partie.</summary>
public sealed record ReecritureObservee(string Original, string Titre, string Reecriture);

/// <summary>
/// Rejoue une réécriture <strong>enregistrée</strong> dans le jeu — jamais une
/// réécriture produite à l'instant.
///
/// C'est ce qui permet de mesurer le respect des contraintes sans dépendre de
/// la disponibilité du juge qui les a produites, et de rejouer exactement le
/// même cas dans six mois. Un évaluateur qui rappellerait `claude -p` à chaque
/// passage mesurerait un texte différent à chaque fois, donc ne mesurerait rien.
/// </summary>
public sealed class ProducteurReecriture : IProducteur
{
    public string Famille => "reecriture";

    public Production Produire(Epreuve epreuve)
    {
        if (epreuve.Entree.ValueKind != JsonValueKind.Object)
            return Panne(epreuve, "l'épreuve n'a pas d'entrée");

        var original = Chaine(epreuve.Entree, "original");
        var reecriture = Chaine(epreuve.Entree, "reecriture");
        if (original.Length == 0) return Panne(epreuve, "l'épreuve ne porte pas de prompt d'origine");
        if (reecriture.Length == 0) return Panne(epreuve, "l'épreuve ne porte pas de réécriture");

        return new Production(reecriture, Chaine(epreuve.Entree, "source"),
            Sujet: new ReecritureObservee(original, Chaine(epreuve.Entree, "titre"), reecriture));
    }

    private static Production Panne(Epreuve epreuve, string pourquoi)
        => new("épreuve " + epreuve.Id + " non jouable", "reecriture") { Panne = pourquoi };

    private static string Chaine(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>
/// Base des évaluateurs qui vérifient une contrainte que le projet <em>écrit
/// lui-même</em> dans son instruction de réécriture.
///
/// Chaque contrainte cite sa source dans son commentaire. Une contrainte écrite
/// dans le prompt et jamais mesurée est décorative : elle donne l'illusion d'un
/// garde-fou sans en être un.
/// </summary>
public abstract class ContrainteReecriture : IEvaluateur
{
    public abstract string Nom { get; }
    public string Famille => "reecriture";
    public Bareme Bareme => Baremes.Conformite;

    /// <summary>
    /// La contrainte, telle qu'elle est écrite dans l'instruction de réécriture.
    /// Le juge la reprend mot pour mot : lui reformuler la question ferait
    /// mesurer un accord sur autre chose que ce que le code mesure.
    /// </summary>
    public abstract string Contrainte { get; }

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not ReecritureObservee vue) return null;
        return Juger(vue);
    }

    protected abstract Verdict? Juger(ReecritureObservee vue);

    protected Verdict Conforme(string explication) => Bareme.Rendre(Nom, "conforme", explication);
    protected Verdict NonConforme(string explication, string preuve)
        => Bareme.Rendre(Nom, "non_conforme", explication, preuve);
}

/// <summary>
/// PromptCritic.cs:144 — « n'invente aucun détail technique absent du prompt
/// d'origine ou de son titre ; si une information manque, écris un emplacement
/// entre crochets ».
///
/// <para>La vérification est un <strong>confinement de jetons</strong> : chaque
/// nom de fichier, chaque identifiant en casse chameau et chaque nombre de la
/// réécriture doit déjà exister dans l'original ou dans le titre. Ce qui vit
/// entre crochets est exempt — c'est précisément l'échappatoire que
/// l'instruction offre au modèle pour dire « je ne sais pas ».</para>
///
/// <para>C'est la contrainte la mieux vérifiable des cinq, et de loin la plus
/// importante : un prompt de coaching qui invente un nom de fichier envoie
/// l'apprenant travailler sur quelque chose qui n'existe pas.</para>
/// </summary>
public sealed class SansInvention : ContrainteReecriture
{
    public override string Nom => "sans_invention";
    public override string Contrainte => "n'invente aucun détail technique absent du prompt d'origine ou de son titre ; si une information manque, écris un emplacement entre crochets.";

    /// <summary>Ce qui vit entre crochets est un emplacement à remplir, pas une invention.</summary>
    private static readonly Regex Crochets = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    private static readonly Regex Fichiers = new(
        @"\b[\w\-/\.]+\.(?:cs|ts|js|py|json|html|scss|css|sql|md|yml|yaml|razor|xaml)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// La casse chameau seulement : elle exige une majuscule interne, ce qui
    /// écarte d'emblée tout mot français ouvrant une phrase. Chercher plus large
    /// ferait un évaluateur bavard, et un évaluateur bavard finit désactivé.
    /// </summary>
    private static readonly Regex Identifiants = new(
        @"\b[A-Z][a-z]+(?:[A-Z][A-Za-z0-9]*)+\b", RegexOptions.Compiled);

    private static readonly Regex Nombres = new(@"\b\d+\b", RegexOptions.Compiled);

    protected override Verdict? Juger(ReecritureObservee vue)
    {
        var source = vue.Original + " \n " + vue.Titre;
        var candidat = Crochets.Replace(vue.Reecriture, " ");

        foreach (var (nature, motif) in (( string, Regex )[])
                 [("nom de fichier", Fichiers), ("identifiant", Identifiants), ("nombre", Nombres)])
            foreach (Match m in motif.Matches(candidat))
            {
                if (source.Contains(m.Value, StringComparison.OrdinalIgnoreCase)) continue;
                return NonConforme(
                    "la réécriture introduit un " + nature + " absent de l'original et de son titre.",
                    m.Value);
            }

        return Conforme("aucun nom de fichier, identifiant ni nombre de la réécriture n'est absent de la source.");
    }
}

/// <summary>PromptCritic.cs:146 — « ne rallonge pas inutilement : vise deux fois la longueur d'origine au maximum ».</summary>
public sealed class LongueurBornee : ContrainteReecriture
{
    public override string Nom => "longueur_bornee";
    public override string Contrainte => "ne rallonge pas inutilement : vise deux fois la longueur d'origine au maximum.";

    protected override Verdict? Juger(ReecritureObservee vue)
    {
        if (vue.Original.Length == 0) return Bareme.Indecis(Nom, "l'original est vide : aucun rapport de longueur n'a de sens.");

        var plafond = vue.Original.Length * 2;
        var rapport = (double)vue.Reecriture.Length / vue.Original.Length;

        return vue.Reecriture.Length <= plafond
            ? Conforme("la réécriture fait " + rapport.ToString("0.0") + " fois l'original, sous le plafond de 2.")
            : NonConforme("la réécriture dépasse deux fois la longueur d'origine.",
                vue.Reecriture.Length + " caractères pour un plafond de " + plafond);
    }
}

/// <summary>
/// PromptCritic.cs:143 — « reste en français ».
///
/// Mesuré par comptage de mots-outils, pas par détection de langue : une
/// bibliothèque de détection serait une dépendance, et le Core n'en prend
/// aucune. Le comptage suffit largement pour trancher entre un prompt français
/// et un prompt qui a basculé en anglais.
/// </summary>
public sealed class ResteFrancais : ContrainteReecriture
{
    public override string Nom => "reste_francais";
    public override string Contrainte => "reste en français, dans le registre de l'auteur.";

    private static readonly Regex MotsFrancais = new(
        @"\b(le|la|les|de|des|du|un|une|et|dans|pour|que|qui|ne|pas|sur|avec|est|sont|aux|cette|ses|plus|tout|sans|par|elle)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MotsAnglais = new(
        @"\b(the|and|with|must|should|this|that|your|please|following|make|sure|will|have|been|from|when|which)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    protected override Verdict? Juger(ReecritureObservee vue)
    {
        var francais = MotsFrancais.Matches(vue.Reecriture).Count;
        var anglais = MotsAnglais.Matches(vue.Reecriture).Count;

        if (francais == 0 && anglais == 0)
            return Bareme.Indecis(Nom, "la réécriture est trop courte pour que le comptage tranche.");

        return francais > anglais
            ? Conforme("la réécriture reste en français (" + francais + " mots-outils français, " + anglais + " anglais).")
            : NonConforme("la réécriture a basculé hors du français.",
                anglais + " mots-outils anglais pour " + francais + " français");
    }
}

/// <summary>
/// PromptCritic.cs:143 — « sans le vouvoyer ni le tutoyer ».
///
/// On soustrait ce que l'original portait déjà : un apprenant qui se tutoie
/// lui-même dans son propre prompt n'a pas à voir sa réécriture pénalisée pour
/// l'avoir conservé. C'est l'<em>ajout</em> d'adresse directe qui rompt la
/// contrainte.
/// </summary>
public sealed class SansAdresseDirecte : ContrainteReecriture
{
    public override string Nom => "sans_adresse_directe";
    public override string Contrainte => "n'interpelle pas l'auteur : ni vouvoiement, ni tutoiement, si l'original n'en portait pas.";

    private static readonly Regex Adresse = new(
        @"\b(vous|votre|vos|tu|ton|ta|tes|toi)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    protected override Verdict? Juger(ReecritureObservee vue)
    {
        var apres = Adresse.Matches(vue.Reecriture).Count;
        var avant = Adresse.Matches(vue.Original).Count;

        if (apres <= avant)
            return Conforme("la réécriture n'ajoute aucune adresse directe (" + apres + " contre " + avant + " dans l'original).");

        var premier = Adresse.Matches(vue.Reecriture).Select(m => m.Value)
            .FirstOrDefault(v => !vue.Original.Contains(v, StringComparison.OrdinalIgnoreCase)) ?? "";

        return NonConforme("la réécriture s'adresse à l'auteur alors que l'original ne le faisait pas.",
            premier.Length > 0 ? "« " + premier + " »" : apres + " occurrences contre " + avant);
    }
}

/// <summary>
/// PromptCritic.cs:147 — « le résultat doit être un prompt prêt à copier, pas
/// une explication ».
///
/// Heuristique assumée : on regarde l'ouverture. Un texte qui commence par
/// « Voici le prompt réécrit » est un commentaire sur un prompt, pas un prompt.
/// C'est la seule des cinq contraintes que le code n'attrape qu'imparfaitement —
/// un juge la trancherait mieux, et c'est noté.
/// </summary>
public sealed class PromptPasExplication : ContrainteReecriture
{
    public override string Nom => "prompt_pas_explication";
    public override string Contrainte => "le résultat doit être un prompt prêt à copier, pas une explication de ce qu'il faudrait écrire.";

    private static readonly string[] Ouvertures =
    [
        "voici", "voilà", "ce prompt", "le prompt", "je propose", "je te propose",
        "il s'agit", "cette réécriture", "en résumé", "note :", "réécriture :",
    ];

    protected override Verdict? Juger(ReecritureObservee vue)
    {
        var premiere = vue.Reecriture
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";

        var bas = premiere.ToLowerInvariant();
        foreach (var ouverture in Ouvertures)
            if (bas.StartsWith(ouverture, StringComparison.Ordinal))
                return NonConforme("la réécriture commente le prompt au lieu d'en être un.",
                    premiere.Length <= 60 ? premiere : premiere[..60] + "…");

        return Conforme("la réécriture ouvre directement sur la demande, sans préambule.");
    }
}
