namespace CoachingIA.Orchestration;

// Ces types transitent dans l'historique d'exécution de Temporal (visibles
// dans son UI, journalisés à chaque étape du workflow). Ils ne portent donc
// que des chemins, des identifiants et des compteurs — jamais un texte de
// prompt ni un contenu de transcript. Voir docs/decision-temporal.md pour la
// règle de vie privée complète et le déterminisme attendu des workflows.

/// <summary>
/// Entrée du workflow <c>BilanHebdo</c>. Tous les dossiers sont des chemins
/// absolus ; <see cref="Semaine"/> est au format ISO <c>2026-W34</c>, ou
/// <c>null</c> pour désigner la dernière semaine close.
/// </summary>
public sealed record BilanDemande(
    string DossierTranscripts,
    int Limite,
    string? Semaine,
    string DossierLentilles,
    string? Lentille,
    string? Camp,
    string DossierSortie,
    string DossierTravail,
    bool Juge);

/// <summary>
/// Résultat du workflow <c>BilanHebdo</c>, tel qu'il apparaît dans
/// l'historique Temporal.
/// </summary>
public sealed record BilanResultat(
    string Semaine,
    bool Vide,
    string? CheminPage,
    string? CheminTexte,
    string? EtatPage,
    string? EtatTexte,
    string? VersionPrecedente,
    string? SourceCritique,
    int? CriteresManquants,
    bool CritiqueEnRepli);

/// <summary>
/// Noms et constantes partagés entre le Worker et ce qui planifie ou déclenche
/// le bilan durable (file de tâches, nom de workflow, identifiant de
/// planification, adresse et espace de noms Temporal par défaut).
/// </summary>
public static class BilanContrats
{
    public const string FileDeTaches = "coachingia-bilan";
    public const string NomWorkflow = "BilanHebdo";
    public const string IdPlanification = "coachingia-bilan-lundi";
    public const string AdresseParDefaut = "127.0.0.1:7233";
    public const string EspaceParDefaut = "default";
    public const string ErreurClaudeIndisponible = "ClaudeIndisponible";
    public const string ErreurClaudeEchec = "ClaudeEchec";

    /// <summary>
    /// Dossier de travail par défaut du Worker (fichiers intermédiaires, hors
    /// dossier de sortie du bilan).
    /// </summary>
    public static string DossierTravailParDefaut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoachingIA", "temporal", "travail");
}
