namespace CoachingIA.Harness.Core.Evaluation;

/// <summary>
/// Un évaluateur : une question fermée, posée à un cas, qui rend une étiquette
/// de son barème et la phrase qui la justifie.
///
/// Synchrone et sans jeton d'annulation, contrairement au modèle du cours
/// Phoenix : celui-ci parle à une API réseau, tout ici tourne en lot, sur un
/// poste, hors ligne. Un <c>Task</c> qui ne sert jamais est une contagion — il
/// remonterait jusqu'au CLI, qui est un script, et jusqu'au harnais de tests,
/// qui est une fermeture synchrone.
///
/// <c>Evaluer</c> rend <c>null</c> quand l'épreuve n'est pas de son ressort —
/// motif exact de <c>IPromptCritic.Critique</c>, qui rend null sur un prompt
/// déjà complet. Distinguer « ne s'applique pas » (hors du dénominateur) de
/// « s'applique mais je ne sais pas trancher » (indécis, dans le dénominateur)
/// est ce qui rend un taux lisible.
/// </summary>
public interface IEvaluateur
{
    string Nom { get; }
    Bareme Bareme { get; }
    string Famille { get; }
    Verdict? Evaluer(Epreuve epreuve, Production production);

    /// <summary>
    /// L'évaluateur rend-il toujours la même étiquette sur la même entrée ?
    ///
    /// Seuls les déterministes entrent dans le verdict approuvé et versionné :
    /// y laisser une sortie de juge ferait bouger le fichier à chaque
    /// exécution, et l'archivage se remettrait à tourner pour rien. Les
    /// évaluateurs adossés au juge redéfinissent ceci à <c>false</c>.
    /// </summary>
    bool Deterministe => true;
}

/// <summary>
/// Ce qui transforme une épreuve en production : la partie « exécuter le
/// composant » de la boucle, séparée de la partie « juger le résultat ».
///
/// Les séparer est ce qui permet de rejouer les mêmes évaluateurs sur une
/// production enregistrée plutôt que refabriquée — donc de mesurer une
/// réécriture sans dépendre de la disponibilité du juge qui l'a produite.
/// </summary>
public interface IProducteur
{
    string Famille { get; }
    Production Produire(Epreuve epreuve);
}
