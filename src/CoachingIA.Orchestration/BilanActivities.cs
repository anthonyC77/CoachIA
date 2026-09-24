using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Coaching;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace CoachingIA.Orchestration;

/// <summary>
/// Ce que <see cref="BilanActivities.SegmenterEtExtraire"/> a produit : la
/// semaine visée, un drapeau « rien à raconter » et, sinon, le chemin de la
/// revue écrite sur disque. Contrairement à <see cref="WeeklyReview"/>, ce
/// type traverse l'historique Temporal — d'où l'absence de tout texte de
/// prompt ou de transcript, remplacés par un chemin de fichier.
/// </summary>
public sealed record Extraction(string Semaine, bool Vide, string? CheminRevue);

/// <summary>
/// Les quatre activities du bilan durable, dans l'ordre où le workflow les
/// enchaîne : inventorier les transcripts, en extraire la revue de la semaine,
/// la critiquer, puis la rendre et l'archiver.
///
/// Chacune ne fait circuler que des chemins de fichiers — jamais un texte de
/// prompt ni un contenu de transcript, qui resteraient sinon dans l'historique
/// Temporal, visible dans son UI et journalisé à chaque étape. Voir
/// docs/decision-temporal.md.
///
/// Les activities sont rejouables (au-moins-une-fois) : toute écriture sur
/// disque est atomique, fichier temporaire dans le même dossier puis
/// <see cref="File.Move(string, string, bool)"/>, pour qu'une relecture voie
/// toujours un contenu complet, jamais une écriture à moitié faite.
/// </summary>
public sealed class BilanActivities(IClaudeCli claude)
{
    private const string NomFichierRevue = "revue.json";
    private const string NomFichierCritique = "critique.json";

    /// <summary>Les transcripts à lire, en chemins absolus, dans l'ordre où ils seraient lus.</summary>
    [Activity]
    public List<string> InventorierSemaine(BilanDemande demande)
        => BilanPipeline.Inventorier(demande.DossierTranscripts, demande.Limite);

    /// <summary>
    /// Charge les transcripts désignés, segmente et extrait la revue de la
    /// semaine visée — sans appeler la critique du prompt, différée à
    /// <see cref="Critiquer"/>. Une semaine sans tâche n'écrit rien.
    /// </summary>
    [Activity]
    public Extraction SegmenterEtExtraire(BilanDemande demande, List<string> chemins)
    {
        var avertissements = new List<string>();
        BilanPipeline.MonterCorpus(demande.DossierLentilles, avertissements);
        var ecrivain = BilanPipeline.Ecrivain(demande.DossierLentilles, demande.Lentille, demande.Camp, avertissements);
        foreach (var avertissement in avertissements)
            Console.Error.WriteLine("⚠ " + avertissement);

        var sessions = BilanPipeline.Charger(chemins);
        var revue = BilanPipeline.Preparer(sessions, demande.Semaine, ecrivain, critique: null, differerCritique: true);

        if (revue.IsEmpty)
            return new Extraction(revue.Week, true, null);

        var dossierSemaine = Path.Combine(demande.DossierTravail, revue.Week);
        Directory.CreateDirectory(dossierSemaine);
        var cheminRevue = Path.Combine(dossierSemaine, NomFichierRevue);
        EcrireAtomique(cheminRevue, WeeklyReviewSnapshot.Serialiser(revue));

        return new Extraction(revue.Week, false, Path.GetFullPath(cheminRevue));
    }

    /// <summary>
    /// Relit la revue et critique son prompt le plus coûteux — hors ligne par
    /// défaut, ou jugé par Claude quand <paramref name="juge"/> vaut vrai.
    /// Sans prompt choisi (semaine trop calme), ne fait rien. Le message des
    /// exceptions levées ne recopie jamais le prompt ni l'erreur de Claude :
    /// l'un et l'autre ne vont que sur la sortie d'erreur, jamais dans
    /// l'historique Temporal.
    /// </summary>
    [Activity]
    public string? Critiquer(string cheminRevue, bool juge)
    {
        var revue = WeeklyReviewSnapshot.Deserialiser(File.ReadAllText(cheminRevue));
        if (revue.PromptACritiquer is not { } prompt || revue.PromptContext is not { } contexte)
            return null;

        PromptCritique? critique;
        if (!juge)
        {
            critique = new HeuristicPromptCritic().Critique(prompt, contexte);
        }
        else
        {
            // On regarde avant d'appeler, jamais après avoir échoué : sans
            // binaire disponible, on ne tente même pas Demander.
            if (!claude.EstDisponible)
                throw new ApplicationFailureException(
                    "Le juge Claude n'est pas disponible pour critiquer le prompt de la semaine.",
                    errorType: BilanContrats.ErreurClaudeIndisponible, nonRetryable: true);

            var jugeClaude = new ClaudePromptCritic(new HeuristicPromptCritic(), claude);
            critique = jugeClaude.Critique(prompt, contexte);
            if (jugeClaude.LastError is { } erreur)
            {
                Console.Error.WriteLine("⚠ l'appel à Claude a échoué : " + erreur);
                throw new ApplicationFailureException(
                    "L'appel à Claude pour juger le prompt de la semaine a échoué.",
                    errorType: BilanContrats.ErreurClaudeEchec, nonRetryable: false);
            }
        }

        if (critique is null) return null;      // le prompt était déjà complet

        var cheminCritique = Path.Combine(Path.GetDirectoryName(cheminRevue)!, NomFichierCritique);
        EcrireAtomique(cheminCritique, WeeklyReviewSnapshot.SerialiserCritique(critique));
        return Path.GetFullPath(cheminCritique);
    }

    /// <summary>Relit la revue et sa critique, rend la page et le texte, puis les archive.</summary>
    [Activity]
    public BilanResultat RendreEtArchiver(BilanDemande demande, string cheminRevue, string? cheminCritique)
    {
        var revue = WeeklyReviewSnapshot.Deserialiser(File.ReadAllText(cheminRevue));
        if (cheminCritique is not null)
            revue.PromptOfTheWeek = WeeklyReviewSnapshot.DeserialiserCritique(File.ReadAllText(cheminCritique));

        var avertissements = new List<string>();
        var ecrivain = BilanPipeline.Ecrivain(demande.DossierLentilles, demande.Lentille, demande.Camp, avertissements);
        foreach (var avertissement in avertissements)
            Console.Error.WriteLine("⚠ " + avertissement);

        var (page, texte) = BilanPipeline.Archiver(demande.DossierSortie, revue, ecrivain);

        return new BilanResultat(
            revue.Week,
            Vide: false,
            CheminPage: Path.GetFullPath(page.Path),
            CheminTexte: Path.GetFullPath(texte.Path),
            EtatPage: Etat(page.Outcome),
            EtatTexte: Etat(texte.Outcome),
            VersionPrecedente: page.ArchivedTo,
            SourceCritique: revue.PromptOfTheWeek?.Source,
            CriteresManquants: revue.PromptOfTheWeek?.Missing.Count,
            CritiqueEnRepli: false);
    }

    private static string Etat(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.Created => "nouveau",
        WriteOutcome.Unchanged => "inchangé",
        _ => "mis à jour, ancien archivé",
    };

    /// <summary>
    /// Écrit un fichier de façon atomique : un fichier temporaire dans le même
    /// dossier, puis un déplacement qui écrase l'ancien contenu. Une activity
    /// rejouée réécrit ainsi le même contenu au même chemin, jamais un fichier
    /// à moitié écrit.
    /// </summary>
    private static void EcrireAtomique(string chemin, string contenu)
    {
        var dossier = Path.GetDirectoryName(chemin)!;
        var temporaire = Path.Combine(dossier, Path.GetRandomFileName());
        File.WriteAllText(temporaire, contenu);
        File.Move(temporaire, chemin, overwrite: true);
    }
}
