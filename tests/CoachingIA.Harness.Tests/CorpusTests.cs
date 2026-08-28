using System.Text.Json;
using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai du corpus et du validateur.
///
/// Ce qui est vérifié ici est le contrat du garde-fou : il doit attraper les
/// erreurs qu'on commet vraiment en écrivant un pack — une unité inventée, une
/// unité du mauvais camp donnée au joueur, une menace invisible qu'on ne nomme
/// pas, une ouverture d'avant-patch — et se taire sur tout le reste. Un
/// validateur qui crie pour des questions de goût finit désactivé, et un
/// validateur désactivé ne protège de rien.
/// </summary>
public static class CorpusTests
{
    public static void Run(Action<bool, string> check, string lensDir)
    {
        var corpus = Corpus.Load(lensDir, "starcraft2");

        Console.WriteLine("Le corpus");
        check(corpus is not null, "le corpus StarCraft II se charge");
        check(corpus!.Patch.Version.Length > 0, $"il annonce son patch de référence ({corpus.Patch.Version})");
        check(corpus.Patch.Obsolete.Length > 0, "et ce que ce patch a rendu faux");
        check(corpus.Units.Count >= 40, $"il connaît assez d'unités pour écrire ({corpus.Units.Count})");
        check(corpus.Buildings.Count >= 20, $"et les bâtiments qui datent les scènes ({corpus.Buildings.Count})");
        check(corpus.Punishments.Count >= 20, $"la table des punitions est fournie ({corpus.Punishments.Count})");

        check(corpus.Units.All(u => u.Race is "zerg" or "terran" or "protoss"),
              "chaque unité appartient à une race connue");
        check(corpus.Units.Select(u => u.Name).Distinct().Count() == corpus.Units.Count,
              "aucune unité n'est déclarée deux fois");
        check(corpus.Find("DT")?.Name == "Dark Templar", "un alias retrouve son unité");
        check(corpus.Find("mutas")?.Race == "zerg", "y compris au pluriel");
        check(corpus.Find("Overseer")!.Detector, "l'Overseer est bien un détecteur");
        check(corpus.Find("Dark Templar")!.Cloaked, "le Dark Templar est bien invisible");
        check(!corpus.Find("Hydralisk")!.Detector, "l'Hydralisk ne détecte rien");

        var sansSignal = corpus.Punishments.Where(p => SignalSpecs.Find(p.Signal) is null).Select(p => p.Id).ToList();
        check(sansSignal.Count == 0, $"chaque punition vise un signal mesuré ({string.Join(", ", sansSignal)})");
        check(corpus.Punishments.All(p => p.Situation.Length > 0 && p.Sanction.Length > 0 && p.Cost.Length > 0),
              "et chacune raconte une situation, une sanction et un coût");

        Console.WriteLine("\nCe que le validateur doit attraper");
        var v = new SceneValidator(corpus);

        var camp = v.Check("harness_breadth", "zerg", "tu avances avec tes Marines et ça tient dix minutes");
        check(camp.HasErrors && camp.Issues.Any(i => i.Rule == "mauvais camp"),
              "une unité terran donnée au joueur zerg est refusée");

        var invisible = v.Check("verification_present", "zerg",
            "quelque chose d'invisible traverse ta base et tu ne peux rien y faire");
        check(invisible.HasErrors && invisible.Issues.Any(i => i.Rule == "invisible sans nom"),
              "une menace invisible que la scène ne nomme pas est refusée");

        var air = v.Check("harness_breadth", "zerg",
            "tu n'as aucun anti-aérien, et tes Zerglings ne servent à rien");
        check(air.HasErrors && air.Issues.Any(i => i.Rule == "anti-aérien sans air"),
              "parler d'anti-aérien sans nommer une unité qui vole est refusé");

        var perime = v.Check("has_acceptance_criteria", "zerg",
            "tu joues 17 hatch, 18 pool, et tu scoutes à 3:00 avec un Drone");
        check(perime.HasErrors && perime.Issues.Any(i => i.Rule == "supply d'avant-patch"),
              "une ouverture citée en supply absolu est refusée depuis le passage à huit ouvriers");

        var inventee = v.Check("harness_breadth", "zerg",
            "le Hydraviper sort de la spire et rase la base adverse");
        check(inventee.Issues.Any(i => i.Rule == "unité inconnue"),
              "une unité inventée est signalée");

        Console.WriteLine("\nCe sur quoi il doit se taire");
        var bonne = v.Check("verification_present", "zerg",
            "tu tiens ta troisième et tout va bien, puis deux Dark Templars entrent dans "
            + "la ligne de drones. Sans Overseer il n'y a rien à cliquer");
        check(!bonne.HasErrors, $"une scène juste passe sans erreur ({string.Join(" | ", bonne.Issues)})");

        var batiment = v.Check("harness_breadth", "zerg",
            "le Fusion Core est sorti pendant que tu regardais ailleurs, et le Vaisseau de guerre arrive");
        check(!batiment.HasErrors && batiment.Issues.All(i => i.Rule != "unité inconnue"),
              "un bâtiment connu n'est pas pris pour une unité inventée");

        var possessifAmi = v.Check("autonomy_ratio", "zerg", "tes Zerglings meurent pendant que tes reines n'injectent pas");
        check(!possessifAmi.HasErrors, "des unités du bon camp ne déclenchent rien");

        var general = v.Check("verification_present", null,
            "une tâche close sans détection ressemble à une partie qu'on croit gagnée");
        check(!general.HasErrors, "parler de détection en général reste permis quand aucune unité n'est nommée");

        Console.WriteLine("\nLe pack livré passe le garde-fou");
        var catalog = LensCatalog.Load(lensDir);
        var rapports = v.CheckLens(catalog.Resolve("starcraft2"));
        var fautes = rapports.Where(r => r.HasErrors).ToList();
        check(fautes.Count == 0,
              fautes.Count == 0
                  ? $"les {rapports.Count} scènes du pack sont sans erreur"
                  : $"{fautes.Count} scène(s) en erreur : {string.Join(" ; ", fautes.Take(3).Select(f => f.Issues[0].Rule))}");

        Console.WriteLine("\nLecture d'une infobox Liquipedia");
        // Un extrait fidèle de ce que renvoie l'API : modèles, liens, gras.
        var wikitext = """
            {{Infobox unit
            |name=Mutalisk
            |race=Zerg
            |minerals=100
            |gas=100
            |supply=2
            |buildtime=24
            |hp=120
            |armor=0 {{Plus}}1
            |attack=9 ([[Glaive Wurm]])
            |speed=''4.0''
            |unused=quelque chose
            }}
            """;
        var chiffres = LiquipediaHarvester.Extract(wikitext);
        check(chiffres["minerai"] == "100", "le coût en minerai est lu");
        check(chiffres["gaz"] == "100", "le coût en gaz aussi");
        check(chiffres["points de vie"] == "120", "les points de vie aussi");
        check(chiffres["attaque"] == "9 (Glaive Wurm)", $"les liens sont réduits à leur texte ({chiffres["attaque"]})");
        check(chiffres["vitesse"] == "4.0", $"les italiques disparaissent ({chiffres["vitesse"]})");
        check(chiffres["armure"] == "0 1", $"les modèles sont retirés ({chiffres["armure"]})");
        check(!chiffres.ContainsKey("unused"), "les champs non demandés sont ignorés");
        check(LiquipediaHarvester.Extract("rien du tout").Count == 0, "une page sans infobox ne rend rien");

        Console.WriteLine("\nLe sas des propositions");
        var dir = Path.Combine(Path.GetTempPath(), "coachingia-corpus-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            var store = new ProposalStore();
            store.Add("verification_present", "zerg", ["une scène", "une autre"]);
            store.Add("verification_present", "zerg", ["une scène"]);
            check(store.Items.Count == 2, "une proposition déjà présente n'est pas ajoutée deux fois");

            store.Save(dir, "essai");
            var relu = ProposalStore.Load(dir, "essai");
            check(relu.Items.Count == 2, "le sas se relit tel qu'il a été écrit");
            check(relu.Items[0].Race == "zerg" && relu.Items[0].Signal == "verification_present",
                  "avec son signal et son camp");

            Console.WriteLine("\nInsertion dans la lentille");
            var lensPath = Path.Combine(dir, "essai.json");
            File.WriteAllText(lensPath, """
                {"id":"essai","name":"Essai","signals":{"loop_closure":"la scène d'origine"}}
                """);

            var added = LensEditor.Append(lensPath, [
                new Proposal("loop_closure", null, "une scène ajoutée", DateTimeOffset.UtcNow),
                new Proposal("verification_present", "zerg", "une scène zerg", DateTimeOffset.UtcNow),
            ]);
            check(added == 2, $"les deux scènes sont insérées (obtenu {added})");

            var lens = JsonSerializer.Deserialize<Lens>(File.ReadAllText(lensPath), Lens.Json)!;
            check(lens.Signals["loop_closure"].Length == 2,
                  "une clé qui était une chaîne devient un tableau sans perdre l'original");
            check(lens.Signals["loop_closure"][0] == "la scène d'origine", "et l'original reste en tête");
            check(lens.Race("zerg")?.Signals["verification_present"].Length == 1,
                  "un camp qui n'existait pas est créé");
            check(lens.Name == "Essai", "le reste du fichier est intact");

            var encore = LensEditor.Append(lensPath, [
                new Proposal("loop_closure", null, "une scène ajoutée", DateTimeOffset.UtcNow),
            ]);
            check(encore == 0, "réinsérer la même scène ne la duplique pas");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        Console.WriteLine("\nLe rédacteur sans juge");
        var writer = new SceneWriter(TimeSpan.FromSeconds(5), "binaire-qui-nexiste-pas");
        var scenes = writer.Write(corpus, "verification_present", "zerg", [], 3, out var erreur);
        check(scenes.Count == 0 && erreur is not null,
              $"l'absence de « claude » est expliquée, pas subie : {erreur}");

        var sansMatiere = writer.Write(corpus, "first_try_success", "zerg", [], 3, out var erreur2);
        check(sansMatiere.Count == 0 && erreur2 is not null && erreur2.Contains("punition"),
              "un signal sans punition ne déclenche aucun appel : il n'y a rien à mettre en scène");
    }
}
