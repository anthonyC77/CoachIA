using System.Text.Json;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Orchestration;
using Temporalio.Exceptions;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai des quatre activities du bilan durable, appelées hors ligne
/// comme de simples fonctions — exactement ce que garantit leur contrat :
/// aucune ne dépend d'un serveur Temporal pour être éprouvée.
///
/// La promesse vérifiée ici tient en trois points : le témoin du prompt
/// traverse le pipeline jusque dans les fichiers intermédiaires mais jamais
/// dans ce qui circulerait dans l'historique Temporal ; les écritures sont
/// idempotentes ; et le repli sur l'heuristique, ou l'échec du juge, ne fuit
/// jamais un texte de prompt ni l'erreur brute de Claude dans le message
/// d'une exception.
/// </summary>
public static class BilanActivitiesTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        var racine = Path.Combine(Path.GetTempPath(), "coachingia-activities-" + Guid.NewGuid().ToString("N")[..8]);
        var dossierTranscripts = Path.Combine(racine, "transcripts");
        var dossierTravail = Path.Combine(racine, "travail");
        var dossierSortie = Path.Combine(racine, "bilans");

        try
        {
            BilanFixtures.Ecrire(dossierTranscripts);

            var demande = new BilanDemande(
                DossierTranscripts: dossierTranscripts,
                Limite: BilanPipeline.LimiteParDefaut,
                Semaine: BilanFixtures.Semaine,
                DossierLentilles: lensDir,
                Lentille: null,
                Camp: null,
                DossierSortie: dossierSortie,
                DossierTravail: dossierTravail,
                Juge: false);

            var fauxClaude = new FauxClaudeCli();
            var activities = new BilanActivities(fauxClaude);

            Console.WriteLine("Inventaire");
            var unSeulChemin = activities.InventorierSemaine(demande with { Limite = 1 });
            check(unSeulChemin.Count == 1, $"Limite 1 renvoie exactement 1 chemin (obtenu {unSeulChemin.Count})");
            check(unSeulChemin.All(c => Path.IsPathRooted(c) && File.Exists(c)), "et ce chemin est absolu et existe");

            var chemins = activities.InventorierSemaine(demande);

            Console.WriteLine("\nSegmentation et extraction");
            var extraction = activities.SegmenterEtExtraire(demande, chemins);
            check(!extraction.Vide, "la semaine du témoin n'est pas vide");
            check(extraction.CheminRevue is not null && File.Exists(extraction.CheminRevue),
                  "la revue est écrite sous DossierTravail");
            var contenuRevue = File.ReadAllText(extraction.CheminRevue!);
            check(contenuRevue.Contains(BilanFixtures.PromptTemoin),
                  "contrôle positif : le témoin traverse bien le pipeline jusque dans revue.json");
            var jsonExtraction = JsonSerializer.Serialize(extraction);
            check(!jsonExtraction.Contains(BilanFixtures.PromptTemoin),
                  "mais jamais dans ce que le workflow ferait circuler dans l'historique Temporal");

            Console.WriteLine("\nIdempotence de l'extraction");
            var extraction2 = activities.SegmenterEtExtraire(demande, chemins);
            check(extraction2.CheminRevue == extraction.CheminRevue, "un second appel identique rend le même chemin");
            check(File.ReadAllText(extraction2.CheminRevue!) == contenuRevue,
                  "et un fichier au contenu identique — l'activity peut être rejouée sans risque");

            Console.WriteLine("\nSemaine sans tâche");
            var extractionVide = activities.SegmenterEtExtraire(demande with { Semaine = "2020-W01" }, chemins);
            check(extractionVide.Vide, "une semaine sans tâche rend Vide = true");
            check(extractionVide.CheminRevue is null, "et aucun chemin de revue");
            check(!Directory.Exists(Path.Combine(dossierTravail, "2020-W01")), "et n'écrit aucun fichier");

            Console.WriteLine("\nCritique hors ligne");
            var cheminCritiqueHeuristique = activities.Critiquer(extraction.CheminRevue!, juge: false);
            check(cheminCritiqueHeuristique is not null && File.Exists(cheminCritiqueHeuristique),
                  "la critique heuristique est écrite sur disque");
            var critiqueHeuristique = WeeklyReviewSnapshot.DeserialiserCritique(File.ReadAllText(cheminCritiqueHeuristique!));
            check(critiqueHeuristique.Source == "heuristique", "la critique hors ligne porte la bonne source");
            check(fauxClaude.Appels == 0, "le faux Claude n'a reçu aucun appel : juge = false ne le sollicite jamais");

            // Rendu et archivage tout de suite, avant que les essais du juge
            // ci-dessous ne réécrivent critique.json — un seul fichier par
            // revue, exactement comme le voudrait un vrai workflow qui
            // n'appelle Critiquer qu'une fois.
            Console.WriteLine("\nRendu et archivage");
            var resultat = activities.RendreEtArchiver(demande, extraction.CheminRevue!, cheminCritiqueHeuristique);
            check(resultat.EtatPage == "nouveau", $"le premier appel crée la page (obtenu {resultat.EtatPage})");
            check(File.Exists(Path.Combine(dossierSortie, resultat.Semaine + ".html")), "la page HTML est écrite dans DossierSortie");
            check(File.Exists(Path.Combine(dossierSortie, resultat.Semaine + ".md")), "le texte Markdown aussi");

            var resultat2 = activities.RendreEtArchiver(demande, extraction.CheminRevue!, cheminCritiqueHeuristique);
            check(resultat2.EtatPage == "inchangé",
                  $"un second appel identique ne touche pas la page (obtenu {resultat2.EtatPage})");

            var avertissements = new List<string>();
            BilanPipeline.MonterCorpus(lensDir, avertissements);
            var ecrivainDirect = BilanPipeline.Ecrivain(lensDir, demande.Lentille, demande.Camp, avertissements);
            var sessionsDirectes = BilanPipeline.Charger(chemins);
            var revueDirecte = BilanPipeline.Preparer(sessionsDirectes, demande.Semaine, ecrivainDirect, new HeuristicPromptCritic());
            var htmlDirect = HtmlReviewRenderer.Render(revueDirecte, ecrivainDirect);
            var htmlEcrit = File.ReadAllText(Path.Combine(dossierSortie, resultat.Semaine + ".html"));
            check(htmlEcrit == htmlDirect,
                  "le HTML archivé par les activities est identique à celui d'une revue construite en une passe");

            var jsonResultat = JsonSerializer.Serialize(resultat);
            check(!jsonResultat.Contains(BilanFixtures.PromptTemoin),
                  "et le résultat qui circulerait dans Temporal ne porte pas le témoin");

            Console.WriteLine("\nJuge indisponible");
            fauxClaude.EstDisponible = false;
            ApplicationFailureException? indisponible = null;
            try { activities.Critiquer(extraction.CheminRevue!, juge: true); }
            catch (ApplicationFailureException ex) { indisponible = ex; }
            check(indisponible is not null, "un juge indisponible lève une ApplicationFailureException");
            check(indisponible?.ErrorType == BilanContrats.ErreurClaudeIndisponible,
                  $"avec le bon type d'erreur (obtenu {indisponible?.ErrorType})");
            check(indisponible?.NonRetryable == true, "non retentable : ça ne sert à rien de réessayer sans le binaire");
            check(fauxClaude.Appels == 0, "sans jamais appeler Demander");

            Console.WriteLine("\nJuge en échec");
            fauxClaude.EstDisponible = true;
            const string chaineRepere = "REPERE-ERREUR-3f9c1";
            fauxClaude.ProchaineErreur = chaineRepere;
            ApplicationFailureException? echec = null;
            try { activities.Critiquer(extraction.CheminRevue!, juge: true); }
            catch (ApplicationFailureException ex) { echec = ex; }
            check(echec is not null, "un échec de Claude lève une ApplicationFailureException");
            check(echec?.ErrorType == BilanContrats.ErreurClaudeEchec,
                  $"avec le bon type d'erreur (obtenu {echec?.ErrorType})");
            check(echec?.NonRetryable == false, "mais elle reste retentable : un prochain essai peut réussir");
            check(echec is not null && !echec.Message.Contains(BilanFixtures.PromptTemoin),
                  "le message de l'exception ne recopie pas le prompt");
            check(echec is not null && !echec.Message.Contains(chaineRepere),
                  "ni la dernière erreur de Claude — l'une et l'autre restent hors de l'historique Temporal");

            Console.WriteLine("\nJuge disponible");
            fauxClaude.ProchaineErreur = null;
            fauxClaude.ProchaineReponse = """{"structured_output":{"rewrite":"Prompt réécrit par le juge.","missing":[]}}""";
            var cheminCritiqueJuge = activities.Critiquer(extraction.CheminRevue!, juge: true);
            check(cheminCritiqueJuge is not null, "le juge disponible produit bien une critique");
            var critiqueJuge = WeeklyReviewSnapshot.DeserialiserCritique(File.ReadAllText(cheminCritiqueJuge!));
            check(critiqueJuge.Source == "claude -p", "et sa source annonce que c'est le juge qui a écrit");
        }
        finally
        {
            try { Directory.Delete(racine, recursive: true); } catch { /* nettoyage au mieux */ }
        }
    }

    /// <summary>
    /// Un faux <see cref="IClaudeCli"/> : compteur d'appels, disponibilité et
    /// réponse programmables. C'est le seul moyen d'éprouver <c>Critiquer</c>
    /// hors ligne — aucun vrai appel à <c>claude -p</c> dans cette suite.
    /// </summary>
    private sealed class FauxClaudeCli : IClaudeCli
    {
        public int Appels { get; private set; }
        public bool EstDisponible { get; set; } = true;
        public string? DerniereErreur { get; private set; }

        /// <summary>La réponse que <see cref="Demander"/> renverra — ignorée si <see cref="ProchaineErreur"/> est renseigné.</summary>
        public string? ProchaineReponse { get; set; }

        /// <summary>Quand renseigné, <see cref="Demander"/> échoue et rend cette chaîne en <see cref="DerniereErreur"/>.</summary>
        public string? ProchaineErreur { get; set; }

        public string? Demander(string instruction, string? schema)
        {
            Appels++;
            DerniereErreur = ProchaineErreur;
            return ProchaineErreur is null ? ProchaineReponse : null;
        }
    }
}
