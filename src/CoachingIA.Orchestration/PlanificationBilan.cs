using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace CoachingIA.Orchestration;

/// <summary>
/// Construction du Schedule Temporal « tous les lundis à 8 h » qui lance le
/// workflow <c>BilanHebdo</c>. Classe pure et testable : elle ne fait aucune
/// E/S hors du client qu'on lui passe, et ne construit jamais le Schedule à
/// partir de l'horloge — le calendrier est fixe, pas relatif à « maintenant ».
/// </summary>
public static class PlanificationBilan
{
    /// <summary>
    /// Le Schedule d'un bilan hebdomadaire durable : calendrier lundi 8 h 00
    /// dans le fuseau donné, action sur le workflow <see cref="BilanContrats.NomWorkflow"/>
    /// via la file <see cref="BilanContrats.FileDeTaches"/>, avec <see cref="BilanDemande.Semaine"/>
    /// toujours remis à <c>null</c> : un Schedule vise toujours la dernière
    /// semaine close au moment où il se déclenche, jamais une semaine figée à
    /// la création. La politique de chevauchement <c>Skip</c> évite deux
    /// bilans concurrents si le précédent tourne encore.
    /// </summary>
    public static Schedule Construire(BilanDemande demande, string fuseau = "Europe/Paris")
    {
        var spec = new ScheduleSpec
        {
            Calendars =
            [
                new ScheduleCalendarSpec
                {
                    DayOfWeek = [new ScheduleRange(1)],
                    Hour = [new ScheduleRange(8)],
                    Minute = [new ScheduleRange(0)],
                    Second = [new ScheduleRange(0)],
                },
            ],
            TimeZoneName = fuseau,
        };

        var action = ScheduleActionStartWorkflow.Create(
            BilanContrats.NomWorkflow,
            new object?[] { demande with { Semaine = null } },
            new WorkflowOptions(id: "bilan-planifie", taskQueue: BilanContrats.FileDeTaches));

        return new Schedule(action, spec)
        {
            Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
        };
    }

    /// <summary>
    /// Crée le Schedule <see cref="BilanContrats.IdPlanification"/> s'il n'existe
    /// pas encore, ou le remplace s'il existe déjà — idempotent, pour qu'on
    /// puisse relancer <c>bilan --planifier</c> sans risque. Renvoie
    /// <c>"créée"</c> ou <c>"mise à jour"</c>.
    /// </summary>
    public static async Task<string> CreerOuMettreAJourAsync(ITemporalClient client, BilanDemande demande, string fuseau)
    {
        var schedule = Construire(demande, fuseau);
        try
        {
            await client.CreateScheduleAsync(BilanContrats.IdPlanification, schedule);
            return "créée";
        }
        catch (ScheduleAlreadyRunningException)
        {
            var handle = client.GetScheduleHandle(BilanContrats.IdPlanification);
            await handle.UpdateAsync(_ => new ScheduleUpdate(schedule));
            return "mise à jour";
        }
    }
}
