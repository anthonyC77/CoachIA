using CoachingIA.Harness.Core.Coaching;

/// <summary>
/// La commande <c>corpus</c> : tenir le fond de connaissance, et s'en servir.
///
/// Quatre gestes, dans l'ordre où ils se rendent service :
/// <c>--valider</c> relit les scènes existantes contre les faits,
/// <c>--recolter</c> complète les chiffres depuis Liquipedia,
/// <c>--generer</c> fait écrire de nouvelles scènes,
/// <c>--relire</c> les accepte une par une.
///
/// Rien de ce qui est généré n'atteint un bilan sans être passé par
/// <c>--relire</c>. C'est la seule garantie qui tienne : un validateur attrape
/// un contresens sur une unité, il n'attrape pas une scène qui sonne faux.
/// </summary>
public static class CorpusCommand
{
    public static int Run(string[] args, string lensDir, string? lensId, string? raceId)
    {
        var id = lensId ?? "starcraft2";
        var corpus = Corpus.Load(lensDir, id);
        if (corpus is null)
        {
            Console.Error.WriteLine($"  Aucun corpus pour « {id} ».");
            Console.Error.WriteLine($"  Attendu : {Corpus.PathFor(lensDir, id)}");
            return 1;
        }

        var catalog = LensCatalog.Load(lensDir);
        if (!catalog.Knows(id))
        {
            Console.Error.WriteLine($"  La lentille « {id} » est introuvable dans {lensDir}.");
            return 1;
        }
        var lens = catalog.Resolve(id);

        var has = (string flag) => Array.IndexOf(args, flag) >= 0;
        var value = (string flag) =>
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        };

        if (has("--valider")) return Valider(corpus, lens, has("--tout"));
        if (has("--recolter")) return Recolter(corpus, lensDir, id, has("--ecrire"));
        if (has("--generer")) return Generer(corpus, lens, lensDir, id, value("--generer"), raceId,
                                             int.TryParse(value("-n"), out var n) ? n : 3);
        if (has("--relire")) return Relire(lensDir, id);

        return Resume(corpus, lens);
    }

    // ---------------------------------------------------------------- résumé

    private static int Resume(Corpus corpus, Lens lens)
    {
        var scenes = lens.Signals.Sum(kv => kv.Value.Length)
                   + lens.Races.Sum(r => r.Value.Signals.Sum(kv => kv.Value.Length));

        Console.WriteLine($"""

              Corpus {corpus.Game} — patch {corpus.Patch.Version}

                {corpus.Units.Count,4} unités        ({string.Join(", ", corpus.Units.GroupBy(u => u.Race).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"))})
                {corpus.Mechanics.Count,4} mécaniques
                {corpus.Vernacular.Count,4} termes de vernaculaire
                {corpus.Punishments.Count,4} punitions      ({corpus.Punishments.Select(p => p.Signal).Distinct().Count()} signaux couverts)

                {scenes,4} scènes dans la lentille, à vérifier contre tout ça

              {corpus.Patch.Summary}

              corpus --valider              relire les scènes contre les faits
              corpus --recolter             compléter les chiffres depuis Liquipedia
              corpus --generer <signal>     faire écrire de nouvelles scènes
              corpus --relire               accepter ou jeter les propositions
            """);

        var sansPunition = SignalSpecs.All
            .Where(s => !corpus.Punishments.Any(p => p.Signal == s.Key))
            .Select(s => s.Key).ToList();
        if (sansPunition.Count > 0)
            Console.WriteLine($"\n  Signaux sans punition, donc sans matière à générer : {string.Join(", ", sansPunition)}");
        return 0;
    }

    // -------------------------------------------------------------- valider

    private static int Valider(Corpus corpus, Lens lens, bool tout)
    {
        var validator = new SceneValidator(corpus);
        var reports = validator.CheckLens(lens);

        var erreurs = reports.Where(r => r.HasErrors).ToList();
        var doutes = reports.Where(r => !r.HasErrors && !r.IsClean).ToList();

        Console.WriteLine($"\n  {reports.Count} scènes relues contre {corpus.Units.Count} unités "
            + $"et {corpus.Mechanics.Count} mécaniques, patch {corpus.Patch.Version}.\n");

        foreach (var r in erreurs) Ecrire(r, "ERREUR");
        if (tout) foreach (var r in doutes) Ecrire(r, "doute ");

        // Les doutes se lisent en proportion, pas en liste : trente lignes
        // identiques ne disent rien de plus qu'un pourcentage, et elles noient
        // les deux qui comptent.
        foreach (var g in doutes.SelectMany(d => d.Issues).GroupBy(i => i.Rule).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Count(),4} · {g.Key}"
                + (g.Key == "sans ancrage" ? $"   ({g.Count() * 100.0 / reports.Count:F0} % du pack)" : ""));

        Console.WriteLine(erreurs.Count == 0
            ? $"\n  Aucune erreur. {doutes.Count} scène(s) à discuter{(tout ? "" : " — « --tout » pour les lire")}."
            : $"\n  {erreurs.Count} erreur(s), {doutes.Count} scène(s) à discuter.");

        // Un doute ne bloque pas : c'est une question de goût, et un outil qui
        // crie pour des questions de goût finit ignoré.
        return erreurs.Count == 0 ? 0 : 1;
    }

    private static void Ecrire(SceneReport r, string etiquette)
    {
        var ou = r.Race is null ? r.Key : $"{r.Key} · {r.Race}";
        Console.WriteLine($"  {etiquette}  {ou}");
        foreach (var i in r.Issues) Console.WriteLine($"           {i}");
        var extrait = r.Text.Length > 150 ? r.Text[..150] + "…" : r.Text;
        Console.WriteLine($"           « {extrait} »\n");
    }

    // ------------------------------------------------------------- récolter

    private static int Recolter(Corpus corpus, string lensDir, string id, bool ecrire)
    {
        var harvester = new LiquipediaHarvester();
        Console.WriteLine($"\n  Récolte sur {LiquipediaHarvester.Host} — {corpus.Units.Count} unités, "
            + $"une requête toutes les {LiquipediaHarvester.DelaySeconds} s.");
        Console.WriteLine("  Contenu sous licence CC BY-SA 3.0, attribué à Liquipedia dans le corpus.\n");

        var result = harvester.Harvest(corpus, line => Console.WriteLine("    " + line));

        Console.WriteLine($"\n  {result.Filled} unité(s) complétée(s), {result.Missed} sans page trouvée, "
            + $"{result.Failed} en échec réseau.");

        if (!ecrire)
        {
            Console.WriteLine("  Rien n'a été écrit. Relancez avec --ecrire pour enregistrer.");
            return 0;
        }

        var path = Corpus.PathFor(lensDir, id);
        var page = ReviewArchive.Write(Path.GetDirectoryName(path)!, Path.GetFileName(path),
            System.Text.Json.JsonSerializer.Serialize(corpus, Corpus.Json));
        Console.WriteLine($"  Corpus enregistré : {page.Path}"
            + (page.ArchivedTo is null ? "" : $" (version précédente dans {Path.GetFileName(page.ArchivedTo)})"));
        return 0;
    }

    // -------------------------------------------------------------- générer

    private static int Generer(Corpus corpus, Lens lens, string lensDir, string id,
                               string? signal, string? race, int count)
    {
        if (signal is null || SignalSpecs.Find(signal) is null)
        {
            Console.Error.WriteLine("  Précisez un signal connu : corpus --generer verification_present");
            Console.Error.WriteLine($"  Signaux : {string.Join(", ", SignalSpecs.All.Select(s => s.Key))}");
            return 1;
        }

        var writer = new SceneWriter();
        var existing = (race is not null && lens.Race(race) is { } r && r.Signals.TryGetValue(signal, out var mine)
                            ? mine
                            : lens.Signals.GetValueOrDefault(signal) ?? []).ToList();

        Console.WriteLine($"\n  {count} scène(s) demandée(s) pour {signal}"
            + (race is null ? "" : $" · camp {race}") + ".");
        Console.WriteLine($"  {existing.Count} scène(s) déjà écrites, données au rédacteur pour qu'il ne les répète pas.");

        var proposals = writer.Write(corpus, signal, race, existing, count, out var error);
        if (error is not null)
        {
            Console.Error.WriteLine($"\n  Le rédacteur n'a pas répondu : {error}");
            Console.Error.WriteLine("  Rien n'a été écrit. La lentille est intacte.");
            return 1;
        }

        var validator = new SceneValidator(corpus);
        var retenues = new List<string>();
        foreach (var scene in proposals)
        {
            var report = validator.Check(signal, race, scene);
            if (report.HasErrors)
            {
                Console.WriteLine($"\n  Rejetée par le validateur :");
                foreach (var i in report.Issues.Where(i => i.Level == SceneIssueLevel.Erreur))
                    Console.WriteLine($"    {i}");
                continue;
            }
            retenues.Add(scene);
        }

        if (retenues.Count == 0)
        {
            Console.WriteLine("\n  Aucune proposition n'a passé le validateur.");
            return 1;
        }

        var store = ProposalStore.Load(lensDir, id);
        store.Add(signal, race, retenues);
        store.Save(lensDir, id);

        Console.WriteLine($"\n  {retenues.Count} proposition(s) retenue(s) sur {proposals.Count}, "
            + $"en attente de relecture dans {ProposalStore.PathFor(lensDir, id)}.");
        Console.WriteLine("  Elles n'apparaîtront dans aucun bilan avant : corpus --relire");
        return 0;
    }

    // --------------------------------------------------------------- relire

    private static int Relire(string lensDir, string id)
    {
        var store = ProposalStore.Load(lensDir, id);
        if (store.Items.Count == 0)
        {
            Console.WriteLine("\n  Aucune proposition en attente.");
            return 0;
        }

        Console.WriteLine($"\n  {store.Items.Count} proposition(s). g = garder, j = jeter, q = quitter.\n");

        var gardees = new List<Proposal>();
        var restantes = new List<Proposal>();
        var arrete = false;

        foreach (var item in store.Items)
        {
            if (arrete) { restantes.Add(item); continue; }

            Console.WriteLine($"  {item.Signal}{(item.Race is null ? "" : " · " + item.Race)}");
            foreach (var line in Wrap(item.Text, 74)) Console.WriteLine("    " + line);
            Console.Write("\n  [g/j/q] ");

            var reponse = Console.ReadLine()?.Trim().ToLowerInvariant();
            Console.WriteLine();

            switch (reponse)
            {
                case "g": gardees.Add(item); break;
                case "q": arrete = true; restantes.Add(item); break;
                default: break;   // jetée : elle n'est ni gardée ni conservée
            }
        }

        if (gardees.Count > 0)
        {
            var path = Path.Combine(lensDir, id + ".json");
            var added = LensEditor.Append(path, gardees);
            Console.WriteLine($"  {added} scène(s) ajoutée(s) à {path}.");
        }

        store.Items.Clear();
        store.Items.AddRange(restantes);
        store.Save(lensDir, id);

        Console.WriteLine($"  {restantes.Count} proposition(s) encore en attente.");
        return 0;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > width && line.Length > 0)
            { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
