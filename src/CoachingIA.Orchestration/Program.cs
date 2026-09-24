using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using CoachingIA.Harness.Core;
using CoachingIA.Orchestration;

// Point d'entrée du Worker Temporal du bilan durable. --verifier ne fait que
// sonder la connexion, sans rien héberger ; sans argument, le Worker démarre
// pour de bon, avec le workflow BilanHebdo et les quatre activities de
// BilanActivities enregistrés.

const int DelaiVerificationSecondes = 10;

if (args.Contains("--verifier"))
    return await VerifierAsync(args);

if (args.Contains("--aide") || args.Contains("--help"))
{
    AfficherAide();
    return 0;
}

await HebergerAsync(args);
return 0;

static void AfficherAide()
{
    Console.WriteLine("coachingia-orchestration — Worker Temporal du bilan durable.");
    Console.WriteLine();
    Console.WriteLine("Usage :");
    Console.WriteLine("  coachingia-orchestration [--temporal <hôte:port>] [--espace <nom>]");
    Console.WriteLine("  coachingia-orchestration --verifier [--temporal <hôte:port>] [--espace <nom>]");
    Console.WriteLine();
    Console.WriteLine($"  --temporal   adresse gRPC du serveur Temporal (défaut {BilanContrats.AdresseParDefaut})");
    Console.WriteLine($"  --espace     espace de noms Temporal (défaut {BilanContrats.EspaceParDefaut})");
}

// Héberge le Worker pour de bon : file coachingia-bilan, workflow BilanHebdo
// et activities du bilan enregistrées en singleton. IClaudeCli est lui aussi
// en singleton — un seul binaire à sonder, partagé entre toutes les
// exécutions de Critiquer.
static async Task HebergerAsync(string[] args)
{
    var adresse = Arg(args, "--temporal") ?? BilanContrats.AdresseParDefaut;
    var espace = Arg(args, "--espace") ?? BilanContrats.EspaceParDefaut;

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSingleton<IClaudeCli>(new ClaudeCli());
    builder.Services
        .AddHostedTemporalWorker(adresse, espace, BilanContrats.FileDeTaches)
        .AddWorkflow<BilanHebdoWorkflow>()
        .AddSingletonActivities<BilanActivities>();

    using var host = builder.Build();
    await host.RunAsync();
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
