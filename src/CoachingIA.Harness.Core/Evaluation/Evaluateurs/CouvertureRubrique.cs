using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Core.Evaluation.Evaluateurs;

/// <summary>
/// La réécriture couvre-t-elle mieux la grille du projet que l'original ?
///
/// <para>C'est la question « le juge mérite-t-il ses quatre-vingt-dix
/// secondes ? », posée avec la seule grille que le projet reconnaisse :
/// <see cref="PromptRubric"/>. Personne ne peut y répondre aujourd'hui, parce
/// que rien ne mesure ce que la réécriture apporte.</para>
///
/// <para><strong>Le barème sanctionne la perte, pas l'absence de gain.</strong>
/// La critique hors ligne pose des emplacements entre crochets — « Objectif :
/// [le verbe et l'objet précis de la demande] » — qui ne déclenchent aucun
/// indice de la grille, et c'est voulu : ils attendent la main de l'apprenant.
/// Les compter comme un échec reviendrait à reprocher à l'heuristique de faire
/// exactement ce qu'elle annonce faire. En revanche une réécriture qui
/// <em>perd</em> un critère que l'original portait a détruit de l'information,
/// et c'est un vrai défaut.</para>
///
/// <para>Le gain reste écrit dans l'explication de chaque verdict : c'est en
/// comparant ces écarts entre une réécriture heuristique et une réécriture
/// « claude -p » sur le même original qu'on répondra à la question de fond.
/// Un chiffre agrégé unique la masquerait.</para>
/// </summary>
public sealed class CouvertureRubrique : IEvaluateur
{
    public string Nom => "couverture_rubrique";
    public string Famille => "reecriture";
    public Bareme Bareme => Baremes.Conformite;

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not ReecritureObservee vue) return null;

        var avant = PromptRubric.Coverage(vue.Original);
        var apres = PromptRubric.Coverage(vue.Reecriture);
        var ecart = apres - avant;

        var chiffres = "couverture " + avant.ToString("0.00") + " → " + apres.ToString("0.00")
                     + " (écart " + (ecart >= 0 ? "+" : "") + ecart.ToString("0.00") + ")";

        if (apres >= avant)
        {
            var gagnes = PromptRubric.Present(vue.Reecriture)
                .Select(i => i.Key)
                .Except(PromptRubric.Present(vue.Original).Select(i => i.Key), StringComparer.Ordinal)
                .ToList();

            return Bareme.Rendre(Nom, "conforme",
                gagnes.Count == 0
                    ? "la réécriture ne perd aucun critère de la grille (" + chiffres + ")."
                    : "la réécriture gagne " + string.Join(", ", gagnes) + " (" + chiffres + ").");
        }

        var perdus = PromptRubric.Present(vue.Original)
            .Select(i => i.Key)
            .Except(PromptRubric.Present(vue.Reecriture).Select(i => i.Key), StringComparer.Ordinal)
            .ToList();

        return Bareme.Rendre(Nom, "non_conforme",
            "la réécriture perd un critère que l'original portait (" + chiffres + ").",
            perdus.Count > 0 ? string.Join(", ", perdus) : chiffres);
    }
}
