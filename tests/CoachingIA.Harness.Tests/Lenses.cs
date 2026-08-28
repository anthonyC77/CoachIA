using System.Text.Json;
using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai de la lentille. L'essentiel de ce qui est vérifié ici n'est pas
/// du comportement mais une garantie : que la métaphore ne puisse jamais
/// remplacer le fait, ni un pack de vocabulaire incomplet faire taire le coach.
/// </summary>
public static class LensTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        Console.WriteLine("Chargement");
        var warnings = new List<string>();
        var catalog = LensCatalog.Load(lensDir, warnings);
        check(warnings.Count == 0, $"les lentilles livrées se chargent sans avertissement ({string.Join(" ; ", warnings)})");
        check(catalog.Knows("starcraft2") && catalog.Knows("echecs") && catalog.Knows("neutre"),
              "les trois lentilles sont disponibles");
        check(catalog.Resolve("nexistepas").Id == "neutre", "une lentille inconnue retombe sur le neutre");
        check(catalog.Resolve(null).Id == "neutre", "aucune lentille demandée = neutre");

        Console.WriteLine("\nLe fait passe devant");
        var sc2 = new LensWriter(catalog.Resolve("starcraft2"));
        var msg = sc2.ForSignal("context_pressure", "pic de contexte : 162 000 jetons");
        check(msg.ToString().StartsWith("pic de contexte"), $"la phrase commence par le fait : {msg}");
        check(msg.ToString().Contains("—"), "l'image est séparée du fait par un tiret");
        check(msg.Plain == "pic de contexte : 162 000 jetons", "le fait seul reste disponible");

        var neutral = new LensWriter(catalog.Resolve("neutre"));
        var plain = neutral.ForSignal("context_pressure", "pic de contexte : 162 000 jetons");
        check(plain.ToString() == plain.Plain, "en neutre, la phrase est exactement le fait");
        check(!plain.ToString().Contains("—"), "aucun tiret orphelin quand l'image manque");

        var missing = sc2.ForSignal("signal_qui_nexiste_pas", "un fait");
        check(missing.ToString() == "un fait", "une clé absente ne produit ni exception ni tiret vide");

        Console.WriteLine("\nIntégrité du contenu");
        foreach (var lens in catalog.All)
        {
            for (var level = 1; level <= 5; level++)
            {
                var l = lens.Levels.GetValueOrDefault(level.ToString());
                var ok = lens.Id == "neutre" ? l is not null && l.Analogy.Length > 0
                                             : l is null || l.Term.Length == 0 || l.Analogy.Length > 0;
                check(ok, $"{lens.Id} palier {level} : un terme sans analogie serait du décor");
            }
            check(lens.Levels.Count == 0 || lens.Levels.Count == 5,
                  $"{lens.Id} : les cinq paliers sont couverts, ou aucun");
        }

        var known = new[] { "has_acceptance_criteria", "compaction_mode", "verification_present", "loop_closure", "self_correction" };
        foreach (var lens in catalog.All)
            foreach (var key in lens.Challenges.Keys)
                check(known.Contains(key), $"{lens.Id} : le défi « {key} » correspond à un signal réel");

        Console.WriteLine("\nChoix du défi");
        var library = new ChallengeLibrary();
        var weakEverywhere = new Dictionary<string, double>
        {
            ["has_acceptance_criteria"] = 0.1, ["verification_present"] = 0.2,
            ["loop_closure"] = 0.1, ["self_correction"] = 0.0,
        };
        var picked = library.Pick(weakEverywhere, sc2);
        check(picked?.Level == 1, $"le plus bas palier faible est visé d'abord (obtenu {picked?.Level})");
        check(picked!.Render().StartsWith("Lancez trois"), "l'énoncé neutre ouvre la phrase, l'image suit");
        check(picked.Render().Contains("build order"), "la lentille StarCraft II habille bien le défi");

        var onlyLoopWeak = new Dictionary<string, double>
        {
            ["has_acceptance_criteria"] = 0.9, ["verification_present"] = 0.9, ["loop_closure"] = 0.1,
        };
        check(library.Pick(onlyLoopWeak, sc2)?.Level == 4, "un palier haut est visé quand les bas tiennent");

        var allStrong = new Dictionary<string, double>
        {
            ["has_acceptance_criteria"] = 0.9, ["verification_present"] = 0.9,
            ["loop_closure"] = 0.9, ["self_correction"] = 0.9,
        };
        check(library.Pick(allStrong, sc2) is null, "aucun défi inventé quand tout est au-dessus de la cible");

        var hookOnly = new Dictionary<string, double> { ["compaction_mode"] = 0.1 };
        check(library.Pick(hookOnly, sc2, hooksAvailable: false) is null,
              "un défi qui exige les hooks n'est pas proposé sans eux");
        check(library.Pick(hookOnly, sc2, hooksAvailable: true)?.Level == 2,
              "et il l'est dès que les hooks sont là");

        check(library.Pick(new Dictionary<string, double> { ["loop_closure"] = double.NaN }, sc2) is null,
              "un signal indéterminé ne déclenche pas de défi");

        Console.WriteLine("\nMoment opportun");
        var detector = new MomentDetector();
        var noisy = TaskWith(rework: 4, calls: 12, completed: true);
        var m = detector.Detect(noisy, [], sc2);
        check(m is not null && m.Value.ToString().Contains("4 reprises"), "quatre reprises déclenchent une observation");
        check(m!.Value.ToString().StartsWith("Hier"), "l'observation commence par le fait, pas par l'image");
        check(m.Value.ToString().Contains("build"), "et l'image StarCraft II vient après");

        var calm = TaskWith(rework: 0, calls: 3, completed: true);
        var quiet = detector.Detect(calm, [new Signal("verification_present", 3, 1, "test lancé")], sc2);
        check(quiet is null, "une session ordinaire ne déclenche rien — le silence est la règle");

        var neutralMoment = detector.Detect(noisy, [], neutral);
        check(neutralMoment is not null && !neutralMoment.Value.ToString().Contains("—"),
              "en neutre, la même observation tient sans métaphore");

        Console.WriteLine("\nPack malformé");
        var dir = Path.Combine(Path.GetTempPath(), "lens-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "casse.json"), "{ceci n'est pas du json");
            File.WriteAllText(Path.Combine(dir, "sansid.json"), "{\"name\":\"Sans identifiant\"}");
            var w2 = new List<string>();
            var c2 = LensCatalog.Load(dir, w2);
            check(w2.Count == 2, $"les deux fichiers fautifs sont signalés (obtenu {w2.Count})");
            check(c2.Resolve("nimporte").Id == "neutre", "et le coach continue de parler, en neutre");
            check(c2.Neutral.Levels.Count == 5, "la lentille neutre de secours est complète");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine("\nDossier absent");
        var empty = LensCatalog.Load(Path.Combine(Path.GetTempPath(), "nexiste-pas-" + Guid.NewGuid().ToString("N")[..6]));
        check(empty.Resolve("starcraft2").Id == "neutre", "sans dossier de lentilles, tout retombe en neutre");
    }

    private static SegmentedTask TaskWith(int rework, int calls, bool completed)
    {
        var task = new SegmentedTask { Id = "t", SessionId = "s", Title = "Corriger le parseur de hooks" };
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i <= rework; i++)
        {
            var turn = new Turn
            {
                SessionId = "s", Prompt = "p" + i,
                StartedAt = at.AddMinutes(i), EndedAt = at.AddMinutes(i + 1),
                StopReason = completed && i == rework ? "end_turn" : "end_turn",
            };
            if (i == rework)
                for (var c = 0; c < calls; c++)
                    turn.ToolCalls.Add(new ToolCall { Id = "c" + c, Name = "Bash", CalledAt = at });
            task.Turns.Add(turn);
        }
        return task;
    }
}
