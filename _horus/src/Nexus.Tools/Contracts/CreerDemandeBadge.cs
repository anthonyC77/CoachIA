namespace Nexus.Tools.Contracts;

// Effet de bord : chaque appel DOIT porter une clé d'idempotence (imposé par le
// harnais à l'exécution et vérifié par l'eval depuis l'extérieur).
[Tool("creer_demande_badge", SideEffect = true,
      Description = "Ouvre une demande de badge d'accès pour une personne, au nom de l'identité SI du run.")]
public sealed record CreerDemandeBadgeInput(
    [Doc("Nom complet de la personne")] string Personne,
    [Doc("Site d'affectation")] string Site,
    [Doc("Type de badge : entreprise | prestataire")] string Type,
    [Doc("Identifiant du responsable qui autorise la demande")] string AutorisePar);

[ToolOutput("creer_demande_badge")]
public sealed record CreerDemandeBadgeOutput(
    string DemandeId,
    [Doc("Statut de la demande : ouverte | refusee")] string Statut);
