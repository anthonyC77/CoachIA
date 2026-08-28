using System.Text.Json;
using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai des variantes. Deux promesses, et elles tirent dans des
/// directions opposées : les images doivent <em>changer</em> d'une semaine à
/// l'autre, et rester <em>identiques</em> pour une même semaine — sans quoi
/// l'archivage des bilans verrait une différence à chaque régénération.
/// </summary>
public static class VariantTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        Console.WriteLine("Lecture du format");
        var lens = JsonSerializer.Deserialize<Lens>("""
            {
              "id": "essai", "name": "Essai",
              "signals": {
                "une_seule": "la scène unique",
                "trois": ["première", "deuxième", "troisième"],
                "vide": null,
                "blanc": ["", "   "]
              }
            }
            """, Lens.Json)!;

        check(lens.Signals["une_seule"].Length == 1, "une chaîne devient une variante");
        check(lens.Signals["trois"].Length == 3, "un tableau en donne autant qu'il en contient");
        check(!lens.Signals.ContainsKey("vide"), "une clé nulle est ignorée plutôt que de masquer la lentille du dessous");
        check(!lens.Signals.ContainsKey("blanc"), "des chaînes vides ne comptent pas pour des scènes");

        Console.WriteLine("\nRotation");
        var variants = new[] { "a", "b", "c", "d" };
        var vus = new List<string?>();
        for (var c = 0; c < variants.Length; c++)
            vus.Add(VariantPicker.Pick(variants, "cle", c));

        check(vus.Distinct().Count() == variants.Length,
              "quatre semaines de suite donnent les quatre scènes, jamais deux fois la même");
        check(VariantPicker.Pick(variants, "cle", 4) == vus[0],
              "et la cinquième reprend au début du tour");

        check(VariantPicker.Pick(variants, "cle", 7) == VariantPicker.Pick(variants, "cle", 7),
              "un même cycle donne toujours la même scène");
        check(VariantPicker.Pick(variants, "cle", 7) != VariantPicker.Pick(variants, "cle", 8),
              "deux semaines voisines ne donnent pas la même");

        var decales = new[] { "has_acceptance_criteria", "context_pressure", "verification_present" }
            .Select(k => VariantPicker.Pick(variants, k, 0)).Distinct().Count();
        check(decales > 1, "les signaux ne tournent pas tous au même pas");

        check(VariantPicker.Pick(["seule"], "cle", 12345) == "seule", "une clé à variante unique reste stable");
        check(VariantPicker.Pick([], "cle", 3) is null, "aucune variante ne donne aucune image");
        check(VariantPicker.Pick(null, "cle", 3) is null, "et une clé absente non plus");

        Console.WriteLine("\nCycles");
        check(VariantPicker.CycleOfWeek("2026-W34") == VariantPicker.CycleOfWeek("2026-W34"),
              "une semaine ISO donne toujours le même cycle");
        check(VariantPicker.CycleOfWeek("2026-W35") - VariantPicker.CycleOfWeek("2026-W34") == 1,
              "deux semaines consécutives sont à un cran d'écart");
        // 2020 comptait 53 semaines ISO : compter en « année × 52 » ferait sauter la rotation.
        check(VariantPicker.CycleOfWeek("2021-W01") - VariantPicker.CycleOfWeek("2020-W53") == 1,
              "le passage d'une année à 53 semaines ne saute pas un cran");
        check(VariantPicker.CycleOfWeek("n'importe quoi") > 0, "une semaine illisible retombe sur aujourd'hui sans lever");

        Console.WriteLine("\nLe pack StarCraft II");
        var catalog = LensCatalog.Load(lensDir);
        var sc2 = catalog.Resolve("starcraft2");
        check(sc2.Id == "starcraft2", "le pack se charge");
        check(sc2.Patch.Length > 0, $"et annonce sa version de référence ({sc2.Patch})");

        var sansImage = SignalSpecs.All.Where(s => !sc2.Signals.ContainsKey(s.Key)).Select(s => s.Key).ToList();
        check(sansImage.Count == 0, $"chaque signal mesuré a son image ({string.Join(", ", sansImage)})");

        var zerg = sc2.Race("zerg");
        check(zerg is not null, "le camp zerg existe");
        var maigres = zerg!.Signals.Where(kv => kv.Value.Length < 2).Select(kv => kv.Key).ToList();
        check(maigres.Count == 0, $"aucun signal zerg ne se limite à une scène ({string.Join(", ", maigres)})");

        var total = sc2.Signals.Sum(kv => kv.Value.Length) + zerg.Signals.Sum(kv => kv.Value.Length);
        check(total >= 60, $"le corpus tient la distance ({total} scènes)");

        // Le patch 5.0.16 ramène le départ à huit ouvriers : les ouvertures
        // citées au supply près d'avant-patch sont devenues fausses.
        var perimes = sc2.Signals.Values.Concat(zerg.Signals.Values).SelectMany(v => v)
            .Concat(sc2.Levels.Values.Select(l => l.Analogy))
            .Concat(zerg.Levels.Values.Select(l => l.Analogy))
            .Where(t => t.Contains("17 hatch") || t.Contains("18 pool") || t.Contains("16 ouvriers"))
            .ToList();
        check(perimes.Count == 0, $"aucune ouverture d'avant-patch n'est citée au supply près ({perimes.Count} trouvée(s))");

        Console.WriteLine("\nLe camp passe devant, et la rotation aussi");
        var writer = new LensWriter(sc2, "zerg", cycle: 0);
        var image = writer.ForSignal("verification_present", "un fait");
        check(image.Fact == "un fait", "le fait reste intact");
        check(image.Flourish is not null && image.Flourish.Contains("overseer"),
              "et l'image vient bien du pack zerg");

        var semaines = Enumerable.Range(0, 4)
            .Select(c => new LensWriter(sc2, "zerg", c).ForSignal("verification_present", "f").Flourish)
            .ToList();
        check(semaines.Distinct().Count() == 4, "quatre semaines donnent quatre scènes différentes");

        Console.WriteLine("\nStabilité d'un bilan régénéré");
        var a = new LensWriter(sc2, "zerg").ForWeek("2026-W30");
        var b = new LensWriter(sc2, "zerg").ForWeek("2026-W30");
        check(a.Cycle == b.Cycle, "deux writers calés sur la même semaine partagent le cycle");
        check(a.ForSignal("context_pressure", "f").Flourish == b.ForSignal("context_pressure", "f").Flourish,
              "et racontent la même chose — sinon chaque régénération archiverait un faux changement");
        check(a.Cycle != new LensWriter(sc2, "zerg").ForWeek("2026-W31").Cycle,
              "deux semaines différentes ne partagent pas le cycle");

        Console.WriteLine("\nLes anciens packs restent lisibles");
        var echecs = catalog.Resolve("echecs");
        check(echecs.Id == "echecs", "la lentille échecs se charge encore");
        check(echecs.Signals.Count > 0, "avec son vocabulaire, écrit avant les variantes");
        check(echecs.Signals.Values.All(v => v.Length >= 1), "chaque clé y compte au moins une scène");
    }
}
