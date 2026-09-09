using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Evaluation;

/// <summary>
/// La commande <c>evaluer</c> : jouer la campagne hors du harnais de tests.
///
/// <para>Elle joue exactement la même composition que la porte des
/// vérifications — <c>CampagneStandard</c> — parce que deux listes recopiées
/// auraient divergé, et que la commande aurait alors rassuré sur autre chose
/// que ce que la porte garde.</para>
///
/// <para>Elle <strong>n'écrit jamais</strong> <c>evals/verdict.json</c>.
/// Réapprouver est un geste humain : il passe par
/// <c>COACHINGIA_APPROUVER_EVALS</c> et le harnais, depuis un terminal. Une
/// seconde porte ici, que le hook ne surveille pas, viderait la première de son
/// sens.</para>
///
/// <para>Codes de sortie, lus de la même façon par la CI et par un humain :
/// 0 rien n'a bougé, 1 un écart à corriger ou à approuver, 2 la campagne n'a
/// pas pu conclure.</para>
/// </summary>
public static class EvaluerCommand
{
    public static int Run(string[] args, string lensDir)
    {
        var racine = Valeur(args, "--racine") ?? Path.GetDirectoryName(lensDir.TrimEnd(Path.DirectorySeparatorChar))
                     ?? Environment.CurrentDirectory;
        var dossierCas = Valeur(args, "--cas") ?? Path.Combine(racine, "evals", "cas");
        var cheminVerdict = Valeur(args, "--verdict") ?? Path.Combine(racine, "evals", "verdict.json");

        var jeu = JeuEpreuves.Charger(dossierCas, exigerPartageable: true);
        foreach (var a in jeu.Avertissements) Console.Error.WriteLine("⚠ " + a);

        var evaluateurs = new List<IEvaluateur>(CampagneStandard.Porte());

        // Le juge ne vient que si on le demande : il coûte un appel par épreuve
        // et change d'avis. Absent, la campagne reste entière — elle mesure
        // simplement une chose de moins.
        if (Array.IndexOf(args, "--juge") >= 0)
        {
            var cli = new ClaudeCli(TimeSpan.FromSeconds(90));
            if (!cli.EstDisponible)
                Console.Error.WriteLine("⚠ « claude » introuvable dans le PATH : la campagne tourne sans juge.");
            else
                evaluateurs.AddRange(CampagneStandard.Juges(cli));
        }

        var resultat = new Campagne(CampagneStandard.Producteurs(lensDir), evaluateurs).Jouer(jeu);

        var avertissements = new List<string>();
        var approuve = VerdictApprouve.Lire(cheminVerdict, avertissements);
        foreach (var a in avertissements) Console.Error.WriteLine("⚠ " + a);

        var rapport = Porte.Juger(resultat, approuve, evaluateurs);

        Console.WriteLine($"\n  Campagne d'évaluation — {jeu.Fichiers.Count} fichier(s) de cas, {jeu.Epreuves.Count} épreuve(s)\n");
        Console.Write(Porte.Rendre(resultat, rapport));

        Accord(resultat, evaluateurs);

        Console.WriteLine();
        Console.WriteLine(rapport.Code switch
        {
            0 => "  Rien n'a bougé depuis l'état approuvé.",
            1 => "  Des écarts sont à trancher. Corriger le code, ou approuver l'état courant\n"
               + "  depuis un terminal : $env:COACHINGIA_APPROUVER_EVALS = \"true\" puis relancer\n"
               + "  dotnet run --project tests/CoachingIA.Harness.Tests.",
            _ => "  La campagne n'a pas pu conclure — voir les avertissements ci-dessus.",
        });

        return rapport.Code;
    }

    /// <summary>
    /// Ce que le juge a dit du code. Affiché à part, et jamais mêlé au verdict :
    /// un désaccord désigne un endroit à regarder, il ne prononce rien.
    /// </summary>
    private static void Accord(ResultatCampagne resultat, List<IEvaluateur> evaluateurs)
    {
        var juges = evaluateurs.Where(e => !e.Deterministe).Select(e => e.Nom).ToHashSet(StringComparer.Ordinal);
        if (juges.Count == 0) return;

        var lignes = resultat.Verdicts.Where(l => juges.Contains(l.Verdict.Evaluateur)).ToList();
        if (lignes.Count == 0) return;

        Console.WriteLine("\n  Ce que le juge dit du code");
        foreach (var groupe in lignes.GroupBy(l => l.Verdict.Evaluateur, StringComparer.Ordinal)
                                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var accord = groupe.Count(l => l.Verdict.Etiquette == "accord");
            var desaccord = groupe.Count(l => l.Verdict.Etiquette == "desaccord");
            var doute = groupe.Count(l => l.Verdict.Etiquette == "douteux");
            var indecis = groupe.Count(l => l.Verdict.Indecis);
            Console.WriteLine($"    {groupe.Key,-28} {accord} accord(s), {desaccord} désaccord(s), {doute} doute(s), {indecis} sans réponse");

            foreach (var l in groupe.Where(l => l.Verdict.Etiquette == "desaccord")
                                    .OrderBy(l => l.Epreuve, StringComparer.Ordinal))
                Console.WriteLine($"      {l.Epreuve} — {l.Verdict.Explication} « {l.Verdict.Preuve} »");
        }
    }

    private static string? Valeur(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
