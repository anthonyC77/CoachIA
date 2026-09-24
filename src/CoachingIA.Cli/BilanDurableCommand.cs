using System.Globalization;
using CoachingIA.Orchestration;
using Temporalio.Client;
using Temporalio.Exceptions;

/// <summary>
/// Les deux options durables de <c>bilan</c> : <c>--durable</c> démarre le
/// workflow Temporal <see cref="BilanContrats.NomWorkflow"/> et attend son
/// résultat ; <c>--planifier</c> crée ou met à jour le Schedule qui le
/// déclenche tous les lundis à 8 h.
///
/// Seul fichier du CLI à référencer Temporalio — voir le commentaire de tête
/// de <c>Program.cs</c> : un <c>bilan</c> ordinaire ne charge jamais cet
/// assembly, puisque rien avant l'appel à ce fichier n'en connaît un type.
/// </summary>
public static class BilanDurableCommand
{
    public static int Run(
        string[] args, string root, string? outPath, int limit, string? weekArg,
        string lensDir, string? lensId, string? raceId, bool juge)
    {
        var adresse = Arg(args, "--temporal") ?? BilanContrats.AdresseParDefaut;
        var attenteMinutes = double.TryParse(Arg(args, "--attente"), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 10;
        var fuseau = Arg(args, "--fuseau") ?? "Europe/Paris";

        // Chemins absolus : le Worker tourne dans un autre dossier courant que
        // le CLI qui le déclenche.
        var demande = new BilanDemande(
            DossierTranscripts: Path.GetFullPath(root),
            Limite: limit,
            Semaine: weekArg,
            DossierLentilles: Path.GetFullPath(lensDir),
            Lentille: lensId,
            Camp: raceId,
            DossierSortie: Path.GetFullPath(outPath ?? "bilans"),
            DossierTravail: BilanContrats.DossierTravailParDefaut,
            Juge: juge);

        return Array.IndexOf(args, "--planifier") >= 0
            ? PlanifierAsync(adresse, demande, fuseau).GetAwaiter().GetResult()
            : DurableAsync(adresse, demande, TimeSpan.FromMinutes(attenteMinutes)).GetAwaiter().GetResult();
    }

    private static async Task<int> PlanifierAsync(string adresse, BilanDemande demande, string fuseau)
    {
        // Un Schedule se déclenche lundi matin et vise, à ce moment-là, la
        // semaine qui vient de se clore — jamais une semaine figée à la
        // création : --week n'a donc pas de sens ici.
        if (demande.Semaine is not null)
        {
            Console.Error.WriteLine("  --planifier ne prend pas --week : un Schedule vise toujours la dernière semaine close, au moment où il se déclenche.");
            return 1;
        }

        var client = await ConnecterAsync(adresse);
        if (client is null) return 2;

        var etat = await PlanificationBilan.CreerOuMettreAJourAsync(client, demande, fuseau);
        Console.WriteLine($"\n  Planification {BilanContrats.IdPlanification} {etat}.");
        Console.WriteLine($"    tous les lundis à 8 h ({fuseau}) · file {BilanContrats.FileDeTaches} · workflow {BilanContrats.NomWorkflow}");
        return 0;
    }

    private static async Task<int> DurableAsync(string adresse, BilanDemande demande, TimeSpan attente)
    {
        var client = await ConnecterAsync(adresse);
        if (client is null) return 2;

        var id = "bilan-" + (demande.Semaine ?? "derniere-close") + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        BilanResultat resultat;
        try
        {
            var handle = await client.StartWorkflowAsync(
                BilanContrats.NomWorkflow, new object?[] { demande },
                new WorkflowOptions(id, BilanContrats.FileDeTaches));

            using var delai = new CancellationTokenSource(attente);
            resultat = await handle.GetResultAsync<BilanResultat>(
                followRuns: true,
                rpcOptions: new RpcOptions { CancellationToken = delai.Token });
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"  Le délai d'attente ({attente.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture)} min, --attente) est dépassé : le workflow {id} continue côté serveur (aucun worker actif ?).");
            return 2;
        }
        catch (WorkflowFailedException ex)
        {
            Console.Error.WriteLine($"  Le bilan durable a échoué : {ex.InnerException?.Message ?? ex.Message}");
            return 1;
        }

        // Une semaine vide : mêmes messages et même code que le bilan ordinaire.
        if (resultat.Vide)
        {
            Console.Error.WriteLine(demande.Semaine is null
                ? "  Aucune tâche sur la dernière semaine close — rien à bilanter."
                : $"  Aucune tâche sur {demande.Semaine} — rien à bilanter.");
            Console.Error.WriteLine("  Vérifiez --root, ou visez une autre semaine avec --week 2026-W34.");
            return 1;
        }

        Console.WriteLine($"\n  Bilan {resultat.Semaine}");
        Console.WriteLine($"    page   {resultat.CheminPage}   {resultat.EtatPage}");
        Console.WriteLine($"    texte  {resultat.CheminTexte}   {resultat.EtatTexte}");

        if (resultat.VersionPrecedente is not null)
            Console.WriteLine($"    version précédente conservée dans {resultat.VersionPrecedente}");

        if (resultat.SourceCritique is not null)
            Console.WriteLine($"    prompt de la semaine critiqué ({resultat.SourceCritique}), {resultat.CriteresManquants} critère(s) manquant(s)");

        if (resultat.CritiqueEnRepli)
            Console.WriteLine("    ⚠ critique en repli : le juge n'a pas répondu, critique hors ligne conservée.");

        return 0;
    }

    private static async Task<ITemporalClient?> ConnecterAsync(string adresse)
    {
        try
        {
            return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions { TargetHost = adresse });
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"  Temporal injoignable sur {adresse} — lancez scripts/temporal-local.ps1 puis le worker : dotnet run --project src/CoachingIA.Orchestration");
            return null;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
