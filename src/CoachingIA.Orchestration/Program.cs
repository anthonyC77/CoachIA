using Temporalio.Client;
using CoachingIA.Orchestration;

// Point d'entrée provisoire du Worker Temporal du bilan durable. Pour
// l'instant, seule la vérification de connexion est câblée ; le Worker
// hébergé (AddHostedTemporalWorker), le workflow BilanHebdo et ses
// activities arrivent dans une tâche ultérieure et remplaceront ce
// comportement par défaut.

const int DelaiVerificationSecondes = 10;

if (args.Contains("--verifier"))
    return await VerifierAsync(args);

AfficherAide();
return 1;

static void AfficherAide()
{
    Console.WriteLine("coachingia-orchestration — Worker Temporal du bilan durable (pas encore hébergé).");
    Console.WriteLine();
    Console.WriteLine("Usage :");
    Console.WriteLine("  coachingia-orchestration --verifier [--temporal <hôte:port>] [--espace <nom>]");
    Console.WriteLine();
    Console.WriteLine($"  --temporal   adresse gRPC du serveur Temporal (défaut {BilanContrats.AdresseParDefaut})");
    Console.WriteLine($"  --espace     espace de noms Temporal (défaut {BilanContrats.EspaceParDefaut})");
}

static async Task<int> VerifierAsync(string[] args)
{
    var adresse = Arg(args, "--temporal") ?? BilanContrats.AdresseParDefaut;
    var espace = Arg(args, "--espace") ?? BilanContrats.EspaceParDefaut;

    try
    {
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(DelaiVerificationSecondes));

        var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
        {
            TargetHost = adresse,
            Namespace = espace,
        });

        var joignable = await client.Connection.CheckHealthAsync(options: new RpcOptions
        {
            Timeout = TimeSpan.FromSeconds(DelaiVerificationSecondes),
            CancellationToken = delai.Token,
        });

        if (joignable)
        {
            Console.WriteLine($"Temporal joignable sur {adresse}");
            return 0;
        }

        Console.Error.WriteLine($"Temporal injoignable sur {adresse} — lancez scripts/temporal-local.ps1");
        return 2;
    }
    catch (Exception)
    {
        Console.Error.WriteLine($"Temporal injoignable sur {adresse} — lancez scripts/temporal-local.ps1");
        return 2;
    }
}

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
