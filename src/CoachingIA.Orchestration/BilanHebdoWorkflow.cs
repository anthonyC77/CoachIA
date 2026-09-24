using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace CoachingIA.Orchestration;

/// <summary>
/// Le workflow <c>BilanHebdo</c> : orchestre les quatre activities du bilan
/// durable, dans l'ordre inventaire → segmentation/extraction → critique →
/// rendu/archivage.
///
/// Toute la logique est déterministe : ce fichier ne fait aucune E/S, ne lit
/// jamais l'horloge ni une source d'aléa, et ne fait circuler que ce que les
/// activities ont déjà produit — jamais un texte de prompt ni un contenu de
/// transcript. Voir docs/decision-temporal.md.
/// </summary>
[Workflow(BilanContrats.NomWorkflow)]
public class BilanHebdoWorkflow
{
    private static readonly ActivityOptions OptionsInventaire = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
    };

    private static readonly ActivityOptions OptionsSegmentation = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(10),
    };

    // Le juge Claude a son propre délai de 90 s (voir ClaudeCli) ; 3 minutes
    // laissent de la marge pour le reste de l'activity. Non retentable quand
    // le binaire est absent — inutile d'insister sans lui ; retentable quand
    // l'appel a simplement échoué, avec un repli exponentiel jusqu'à 4 essais.
    private static readonly ActivityOptions OptionsCritique = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(3),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 4,
            NonRetryableErrorTypes = [BilanContrats.ErreurClaudeIndisponible],
        },
    };

    private static readonly ActivityOptions OptionsRendu = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
    };

    [WorkflowRun]
    public async Task<BilanResultat> RunAsync(BilanDemande demande)
    {
        var chemins = await Workflow.ExecuteActivityAsync(
            (BilanActivities a) => a.InventorierSemaine(demande), OptionsInventaire);

        var extraction = await Workflow.ExecuteActivityAsync(
            (BilanActivities a) => a.SegmenterEtExtraire(demande, chemins), OptionsSegmentation);

        if (extraction.Vide)
            return new BilanResultat(
                extraction.Semaine, Vide: true, CheminPage: null, CheminTexte: null,
                EtatPage: null, EtatTexte: null, VersionPrecedente: null, SourceCritique: null,
                CriteresManquants: null, CritiqueEnRepli: false);

        var cheminRevue = extraction.CheminRevue!;

        string? cheminCritique;
        var critiqueEnRepli = false;
        try
        {
            cheminCritique = await Workflow.ExecuteActivityAsync(
                (BilanActivities a) => a.Critiquer(cheminRevue, demande.Juge), OptionsCritique);
        }
        catch (ActivityFailureException) when (demande.Juge)
        {
            // Le juge n'a pas répondu (indisponible ou en échec durable après
            // les reprises) : on conserve la critique hors ligne, comme la
            // commande bilan quand le juge ne répond pas.
            cheminCritique = await Workflow.ExecuteActivityAsync(
                (BilanActivities a) => a.Critiquer(cheminRevue, false), OptionsCritique);
            critiqueEnRepli = true;
        }

        var resultat = await Workflow.ExecuteActivityAsync(
            (BilanActivities a) => a.RendreEtArchiver(demande, cheminRevue, cheminCritique), OptionsRendu);

        return resultat with { CritiqueEnRepli = critiqueEnRepli };
    }
}
