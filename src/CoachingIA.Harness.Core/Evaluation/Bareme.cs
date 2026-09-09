namespace CoachingIA.Harness.Core.Evaluation;

/// <summary>
/// Un barème : la liste close des réponses qu'un évaluateur a le droit de
/// donner, et celles qui valent réussite.
///
/// Fermer la liste est la seule chose qui rende deux campagnes comparables. Un
/// évaluateur qui rend un score libre invite à déplacer la barre après coup —
/// ce qui est exactement la façon dont on se ment à soi-même sur un progrès.
///
/// Une étiquette hors barème ne lève pas : elle produit un verdict indécis qui
/// dit ce qu'il a reçu. Même posture que le corpus de maturité, qui se signale
/// et cède la place au repli plutôt que de faire tomber l'outil.
/// </summary>
public sealed record Bareme(string Id, IReadOnlyList<string> Etiquettes, IReadOnlyList<string> Reussites)
{
    /// <summary>L'étiquette de l'évaluateur qui s'applique mais ne sait pas trancher.</summary>
    public const string Indeterminable = "indeterminable";

    public bool Connait(string etiquette)
        => Etiquettes.Contains(etiquette, StringComparer.OrdinalIgnoreCase);

    /// <summary>1 pour une réussite, 0 pour un échec, NaN pour une indécision.</summary>
    public double ScoreDe(string etiquette)
    {
        if (!Connait(etiquette)) return double.NaN;
        if (string.Equals(etiquette, Indeterminable, StringComparison.OrdinalIgnoreCase)) return double.NaN;
        return Reussites.Contains(etiquette, StringComparer.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    /// <summary>
    /// Fabrique le verdict. Le score est dérivé du barème, jamais saisi :
    /// personne n'écrit « 0,7 ».
    /// </summary>
    public Verdict Rendre(string evaluateur, string etiquette, string explication, string? preuve = null, CoutAppel? cout = null)
    {
        if (!Connait(etiquette))
            return new Verdict(evaluateur, Id, Indeterminable, double.NaN,
                $"étiquette hors barème « {etiquette} » : le barème {Id} n'accepte que {string.Join(", ", Etiquettes)}.",
                preuve, cout);

        return new Verdict(evaluateur, Id, etiquette, ScoreDe(etiquette), explication, preuve, cout);
    }

    /// <summary>L'évaluateur s'applique mais ne peut pas conclure. Ce n'est pas un échec.</summary>
    public Verdict Indecis(string evaluateur, string explication, string? preuve = null)
        => new(evaluateur, Id, Indeterminable, double.NaN, explication, preuve);

    /// <summary>Le barème est bien formé : deux étiquettes au moins, des réussites qui en font partie.</summary>
    public bool EstCoherent(out string pourquoi)
    {
        if (Etiquettes.Count < 2) { pourquoi = $"le barème {Id} n'a que {Etiquettes.Count} étiquette(s)"; return false; }
        if (Reussites.Count == 0) { pourquoi = $"le barème {Id} n'a aucune étiquette de réussite"; return false; }
        foreach (var r in Reussites)
            if (!Connait(r)) { pourquoi = $"le barème {Id} donne « {r} » comme réussite sans la lister"; return false; }
        if (!Connait(Indeterminable)) { pourquoi = $"le barème {Id} ne prévoit pas l'indécision"; return false; }
        pourquoi = "";
        return true;
    }
}

/// <summary>
/// Les barèmes du projet. Immuables et sans état global mutable, contrairement
/// à <c>SignalSpecs</c> : une suite de tests ne doit pas pouvoir changer la
/// grille sous les pieds de la suivante.
///
/// Trois suffisent. On ne généralise pas ceci en système d'extensions tant que
/// trois ne suffisent plus.
/// </summary>
public static class Baremes
{
    /// <summary>Une contrainte énoncée est-elle respectée ? Question factuelle.</summary>
    public static readonly Bareme Conformite =
        new("conformite", ["conforme", "non_conforme", Bareme.Indeterminable], ["conforme"]);

    /// <summary>Une promesse écrite du produit est-elle tenue par le code ?</summary>
    public static readonly Bareme Promesse =
        new("promesse", ["tenue", "rompue", Bareme.Indeterminable], ["tenue"]);

    /// <summary>
    /// Le juge est-il d'accord avec la référence de code ? Le doute est une
    /// étiquette de plein droit, sur le modèle du <c>Doute</c> de SceneValidator :
    /// on refuse de bloquer sur une question de goût.
    /// </summary>
    public static readonly Bareme Accord =
        new("accord", ["accord", "desaccord", "douteux", Bareme.Indeterminable], ["accord"]);

    public static IReadOnlyList<Bareme> Tous { get; } = [Conformite, Promesse, Accord];

    public static Bareme? Trouver(string id)
        => Tous.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
}
