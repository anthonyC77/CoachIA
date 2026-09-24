using System.Text;
using System.Text.Json;
using CoachingIA.Orchestration;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Testing;
using Temporalio.Worker;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du workflow <c>BilanHebdo</c> lui-même — pas des activities
/// hors ligne (voir <see cref="BilanActivitiesTests"/>), mais du même
/// enchaînement rejoué par un vrai moteur Temporal, à temps accéléré.
///
/// Deux volets bien séparés :
/// <list type="number">
/// <item>des vérifications pures sur <see cref="PlanificationBilan.Construire"/>,
/// qui ne dépendent d'aucun serveur et s'exécutent toujours ;</item>
/// <item>des vérifications de workflow, qui exigent
/// <see cref="WorkflowEnvironment.StartTimeSkippingAsync"/> — susceptible de
/// télécharger le serveur de test Temporal au premier usage, et donc
/// d'échouer hors ligne. Dans ce cas la suite rend « rien mesuré », jamais un
/// vert ni un échec.</item>
/// </list>
/// </summary>
public static class BilanWorkflowTests
{
    /// <summary>
    /// Non nul quand le second volet n'a pas pu s'exécuter — la raison à
    /// afficher. <c>null</c> tant que la suite n'a pas tourné, ou si
    /// l'environnement Temporal de test a démarré.
    /// </summary>
    public static string? RienMesure { get; private set; }

    public static void Run(Action<bool, string> check, string lensDir) =>
        RunAsync(check, lensDir).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check, string lensDir)
    {
        RunPlanificationChecks(check);

        WorkflowEnvironment env;
        try
        {
            env = await WorkflowEnvironment.StartTimeSkippingAsync();
        }
        catch (Exception ex)
        {
            RienMesure = $"{ex.GetType().Name} : {ex.Message}";
            Console.WriteLine($"rien mesuré — environnement Temporal de test indisponible ({RienMesure})");
            return;
        }

        await using (env)
        {
            await RunWorkflowChecksAsync(check, lensDir, env);
        }
    }

    /// <summary>
    /// Vérifications pures, sans serveur : le calendrier lundi 8 h 00, le
    /// fuseau, l'action visée et sa file, la semaine remise à null, et la
    /// politique de chevauchement.
    /// </summary>
    private static void RunPlanificationChecks(Action<bool, string> check)
    {
        Console.WriteLine("Planification (pure, sans serveur Temporal)");

        var demande = new BilanDemande(
            DossierTranscripts: "/tmp/transcripts",
            Limite: 40,
            Semaine: "2026-W34",
            DossierLentilles: "/tmp/lentilles",
            Lentille: null,
            Camp: null,
            DossierSortie: "/tmp/bilans",
            DossierTravail: "/tmp/travail",
            Juge: false);

        var planification = PlanificationBilan.Construire(demande);

        check(planification.Spec.TimeZoneName == "Europe/Paris",
              $"le calendrier vise le fuseau Europe/Paris (obtenu {planification.Spec.TimeZoneName})");

        var calendrier = planification.Spec.Calendars.Single();
        check(calendrier.DayOfWeek.Single() is { Start: 1, End: 1 }, "le calendrier vise le lundi (jour 1)");
        check(calendrier.Hour.Single() is { Start: 8, End: 8 }, "le calendrier vise 8 h");
        check(calendrier.Minute.Single() is { Start: 0, End: 0 }, "le calendrier vise la minute 0");

        var action = (ScheduleActionStartWorkflow)planification.Action;
        check(action.Workflow == BilanContrats.NomWorkflow,
              $"l'action démarre le workflow {BilanContrats.NomWorkflow} (obtenu {action.Workflow})");
        check(action.Options.TaskQueue == BilanContrats.FileDeTaches,
              $"l'action vise la file {BilanContrats.FileDeTaches} (obtenu {action.Options.TaskQueue})");

        var argument = (BilanDemande)action.Args.Single()!;
        check(argument.Semaine is null,
              "la semaine de l'argument est remise à null — un schedule vise toujours la dernière semaine close");

        check(planification.Policy?.Overlap == ScheduleOverlapPolicy.Skip,
              "la politique de chevauchement est Skip — pas deux bilans concurrents");
    }

    /// <summary>
    /// Les vérifications qui exigent un vrai moteur Temporal (à temps
    /// accéléré) : un unique Worker, un unique faux Claude reconfiguré entre
    /// chaque scénario, exactement comme le contexte de la tâche le demande.
    /// </summary>
    private static async Task RunWorkflowChecksAsync(Action<bool, string> check, string lensDir, WorkflowEnvironment env)
    {
        var racine = Path.Combine(Path.GetTempPath(), "coachingia-workflow-" + Guid.NewGuid().ToString("N")[..8]);
        var dossierTranscripts = Path.Combine(racine, "transcripts");

        try
        {
            BilanFixtures.Ecrire(dossierTranscripts);

            var faux = new BilanActivitiesTests.FauxClaudeCli();
            var activities = new BilanActivities(faux);
            var fileDeTaches = "bilan-workflow-tests-" + Guid.NewGuid().ToString("N")[..8];

            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(fileDeTaches)
                    .AddWorkflow<BilanHebdoWorkflow>()
                    .AddAllActivities(activities));

            BilanDemande Demande(string sousDossier, string? semaine, bool juge) => new(
                DossierTranscripts: dossierTranscripts,
                Limite: 40,
                Semaine: semaine ?? BilanFixtures.Semaine,
                DossierLentilles: lensDir,
                Lentille: null,
                Camp: null,
                DossierSortie: Path.Combine(racine, sousDossier, "bilans"),
                DossierTravail: Path.Combine(racine, sousDossier, "travail"),
                Juge: juge);

            async Task<(BilanResultat Resultat, Temporalio.Common.WorkflowHistory Historique)> LancerAsync(BilanDemande demande, string idPrefixe)
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (BilanHebdoWorkflow wf) => wf.RunAsync(demande),
                    new(id: idPrefixe + "-" + Guid.NewGuid().ToString("N")[..8], taskQueue: fileDeTaches));
                var resultat = await handle.GetResultAsync();
                var historique = await handle.FetchHistoryAsync();
                return (resultat, historique);
            }

            await worker.ExecuteAsync(async () =>
            {
                Console.WriteLine("Déroulé nominal");
                var demandeNominale = Demande("nominal", null, juge: false);
                var (resultatNominal, historiqueNominal) = await LancerAsync(demandeNominale, "bilan-nominal");
                check(!resultatNominal.Vide, "le déroulé nominal n'est pas vide");
                check(resultatNominal.CheminPage is not null && File.Exists(resultatNominal.CheminPage),
                      $"la page HTML existe au chemin CheminPage (obtenu {resultatNominal.CheminPage})");
                check(resultatNominal.SourceCritique == "heuristique",
                      $"la critique porte la source heuristique (obtenu {resultatNominal.SourceCritique})");
                check(!resultatNominal.CritiqueEnRepli, "aucun repli sur le déroulé nominal");

                Console.WriteLine("\nHistorique du déroulé nominal");
                var jsonHistorique = historiqueNominal.ToJson();
                var decodees = new List<string>();
                using (var doc = JsonDocument.Parse(jsonHistorique))
                    Collecter(doc.RootElement, decodees);
                var concatenationDecodee = string.Join("\n", decodees);
                check(!(jsonHistorique + concatenationDecodee).Contains(BilanFixtures.PromptTemoin),
                      "le témoin du prompt n'apparaît nulle part dans l'historique, ni brut ni décodé");
                // DossierSortie est réencodé comme le ferait tout convertisseur JSON (antislashs
                // doublés sur Windows) avant d'être cherché — comparer au chemin brut échouerait
                // toujours sur ce point, sans rapport avec le décodage lui-même.
                var dossierSortieEchappe = JsonSerializer.Serialize(demandeNominale.DossierSortie);
                dossierSortieEchappe = dossierSortieEchappe[1..^1];
                check(concatenationDecodee.Contains(dossierSortieEchappe),
                      "contrôle positif : le décodage base64 marche — DossierSortie apparaît dans les payloads décodés");
                var cheminRevueNominal = Path.Combine(demandeNominale.DossierTravail, BilanFixtures.Semaine, "revue.json");
                check(File.Exists(cheminRevueNominal) && File.ReadAllText(cheminRevueNominal).Contains(BilanFixtures.PromptTemoin),
                      "contrôle positif : le témoin a bien traversé le pipeline jusque dans revue.json");

                Console.WriteLine("\nRepli (juge indisponible)");
                faux.EstDisponible = false;
                var appelsAvantRepli = faux.Appels;
                var demandeRepli = Demande("repli", null, juge: true);
                var (resultatRepli, _) = await LancerAsync(demandeRepli, "bilan-repli");
                check(resultatRepli.CritiqueEnRepli, "le juge indisponible déclenche le repli");
                check(resultatRepli.SourceCritique == "heuristique",
                      $"la critique de repli porte la source heuristique (obtenu {resultatRepli.SourceCritique})");
                check(faux.Appels == appelsAvantRepli, "le faux n'a reçu aucun appel à Demander pendant le repli");

                Console.WriteLine("\nReprise (juge en échec deux fois, puis disponible)");
                faux.EstDisponible = true;
                faux.Programmer("échec 1", null);
                faux.Programmer("échec 2", null);
                faux.Programmer(null, """{"structured_output":{"rewrite":"Prompt réécrit par le juge.","missing":[]}}""");
                var appelsAvantReprise = faux.Appels;
                var demandeReprise = Demande("reprise", null, juge: true);
                var (resultatReprise, _) = await LancerAsync(demandeReprise, "bilan-reprise");
                check(resultatReprise.SourceCritique == "claude -p",
                      $"après reprise, la source annonce le juge (obtenu {resultatReprise.SourceCritique})");
                check(!resultatReprise.CritiqueEnRepli, "la reprise a fini par réussir : pas de repli");
                check(faux.Appels - appelsAvantReprise == 3,
                      $"le faux a reçu exactement 3 appels (obtenu {faux.Appels - appelsAvantReprise})");

                Console.WriteLine("\nSemaine vide");
                var demandeVide = Demande("vide", "2020-W01", juge: false);
                var (resultatVide, historiqueVide) = await LancerAsync(demandeVide, "bilan-vide");
                check(resultatVide.Vide, "une semaine sans tâche rend Vide = true");
                var jsonHistoriqueVide = historiqueVide.ToJson();
                check(!jsonHistoriqueVide.Contains("\"Critiquer\"") && !jsonHistoriqueVide.Contains("\"RendreEtArchiver\""),
                      "aucune activity Critiquer ni RendreEtArchiver n'apparaît dans l'historique d'une semaine vide");

                Console.WriteLine("\nDéterminisme : rejeu de l'historique du déroulé nominal");
                var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<BilanHebdoWorkflow>());
                var rejeu = await replayer.ReplayWorkflowAsync(historiqueNominal);
                check(rejeu.ReplayFailure is null,
                      $"l'historique du déroulé nominal se rejoue sans erreur de non-déterminisme (obtenu {rejeu.ReplayFailure?.Message})");
            });
        }
        finally
        {
            try { Directory.Delete(racine, recursive: true); } catch { /* nettoyage au mieux */ }
        }
    }

    /// <summary>
    /// Parcourt récursivement le JSON de l'historique et décode en UTF-8
    /// toute valeur base64 d'une propriété <c>"data"</c> — c'est ainsi que
    /// Temporal encode les payloads dans son JSON. Une simple recherche dans
    /// le JSON brut serait trivialement verte ; ce décodage la rend probante.
    /// </summary>
    private static void Collecter(JsonElement element, List<string> resultats)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var propriete in element.EnumerateObject())
                {
                    if (propriete.Name == "data" && propriete.Value.ValueKind == JsonValueKind.String)
                    {
                        var decode = DecoderBase64(propriete.Value.GetString()!);
                        if (decode is not null) resultats.Add(decode);
                    }
                    Collecter(propriete.Value, resultats);
                }
                break;
            case JsonValueKind.Array:
                foreach (var element2 in element.EnumerateArray())
                    Collecter(element2, resultats);
                break;
        }
    }

    private static string? DecoderBase64(string valeur)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(valeur)); }
        catch (FormatException) { return null; }
    }
}
