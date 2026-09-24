using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Le bilan hebdomadaire, découpé en étapes séparables : inventorier les
/// transcripts, les charger, préparer la revue (avec ou sans la critique du
/// prompt), appliquer la critique différée, puis archiver. Chaque étape ne se
/// passe que de chemins de fichiers ou de types déjà en mémoire — c'est ce qui
/// permettra plus tard de les appeler depuis des activities Temporal.
///
/// <c>coachingia bilan</c> assemble ces étapes exactement comme avant : rien
/// n'a changé dans ce que la commande produit, seule la logique a été extraite.
/// </summary>
public static class BilanPipeline
{
    /// <summary>Le nombre de transcripts relus par défaut, faute de --limit.</summary>
    public const int LimiteParDefaut = 40;

    /// <summary>Les transcripts à lire, en chemins absolus, dans l'ordre où ils seraient lus.</summary>
    public static List<string> Inventorier(string dossierTranscripts, int limite)
        => TranscriptReader.FindTranscripts(dossierTranscripts)
            .Take(limite)
            .Select(Path.GetFullPath)
            .ToList();

    /// <summary>Relit les transcripts désignés et reconstruit les sessions.</summary>
    public static List<TranscriptSession> Charger(IEnumerable<string> chemins, ParseReport? rapport = null)
    {
        rapport ??= new ParseReport();
        var records = chemins.SelectMany(f => TranscriptReader.Read(f, rapport));
        return SessionBuilder.Build(records);
    }

    /// <summary>
    /// Monte le corpus de maturité : les signaux notés, les problématiques et
    /// les paliers. C'est un état statique global — il doit être monté avant
    /// tout calcul de signal, y compris ceux de Preparer.
    /// </summary>
    public static void MonterCorpus(string dossierLentilles, List<string> avertissements)
        => SignalSpecs.Use(MaturityCorpus.Load(dossierLentilles, avertissements));

    /// <summary>
    /// Résout la lentille et le camp demandés en un habilleur du bilan. Les
    /// avertissements rencontrés sont ajoutés à la liste fournie, sans le préfixe
    /// « ⚠ » : c'est à l'appelant de décider comment les afficher.
    /// </summary>
    public static LensWriter Ecrivain(string dossierLentilles, string? lentille, string? camp, List<string> avertissements)
    {
        var warnings = new List<string>();
        var catalog = LensCatalog.Load(dossierLentilles, warnings);
        foreach (var w in warnings) avertissements.Add("lentille : " + w);
        if (lentille is not null && !catalog.Knows(lentille))
            avertissements.Add($"lentille « {lentille} » inconnue — retour au neutre.");

        var lens = catalog.Resolve(lentille);
        if (camp is not null && lens.Race(camp) is null)
        {
            var camps = lens.Races.Count == 0 ? "aucun" : string.Join(", ", lens.Races.Keys.OrderBy(x => x, StringComparer.Ordinal));
            avertissements.Add($"camp « {camp} » inconnu pour cette lentille (disponibles : {camps}) — vocabulaire générique.");
        }
        return new LensWriter(lens, camp);
    }

    /// <summary>
    /// Segmente les sessions, extrait les signaux et assemble la revue. Avec
    /// <paramref name="differerCritique"/>, le prompt le plus coûteux est choisi
    /// et son contexte posé, mais la critique elle-même n'est pas appelée — elle
    /// attend un appel séparé à <see cref="AppliquerCritique"/>, potentiellement
    /// depuis une activity Temporal distincte.
    /// </summary>
    public static WeeklyReview Preparer(
        IEnumerable<TranscriptSession> sessions, string? semaine, LensWriter ecrivain,
        IPromptCritic? critique, bool differerCritique = false)
        => new WeeklyReviewBuilder { Critic = critique, DifferCritique = differerCritique }.Build(sessions, semaine, ecrivain);

    /// <summary>
    /// Applique la critique différée par <see cref="Preparer"/>. Sans prompt
    /// choisi (semaine trop calme, ou critique non différée), ne fait rien.
    /// </summary>
    public static void AppliquerCritique(WeeklyReview revue, IPromptCritic critique)
    {
        if (revue.PromptACritiquer is not { } prompt || revue.PromptContext is not { } contexte) return;
        revue.PromptOfTheWeek = critique.Critique(prompt, contexte);
    }

    /// <summary>Écrit la page et le texte du bilan, avec rotation de l'ancienne version.</summary>
    public static (WriteResult Page, WriteResult Texte) Archiver(string dossier, WeeklyReview revue, LensWriter ecrivain)
    {
        var page = ReviewArchive.Write(dossier, $"{revue.Week}.html", HtmlReviewRenderer.Render(revue, ecrivain));
        var texte = ReviewArchive.Write(dossier, $"{revue.Week}.md", ReviewRenderer.ToMarkdown(revue, ecrivain));
        return (page, texte);
    }
}
