using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoachingIA.Harness.Core.Phoenix;

namespace CoachingIA.Harness.Core.Evaluation;

/// <summary>
/// Publie une campagne d'évaluation dans le projet Phoenix « coachingia-evals »
/// (distinct de « coachingia », le projet des sessions), sur demande explicite
/// du drapeau <c>--phoenix</c> de <c>coachingia evaluer</c> — spec §5 et §5.0.
///
/// <para>Reçoit un <see cref="IPhoenixClient"/> et un <see cref="IPhoenixExperiences"/>
/// déjà construits : cette classe ne parle jamais HTTP directement et
/// n'appelle jamais <see cref="IPhoenixClient.AnnotateAsync"/> — un run
/// d'experiment n'est pas un span, les verdicts partent par
/// <see cref="IPhoenixExperiences.EvaluerRunAsync"/> (§5.0).</para>
///
/// <para>Les méthodes de <c>IPhoenixExperiences</c> et les méthodes
/// datasets/experiments de <c>IPhoenixClient</c> lèvent sur échec HTTP : c'est
/// à cette classe de rattraper. Une panne de Phoenix ne doit jamais faire
/// échouer <c>coachingia evaluer</c> — la campagne mesure l'outil, et son
/// verdict sort avec ou sans Phoenix. Un seul avertissement sur la sortie
/// d'erreur suffit : la publication est un tout, elle n'a pas de succès
/// partiel dont il faille détailler l'étendue.</para>
/// </summary>
public static class PublicationPhoenix
{
    /// <summary>Le projet Phoenix visé par la publication de la campagne d'évaluation.</summary>
    public const string Projet = "coachingia-evals";

    /// <summary>
    /// Publie dataset, experiment, runs et évaluations de runs. Toutes les
    /// exceptions sont rattrapées ici : cette méthode ne lève jamais, et
    /// n'affecte jamais le code de retour de la commande qui l'appelle.
    /// </summary>
    /// <param name="productions">
    /// La <see cref="Production"/> de chaque épreuve jouée, indexée par
    /// <c>Epreuve.Id</c>. <c>ResultatCampagne</c> ne conserve pas les
    /// productions (seulement les verdicts) : c'est à l'appelant de les
    /// fournir — <c>Campagne.Jouer</c> n'est pas modifiable pour les exposer.
    /// </param>
    public static void Publier(
        IPhoenixClient client,
        IPhoenixExperiences experiences,
        JeuEpreuves jeu,
        IReadOnlyDictionary<string, Production> productions,
        ResultatCampagne resultat,
        IReadOnlyList<IEvaluateur> evaluateurs,
        CancellationToken ct = default)
    {
        try
        {
            PublierAsync(client, experiences, jeu, productions, resultat, evaluateurs, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine(
                $"⚠ publication Phoenix échouée ({ex.GetType().Name} : {ex.Message}) "
                + "— le verdict de la campagne n'en dépend pas.");
        }
    }

    private static async Task PublierAsync(
        IPhoenixClient client, IPhoenixExperiences experiences, JeuEpreuves jeu,
        IReadOnlyDictionary<string, Production> productions, ResultatCampagne resultat,
        IReadOnlyList<IEvaluateur> evaluateurs, CancellationToken ct)
    {
        // Ce que chaque épreuve publie, et l'empreinte de cette forme publiée —
        // calculées une fois, réutilisées pour décider quoi envoyer et pour
        // rattacher les runs. Épreuve.cs ne porte aucune empreinte par épreuve :
        // elle se calcule ici, sur la forme EFFECTIVEMENT publiée (donc réduite
        // pour une épreuve privée), jamais sur l'Epreuve brute.
        var construits = jeu.Epreuves.ToDictionary(e => e.Id, ConstruireExemple, StringComparer.Ordinal);

        // 1. Si TrouverDatasetAsync lève, rien d'autre ne s'exécute : pas
        //    d'upload, pas d'experiment. C'est le catch de Publier() qui
        //    rattrape et avertit. Pas de repli sur un upload complet — ça
        //    dupliquerait tout un dataset qui existe peut-être déjà.
        var idExistant = await experiences.TrouverDatasetAsync(Projet, ct).ConfigureAwait(false);

        string datasetId;
        IReadOnlyList<ExemplePhoenix> exemplesActuels;

        if (idExistant is null)
        {
            // 2. Premier passage : le dataset n'existe pas, on ne peut pas lister
            //    ses exemples — upload complet de toutes les épreuves.
            datasetId = await client.UpsertDatasetAsync(
                Projet, [.. jeu.Epreuves.Select(e => construits[e.Id].Exemple)], ct).ConfigureAwait(false);
            exemplesActuels = await experiences.ListerExemplesAsync(datasetId, ct).ConfigureAwait(false);
        }
        else
        {
            datasetId = idExistant;
            var existants = await experiences.ListerExemplesAsync(datasetId, ct).ConfigureAwait(false);

            var presents = new HashSet<(string EpreuveId, string Empreinte)>(
                existants
                    .Where(e => e.Metadata.ContainsKey("epreuve_id") && e.Metadata.ContainsKey("empreinte"))
                    .Select(e => (e.Metadata["epreuve_id"], e.Metadata["empreinte"])));

            var manquantes = jeu.Epreuves
                .Where(e => !presents.Contains((e.Id, construits[e.Id].Empreinte)))
                .ToList();

            // 3. Seules les épreuves absentes ou modifiées partent. Si aucune ne
            //    manque, UpsertDatasetAsync n'est pas appelé du tout.
            if (manquantes.Count > 0)
            {
                await client.UpsertDatasetAsync(
                    Projet, [.. manquantes.Select(e => construits[e.Id].Exemple)], ct).ConfigureAwait(false);
                // 4. Reliste après upload : c'est la seule façon de connaître les
                //    identifiants Phoenix des nouveaux exemples (l'upload ne les
                //    rend pas).
                exemplesActuels = await experiences.ListerExemplesAsync(datasetId, ct).ConfigureAwait(false);
            }
            else
            {
                exemplesActuels = existants;   // rien de nouveau : déjà à jour
            }
        }

        // 5, 6, 7. Rattachement par (epreuve_id, empreinte) — jamais par rang.
        // Un doublon hérité d'un passage antérieur se résout au plus récent
        // (MisAJour), et se signale toujours ; une épreuve sans exemple
        // correspondant se signale et n'a ni run ni évaluation.
        var avertissements = new List<string>();
        var rattachements = Rattacher(jeu, construits, exemplesActuels, avertissements);

        // 8. Une experiment nouvelle à chaque passage, un run par épreuve
        //    rattachée et jouée, une évaluation de run par verdict.
        var experimentId = await client.CreateExperimentAsync(datasetId, NomExperiment(jeu), ct).ConfigureAwait(false);

        var deterministes = evaluateurs.Where(e => e.Deterministe).Select(e => e.Nom).ToHashSet(StringComparer.Ordinal);
        var runIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var maintenant = DateTimeOffset.UtcNow;

        foreach (var epreuve in jeu.Epreuves)
        {
            if (!rattachements.TryGetValue(epreuve.Id, out var exemple)) continue;
            if (!productions.TryGetValue(epreuve.Id, out var production)) continue;

            var run = new ExperimentRun(exemple.Id, production.Texte, maintenant, maintenant, production.Panne);
            runIds[epreuve.Id] = await client.CreateRunAsync(experimentId, run, ct).ConfigureAwait(false);
        }

        foreach (var ligne in resultat.Verdicts)
        {
            if (!runIds.TryGetValue(ligne.Epreuve, out var runId)) continue;

            var verdict = ligne.Verdict;
            var metadata = verdict.Preuve is null
                ? null
                : new Dictionary<string, string> { ["preuve"] = verdict.Preuve };
            var evalResult = new AnnotationResult(verdict.Etiquette, verdict.Indecis ? null : verdict.Score, verdict.Explication);
            var annotatorKind = deterministes.Contains(verdict.Evaluateur) ? AnnotatorKinds.Code : AnnotatorKinds.Llm;

            await experiences.EvaluerRunAsync(
                new RunEvaluation(runId, verdict.Evaluateur, annotatorKind, evalResult, maintenant, maintenant, metadata),
                ct).ConfigureAwait(false);
        }

        foreach (var a in avertissements) Console.Error.WriteLine("⚠ " + a);
    }

    /// <summary>
    /// Choisit, pour chaque épreuve, l'exemple Phoenix dont les métadonnées
    /// portent son <c>epreuve_id</c> et son empreinte courante. L'ordre de
    /// <paramref name="exemplesActuels"/> n'a aucune importance.
    /// </summary>
    private static Dictionary<string, ExemplePhoenix> Rattacher(
        JeuEpreuves jeu,
        IReadOnlyDictionary<string, (DatasetExample Exemple, string Empreinte)> construits,
        IReadOnlyList<ExemplePhoenix> exemplesActuels,
        List<string> avertissements)
    {
        var parCouple = new Dictionary<(string EpreuveId, string Empreinte), List<ExemplePhoenix>>();
        foreach (var ex in exemplesActuels)
        {
            if (!ex.Metadata.TryGetValue("epreuve_id", out var eid) || !ex.Metadata.TryGetValue("empreinte", out var emp))
                continue;
            var cle = (eid, emp);
            if (!parCouple.TryGetValue(cle, out var liste)) parCouple[cle] = liste = [];
            liste.Add(ex);
        }

        var rattachements = new Dictionary<string, ExemplePhoenix>(StringComparer.Ordinal);
        foreach (var epreuve in jeu.Epreuves)
        {
            var cle = (epreuve.Id, construits[epreuve.Id].Empreinte);
            if (!parCouple.TryGetValue(cle, out var candidats) || candidats.Count == 0)
            {
                avertissements.Add(
                    $"aucun exemple Phoenix ne correspond à l'épreuve « {epreuve.Id} » : son run n'est pas publié");
                continue;
            }

            if (candidats.Count == 1)
            {
                rattachements[epreuve.Id] = candidats[0];
                continue;
            }

            // Doublon hérité d'un passage antérieur : le plus récent (MisAJour)
            // l'emporte ; sans date, le dernier dans l'ordre de la réponse.
            var avecDate = candidats.Where(c => c.MisAJour is not null).ToList();
            var choisi = avecDate.Count > 0 ? avecDate.OrderByDescending(c => c.MisAJour).First() : candidats[^1];
            rattachements[epreuve.Id] = choisi;

            avertissements.Add(
                $"{candidats.Count} exemples Phoenix portent le même couple (epreuve_id, empreinte) pour « {epreuve.Id} » : "
                + (avecDate.Count > 0 ? "le plus récent (MisAJour) est retenu" : "faute de date, le dernier de la réponse est retenu")
                + " pour le rattachement.");
        }

        return rattachements;
    }

    /// <summary>
    /// Construit la DatasetExample publiée pour une épreuve, et l'empreinte de
    /// cette forme publiée (jamais de l'Epreuve brute — une épreuve réduite
    /// pour vie privée n'expose donc même pas l'empreinte de son contenu caché,
    /// spec §5.0/§5.1). Vie privée non négociable : Partageable = false ou
    /// CiteDuReel = true réduit l'épreuve à son identifiant et son intitulé.
    /// </summary>
    private static (DatasetExample Exemple, string Empreinte) ConstruireExemple(Epreuve epreuve)
    {
        var metadataBase = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["famille"] = epreuve.Famille,
            ["intitule"] = epreuve.Intitule,
            ["origine"] = epreuve.Origine,
            ["etiquettes"] = string.Join(",", epreuve.Etiquettes),
            ["epreuve_id"] = epreuve.Id,
        };

        IReadOnlyDictionary<string, string> input, output;
        if (!epreuve.Partageable || epreuve.CiteDuReel)
        {
            input = new Dictionary<string, string> { ["id"] = epreuve.Id };
            output = new Dictionary<string, string> { ["intitule"] = epreuve.Intitule };
        }
        else
        {
            input = new Dictionary<string, string> { ["entree"] = epreuve.Entree.GetRawText() };
            output = new Dictionary<string, string> { ["attendu"] = epreuve.Attendu.GetRawText() };
        }

        var empreinte = CalculerEmpreinte(input, output, metadataBase);
        var metadata = new Dictionary<string, string>(metadataBase, StringComparer.Ordinal) { ["empreinte"] = empreinte };
        return (new DatasetExample(input, output, metadata), empreinte);
    }

    /// <summary>
    /// SHA-256, en hexadécimal minuscule, de la sérialisation JSON canonique
    /// (clés triées, ordinal) de ce qui est effectivement publié — hors la clé
    /// <c>empreinte</c> elle-même, qui n'existe pas encore à ce stade.
    /// </summary>
    private static string CalculerEmpreinte(
        IReadOnlyDictionary<string, string> input,
        IReadOnlyDictionary<string, string> output,
        IReadOnlyDictionary<string, string> metadata)
    {
        var canonique = new
        {
            input = Trier(input),
            output = Trier(output),
            metadata = Trier(metadata),
        };
        var json = JsonSerializer.Serialize(canonique);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static SortedDictionary<string, string> Trier(IReadOnlyDictionary<string, string> d)
        => new(new Dictionary<string, string>(d, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Une experiment nouvelle à chaque passage, nommée pour rester traçable.</summary>
    private static string NomExperiment(JeuEpreuves jeu)
        => $"{jeu.Empreinte}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}";
}
