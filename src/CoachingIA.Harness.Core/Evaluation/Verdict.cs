namespace CoachingIA.Harness.Core.Evaluation;

/// <summary>
/// Ce qu'un appel a coûté, pour autant que le CLI veuille bien le dire.
///
/// Tous les champs sont facultatifs sauf le temps au mur, qui est le seul
/// toujours mesurable. Et une règle qui ne se négocie pas : <strong>le coût
/// n'est jamais un critère de réussite</strong>. Il est enregistré, il est
/// affiché, il n'entre dans aucun barème. Un projet qui a écrit « ni points,
/// ni badges » ne doit pas se mettre à optimiser des millisecondes sur un
/// outil qui tourne une fois par semaine.
/// </summary>
public sealed record CoutAppel(
    TimeSpan Mur,
    TimeSpan? Api = null,
    long? JetonsEntree = null,
    long? JetonsSortie = null,
    double? Dollars = null,
    int? Tours = null);

/// <summary>
/// Ce qu'un évaluateur a répondu, et pourquoi.
///
/// L'étiquette seule ne vaut rien : six mois plus tard, « non conforme » sans
/// la phrase qui le montre est un chiffre qu'on ne peut ni contester ni
/// corriger. C'est la même règle que pour une observation de bilan — aucun
/// constat sans exemple.
///
/// <c>Explication</c> et <c>Preuve</c> sont séparés là où <c>SceneIssue</c> les
/// fusionne : l'explication est ce que l'évaluateur a conclu, la preuve est le
/// fragment incriminé, cité mot pour mot pour pouvoir être surligné.
///
/// Un verdict d'évaluation ne parle <strong>jamais</strong> à l'apprenant. Il
/// parle à celui qui maintient l'outil. Aucun verdict n'a sa place dans un
/// bilan ni dans une rétrospective.
/// </summary>
public sealed record Verdict(
    string Evaluateur,
    string Bareme,
    string Etiquette,
    double Score,
    string Explication,
    string? Preuve = null,
    CoutAppel? Cout = null)
{
    public bool Reussi => Score >= 1.0;

    /// <summary>
    /// L'évaluateur s'appliquait mais n'a pas su trancher. Une indécision n'est
    /// pas un échec : elle sort du numérateur sans sortir du dénominateur, et
    /// se compte à part. C'est le pendant exact du NaN d'un signal.
    /// </summary>
    public bool Indecis => double.IsNaN(Score);
}
