using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// L'intégrité référentielle du corpus de maturité.
///
/// C'est la suite qui n'existait pas, et son absence se voyait : trois clés
/// vivaient dans les packs sans que rien ne les réclame — <c>graph_depth</c>
/// avait une scène StarCraft II jamais affichée, <c>compaction_mode</c> un défi
/// sans grille, <c>compaction_subie</c> un moment qu'aucun détecteur n'émettait.
/// Aucun contrôle ne pouvait les attraper tant qu'il n'existait pas un endroit
/// où la liste des problématiques fasse foi.
///
/// Les vérifications ci-dessous ferment les deux sens du désalignement : ce que
/// le corpus déclare doit être mesuré, et ce qui est mesuré doit être déclaré.
/// </summary>
public static class MaturiteTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        var warnings = new List<string>();
        var corpus = MaturityCorpus.Load(lensDir, warnings);
        foreach (var w in warnings) Console.WriteLine("    (avertissement) " + w);

        check(warnings.Count == 0, "le corpus de maturité se charge depuis les données");
        check(corpus.Problems.Count > 0, $"il déclare {corpus.Problems.Count} problématiques");
        check(corpus.Signals.Count > 0, $"et {corpus.Signals.Count} signaux");

        // ------------------------------------------------- paliers et grille

        for (var level = 1; level <= 5; level++)
            check(corpus.Levels.ContainsKey(level.ToString()), $"le palier {level} est nommé");

        check(corpus.Specs.Count > 0, $"{corpus.Specs.Count} signaux portent une cible");
        check(corpus.Signals.Any(s => !s.IsGraded),
              "et certains sont mesurés sans cible — le garde-fou anti-Goodhart existe");
        check(corpus.Signals.Where(s => !s.IsGraded).All(s => s.Target is null),
              "un signal sans cible n'en porte vraiment aucune");

        // ---------------------------------------- ce qui est mesuré est déclaré

        // La liste que l'extracteur produit réellement, prise à la source plutôt
        // que recopiée : une recopie se serait désynchronisée exactement comme
        // le reste.
        var emitted = EmittedKeys();
        var declared = corpus.Signals.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var undeclared = emitted.Where(k => !declared.Contains(k)).ToList();
        check(undeclared.Count == 0,
              $"tout signal mesuré est déclaré dans le corpus ({string.Join(", ", undeclared)})");

        // Un signal déclaré « transcripts » que rien n'émet est une promesse en
        // l'air : le bilan l'attendrait pour toujours.
        var promised = corpus.Signals
            .Where(s => s.Source == "transcripts" && !emitted.Contains(s.Key))
            .Select(s => s.Key).ToList();
        check(promised.Count == 0,
              $"tout signal déclaré « transcripts » est réellement émis ({string.Join(", ", promised)})");

        // ------------------------------------------------- les problématiques

        var grouped = corpus.Problems.SelectMany(p => p.Signals).ToList();
        var duplicated = grouped.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        check(duplicated.Count == 0,
              $"aucun signal n'appartient à deux problématiques ({string.Join(", ", duplicated)})");

        var unknown = grouped.Where(k => !declared.Contains(k)).ToList();
        check(unknown.Count == 0,
              $"toute problématique ne cite que des signaux déclarés ({string.Join(", ", unknown)})");

        var orphanSignals = corpus.Signals.Where(s => corpus.ProblemOf(s.Key) is null).Select(s => s.Key).ToList();
        check(orphanSignals.Count == 0,
              $"tout signal appartient à une problématique ({string.Join(", ", orphanSignals)})");

        check(corpus.Problems.All(p => p.Level is >= 1 and <= 5), "chaque problématique tient dans les cinq paliers");
        check(corpus.Problems.All(p => p.Title.Length > 0), "chaque problématique porte un titre");

        var badIds = corpus.Problems.Where(p => p.Id.Length == 0).ToList();
        check(badIds.Count == 0, "chaque problématique porte un identifiant");
        check(corpus.Problems.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == corpus.Problems.Count,
              "les identifiants de problématique sont uniques");

        // ------------------------------------------------------------ défis

        foreach (var p in corpus.Problems.Where(p => p.Challenge is not null))
        {
            var d = p.Challenge!;
            check(declared.Contains(d.SignalKey), $"le défi de « {p.Id} » vise un signal déclaré ({d.SignalKey})");
            check(d.Target is not null || corpus.Spec(d.SignalKey) is not null,
                  $"le défi de « {p.Id} » sait à quel seuil se juger");
            check(d.Statement.Length > 0 && d.Verification.Length > 0,
                  $"le défi de « {p.Id} » dit quoi faire et comment on saura");
        }

        // ------------------------------------------------------------ moments

        var emittedMoments = new[] { "rework", "abandon", "tool_failures", "no_verification" };
        var silent = corpus.Moments
            .Where(m => m.Source == "transcripts" && !emittedMoments.Contains(m.Key))
            .Select(m => m.Key).ToList();
        check(silent.Count == 0,
              $"tout moment « transcripts » est émis par MomentDetector ({string.Join(", ", silent)})");

        foreach (var key in emittedMoments)
            check(corpus.Moment(key) is not null, $"le moment « {key} » a un texte dans le corpus");

        var unfilled = corpus.Moments.Where(m => m.Text.Contains("{tache}", StringComparison.Ordinal)).ToList();
        var filled = corpus.MomentText("rework", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tache"] = "Squelette opérationnel", ["reprises"] = "4",
        });
        check(unfilled.Count > 0 && filled is not null && !filled.Contains('{'),
              "les marques d'un moment sont remplies, pas laissées en clair");
        check(filled!.Contains("Squelette opérationnel") && filled.Contains('4'),
              "et remplies avec les bonnes valeurs");
        check(corpus.MomentText("moment_qui_nexiste_pas", new Dictionary<string, string>()) is null,
              "un moment inconnu rend le silence plutôt qu'une phrase à trous");

        // --------------------------------------------- les scènes des packs

        // Le contrôle qui manquait à graph_depth : une scène dont la clé n'est
        // ni une problématique ni un signal n'est jamais affichée, et personne
        // ne s'en aperçoit.
        var validKeys = declared.Concat(corpus.Problems.Select(p => p.Id)).ToHashSet(StringComparer.Ordinal);
        var catalog = LensCatalog.Load(lensDir);

        foreach (var lens in catalog.All.Where(l => l.Signals.Count > 0 || l.Races.Count > 0))
        {
            var strays = lens.Signals.Keys
                .Concat(lens.Races.SelectMany(r => r.Value.Signals.Keys))
                .Distinct(StringComparer.Ordinal)
                .Where(k => !validKeys.Contains(k))
                .ToList();
            check(strays.Count == 0,
                  $"« {lens.Id} » : aucune scène sur une clé inconnue ({string.Join(", ", strays)})");

            var challengeStrays = lens.Challenges.Keys
                .Concat(lens.Races.SelectMany(r => r.Value.Challenges.Keys))
                .Distinct(StringComparer.Ordinal)
                .Where(k => !validKeys.Contains(k))
                .ToList();
            check(challengeStrays.Count == 0,
                  $"« {lens.Id} » : aucun défi sur une clé inconnue ({string.Join(", ", challengeStrays)})");

            var momentKeys = corpus.Moments.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
            var momentStrays = lens.Moments.Keys
                .Concat(lens.Races.SelectMany(r => r.Value.Moments.Keys))
                .Distinct(StringComparer.Ordinal)
                .Where(k => !momentKeys.Contains(k))
                .ToList();
            check(momentStrays.Count == 0,
                  $"« {lens.Id} » : aucun moment sur une clé inconnue ({string.Join(", ", momentStrays)})");
        }

        // ------------------------------------------------- le repli intégré

        var builtIn = MaturityCorpus.BuiltIn;
        check(builtIn.Specs.Count == corpus.Specs.Count,
              $"le repli intégré note autant de signaux que les données ({builtIn.Specs.Count} contre {corpus.Specs.Count})");
        check(builtIn.Problems.Count == corpus.Problems.Count,
              "et déclare autant de problématiques");
        check(MaturityCorpus.Load(Path.Combine(Path.GetTempPath(), "dossier-qui-nexiste-pas")).Specs.Count > 0,
              "sans dossier de lentilles, le corpus intégré répond quand même");

        // Un corpus vide ne doit jamais prendre la place du complet.
        var vide = Path.Combine(Path.GetTempPath(), "coachingia-maturite-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(vide);
        try
        {
            File.WriteAllText(Path.Combine(vide, MaturityCorpus.FileName), """{ "version": "1" }""");
            var casse = new List<string>();
            var replié = MaturityCorpus.Load(vide, casse);
            check(casse.Count == 1 && replié.Specs.Count > 0,
                  "un corpus incomplet est refusé et signalé, le repli garde la main");

            File.WriteAllText(Path.Combine(vide, MaturityCorpus.FileName), "{ pas du json");
            casse.Clear();
            check(MaturityCorpus.Load(vide, casse).Specs.Count > 0 && casse.Count == 1,
                  "un corpus illisible ne fait pas taire le coach");
        }
        finally { Directory.Delete(vide, recursive: true); }

        // --------------------------------------- la façade sert les données

        SignalSpecs.Use(corpus);
        check(SignalSpecs.All.Count == corpus.Specs.Count, "SignalSpecs sert le corpus monté");
        check(SignalSpecs.Find("verification_present") is not null, "et retrouve un signal noté");
        check(SignalSpecs.Find("mcp_utilization") is null,
              "un signal sans cible n'entre pas dans la grille : il ne reproche rien");
        check(SignalSpecs.Corpus.ProblemOf("verification_present") == "rien_ne_prouve",
              "un signal sait de quelle problématique il relève");
    }

    /// <summary>
    /// Les clés que <see cref="SignalExtractor"/> produit vraiment, obtenues en
    /// le faisant tourner plutôt qu'en recopiant une liste — une recopie se
    /// serait désynchronisée exactement comme le reste.
    /// </summary>
    private static HashSet<string> EmittedKeys()
    {
        var session = ReviewTests.Session(
            ReviewTests.At(ReviewTests.ThisMonday().AddDays(-7), 1), sloppy: true, tasks: 1);
        var task = new TaskSegmenter().Segment(session)[0];
        return new SignalExtractor().ForTask(task, session)
            .Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
    }
}
