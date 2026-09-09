using System.Diagnostics;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// La sonde : ce harnais relancé sur lui-même.
///
/// Mettre <c>ClaudeCli</c> à l'épreuve demande un vrai processus — un tube ne
/// se bouche pas en mémoire. Le seul exécutable dont on soit sûr sur n'importe
/// quel poste, hors ligne, c'est celui qui tourne. On lui passe donc un
/// marqueur dans l'instruction, et il joue l'enfant qu'on lui demande plutôt
/// que la suite de vérifications.
/// </summary>
public static class SondeCli
{
    public const string Marqueur = "SONDE-CLI";

    /// <summary>Écrit beaucoup sur la sortie d'erreur avant de répondre sur la sortie standard.</summary>
    public const string Tube = Marqueur + " TUBE";

    /// <summary>Sort en échec, avec une phrase sur la sortie d'erreur.</summary>
    public const string Code = Marqueur + " CODE";

    /// <summary>Ne répond jamais : c'est le délai qui doit trancher.</summary>
    public const string Lent = Marqueur + " LENT";

    /// <summary>Le volume écrit sur la sortie d'erreur, très au-delà d'un tube système.</summary>
    public const int Volume = 256 * 1024;

    public static int Jouer(string[] args)
    {
        var instruction = args.FirstOrDefault(a => a.Contains(Marqueur, StringComparison.Ordinal)) ?? "";

        if (instruction.Contains("TUBE", StringComparison.Ordinal))
        {
            Console.Error.Write(new string('x', Volume));
            Console.Error.Flush();
            Console.Out.Write("""{"ok":true,"structured_output":{"rewrite":"réécrit par la sonde"}}""");
            return 0;
        }

        if (instruction.Contains("CODE", StringComparison.Ordinal))
        {
            Console.Error.Write("refus de la sonde");
            return 7;
        }

        if (instruction.Contains("LENT", StringComparison.Ordinal))
        {
            Thread.Sleep(TimeSpan.FromSeconds(120));
            return 0;
        }

        Console.Error.Write("sonde inconnue : " + instruction);
        return 9;
    }
}

/// <summary>Un CLI qui ne lance rien : de quoi éprouver ce qui lit sa réponse.</summary>
public sealed class CliFactice(string? reponse, string? erreur = null, bool disponible = true) : IClaudeCli
{
    public int Appels { get; private set; }
    public string? DerniereInstruction { get; private set; }
    public bool EstDisponible => disponible;
    public string? DerniereErreur => erreur;

    public string? Demander(string instruction, string? schema)
    {
        Appels++;
        DerniereInstruction = instruction;
        return reponse;
    }
}

/// <summary>
/// L'appel à <c>claude -p</c>, en un seul endroit.
///
/// Ce qui est éprouvé ici n'est pas le modèle — c'est la plomberie : un tube
/// qui se remplit, un code de sortie, un délai. Trois façons pour un outil de
/// coaching de devenir un point de panne, ce qu'il ne doit jamais être.
/// </summary>
public static class ClaudeCliTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Un binaire absent");
        var absent = new ClaudeCli(TimeSpan.FromSeconds(5), "binaire-qui-nexiste-pas");
        check(!absent.EstDisponible, "un exécutable introuvable est vu avant qu'on tente de le lancer");
        check(absent.Demander("peu importe", null) is null, "l'appel rend la main plutôt que de lever");
        check(absent.DerniereErreur is { Length: > 0 },
              $"l'erreur est retenue pour être expliquée : {absent.DerniereErreur}");

        var sonde = Environment.ProcessPath;
        if (sonde is null || !File.Exists(sonde))
        {
            check(false, "la sonde est lançable (chemin de l'exécutable de test introuvable)");
            return;
        }

        Console.WriteLine("\nUne sortie d'erreur volumineuse");
        var cli = new ClaudeCli(TimeSpan.FromSeconds(15), sonde);
        var chrono = Stopwatch.StartNew();
        var sortie = cli.Demander(SondeCli.Tube, null);
        chrono.Stop();
        check(sortie is not null && sortie.Contains("\"ok\":true", StringComparison.Ordinal),
              $"{SondeCli.Volume / 1024} Kio sur la sortie d'erreur ne bloquent pas la réponse "
              + $"(obtenu en {chrono.Elapsed.TotalSeconds:F1} s : {Court(sortie) ?? "rien, " + cli.DerniereErreur})");
        check(cli.DerniereErreur is null, $"un appel qui aboutit ne laisse aucune erreur derrière lui ({cli.DerniereErreur})");

        Console.WriteLine("\nUn échec du CLI");
        var rate = new ClaudeCli(TimeSpan.FromSeconds(15), sonde);
        check(rate.Demander(SondeCli.Code, null) is null, "un code de sortie non nul ne rend aucune sortie");
        check(rate.DerniereErreur is { } e && e.Contains('7') && e.Contains("refus de la sonde", StringComparison.Ordinal),
              $"l'erreur cite le code et ce que l'enfant a dit : {rate.DerniereErreur}");

        Console.WriteLine("\nUn CLI qui ne répond pas");
        var lent = new ClaudeCli(TimeSpan.FromSeconds(2), sonde);
        var attente = Stopwatch.StartNew();
        var jamais = lent.Demander(SondeCli.Lent, null);
        attente.Stop();
        check(jamais is null && lent.DerniereErreur is { } d && d.Contains("délai", StringComparison.Ordinal),
              $"le délai tranche plutôt que d'attendre indéfiniment ({lent.DerniereErreur})");
        check(attente.Elapsed < TimeSpan.FromSeconds(30),
              $"et la main revient peu après le délai, sans attendre la fin de l'enfant (obtenu {attente.Elapsed.TotalSeconds:F1} s)");

        Console.WriteLine("\nUn seul endroit qui lance le processus");
        check(RecoitUnCli(typeof(ClaudePromptCritic)),
              "la critique rédigée reçoit son CLI au lieu de le fabriquer");
        check(RecoitUnCli(typeof(SceneWriter)),
              "l'écriture de scènes reçoit le même — la plomberie ne vit plus en double");

        Console.WriteLine("\nCe que la critique fait de la réponse");
        var repondu = new CliFactice("""{"structured_output":{"rewrite":"Corrige le segmenteur, fini quand les tests passent.","missing":[]}}""");
        var critique = new ClaudePromptCritic(new HeuristicPromptCritic(), repondu)
            .Critique("refais le truc", Contexte());
        check(critique is not null && critique.Source == "claude -p" && critique.Rewrite.StartsWith("Corrige", StringComparison.Ordinal),
              $"une réponse bien formée remplace la réécriture hors ligne (obtenu « {Court(critique?.Rewrite)} » de {critique?.Source})");

        var muet = new CliFactice(null, "le juge n'a pas répondu");
        var repli = new ClaudePromptCritic(new HeuristicPromptCritic(), muet).Critique("refais le truc", Contexte());
        check(repli is not null && repli.Source == "heuristique",
              $"une panne du juge rend la critique hors ligne, jamais rien (obtenu {repli?.Source ?? "rien"})");

        var complet = new ClaudePromptCritic(new HeuristicPromptCritic(), repondu).Critique(
            "Ajoute EstReferentiel à TaskSegmenter.cs, sans changer la fenêtre de continuation, "
            + "et vérifie que les tests passent. Renvoie un patch. "
            + "Contexte : la brique d'évaluation en a besoin. Va jusqu'au bout sans me demander.", Contexte());
        check(complet is null && repondu.Appels == 1,
              $"un prompt déjà complet n'appelle pas le juge du tout (obtenu {repondu.Appels} appel(s))");
    }

    private static PromptContext Contexte()
        => new("le segmenteur", 3, 12, false, false, [], new DateOnly(2026, 9, 8));

    private static bool RecoitUnCli(Type type)
        => type.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IClaudeCli)));

    private static string? Court(string? texte)
        => texte is null ? null : texte.Length <= 60 ? texte : texte[..60] + "…";
}
