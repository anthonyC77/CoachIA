using System.Text.Json;
using CoachingIA.Harness.Core.Evaluation;
using CoachingIA.Harness.Core.Phoenix;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai de <c>PublicationPhoenix</c> (spec §5, §5.0, §5.1) : dataset,
/// experiment, runs et évaluations de runs produits pour une campagne
/// fabriquée, rattachement par (epreuve_id, empreinte) plutôt que par rang,
/// absence de ré-upload d'un jeu inchangé, doublons et épreuves orphelines,
/// vie privée des épreuves qui citent du réel, et respect du drapeau
/// <c>--phoenix</c> de <c>coachingia evaluer</c>.
///
/// Tout passe par un <c>FakePhoenix</c> qui implémente à la fois
/// <c>IPhoenixClient</c> et <c>IPhoenixExperiences</c> et tient l'état d'un
/// dataset Phoenix minimal en mémoire — <c>UpsertDatasetAsync</c> y ajoute des
/// exemples, <c>ListerExemplesAsync</c> les relit — pour que les scénarios de
/// rattachement (doublons, ordre différent, contenu modifié) s'écrivent sans
/// deviner à l'avance la valeur d'une empreinte calculée en privé par
/// <c>PublicationPhoenix</c>.
/// </summary>
public static class PhoenixEvalTests
{
    public static void Run(Action<bool, string> check, string racine)
    {
        Console.WriteLine("Premier passage : dataset inexistant");
        {
            var fake = new FakePhoenix();
            var jeu = NouveauJeu(E("e1", "familletest", "test un"), E("e2", "familletest", "test deux"));
            var productions = Productions(("e1", "sortie e1"), ("e2", "sortie e2"));
            var resultat = new ResultatCampagne { EmpreinteJeu = "j" };
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval_code", "familletest", true) };

            PublicationPhoenix.Publier(fake, fake, jeu, productions, resultat, evaluateurs);

            var idxUpload = fake.Journal.IndexOf("Upsert");
            var listerAvant = idxUpload < 0 ? 0 : fake.Journal.Take(idxUpload).Count(j => j == "ListerExemples");
            check(listerAvant == 0,
                $"quand TrouverDatasetAsync rend null, ListerExemplesAsync n'est pas appelé avant l'upload (obtenu {listerAvant} appel(s) avant)");
            check(fake.UploadsRecus.Count == 1 && fake.UploadsRecus[0].Exemples.Count == 2,
                $"UpsertDatasetAsync reçoit une DatasetExample par épreuve (obtenu {fake.UploadsRecus.FirstOrDefault().Exemples?.Count ?? 0})");
            check(fake.RunsCrees.Count == 2, $"un run par épreuve jouée (obtenu {fake.RunsCrees.Count})");
            check(fake.RunsCrees.Any(r => r.Run.Sortie == "sortie e1") && fake.RunsCrees.Any(r => r.Run.Sortie == "sortie e2"),
                "la sortie de chaque run est Production.Texte de l'épreuve correspondante");
        }

        Console.WriteLine("\nMétadonnées de chaque DatasetExample");
        {
            var fake = new FakePhoenix();
            var jeu = NouveauJeu(E("e1", "familletest", "intitulé un", ["etq-a", "etq-b"]));
            var productions = Productions(("e1", "sortie e1"));
            PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" },
                [new FakeEvaluateur("eval", "familletest", true)]);

            var meta = fake.UploadsRecus[0].Exemples[0].Metadata;
            check(meta["famille"] == "familletest", "les métadonnées portent la famille");
            check(meta["intitule"] == "intitulé un", "l'intitulé");
            check(meta["origine"] == "synthetique", "l'origine");
            check(meta["etiquettes"].Contains("etq-a") && meta["etiquettes"].Contains("etq-b"), "et les étiquettes");
            check(meta["epreuve_id"] == "e1", "epreuve_id égale Epreuve.Id");
            var empreinte = meta["empreinte"];
            check(empreinte.Length == 64 && empreinte.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
                $"empreinte fait 64 caractères hexadécimaux minuscules (obtenu {empreinte.Length} car. : « {empreinte} »)");
        }

        Console.WriteLine("\nStabilité de l'empreinte");
        {
            var e1 = E("e1", "familletest", "test", entree: """{"texte":"contenu original"}""");
            var jeu1 = NouveauJeu(e1);
            var fakeA = new FakePhoenix();
            PublicationPhoenix.Publier(fakeA, fakeA, jeu1, Productions(("e1", "s")), new ResultatCampagne { EmpreinteJeu = "j" },
                [new FakeEvaluateur("eval", "familletest", true)]);
            var empreinteA = fakeA.UploadsRecus[0].Exemples[0].Metadata["empreinte"];

            var fakeB = new FakePhoenix();
            PublicationPhoenix.Publier(fakeB, fakeB, jeu1, Productions(("e1", "s")), new ResultatCampagne { EmpreinteJeu = "j" },
                [new FakeEvaluateur("eval", "familletest", true)]);
            var empreinteB = fakeB.UploadsRecus[0].Exemples[0].Metadata["empreinte"];

            check(empreinteA == empreinteB,
                $"deux publications du même jeu donnent la même empreinte (obtenu {empreinteA} puis {empreinteB})");

            var e1Modifiee = E("e1", "familletest", "test", entree: """{"texte":"contenu modifié"}""");
            var fakeC = new FakePhoenix();
            PublicationPhoenix.Publier(fakeC, fakeC, NouveauJeu(e1Modifiee), Productions(("e1", "s")),
                new ResultatCampagne { EmpreinteJeu = "j" }, [new FakeEvaluateur("eval", "familletest", true)]);
            var empreinteC = fakeC.UploadsRecus[0].Exemples[0].Metadata["empreinte"];

            check(empreinteA != empreinteC,
                $"l'empreinte change quand l'Entree de l'épreuve change (obtenu {empreinteA} puis {empreinteC})");
        }

        Console.WriteLine("\nDeux passages sur le même jeu : le second n'ajoute aucun exemple");
        {
            var fake = new FakePhoenix();
            var jeu = NouveauJeu(E("e1", "familletest", "un"), E("e2", "familletest", "deux"));
            var productions = Productions(("e1", "sortie e1"), ("e2", "sortie e2"));
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };

            PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs);
            var runsApres1 = fake.RunsCrees.Count;

            PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs);

            check(fake.UploadsRecus.Count == 1,
                $"le second passage sur un jeu inchangé n'appelle pas UpsertDatasetAsync (obtenu {fake.UploadsRecus.Count} upload(s) au total)");
            check(fake.ExperimentsCrees.Count == 2,
                $"chaque passage crée une experiment nouvelle (obtenu {fake.ExperimentsCrees.Count})");
            check(fake.RunsCrees.Count == runsApres1 * 2,
                $"le second passage publie de nouveau un run par épreuve (obtenu {fake.RunsCrees.Count} pour {runsApres1} attendus par passage)");
        }

        Console.WriteLine("\nÉpreuve modifiée : nouvelle version envoyée seule, le run va au nouvel exemple");
        {
            var fake = new FakePhoenix();
            var e2 = E("e2", "familletest", "stable", entree: """{"t":"stable"}""");
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };

            var e1v1 = E("e1", "familletest", "v1", entree: """{"t":"v1"}""");
            PublicationPhoenix.Publier(fake, fake, NouveauJeu(e1v1, e2), Productions(("e1", "sortie e1 v1"), ("e2", "sortie e2")),
                new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs);

            var e1v2 = E("e1", "familletest", "v2", entree: """{"t":"v2 - contenu tres different"}""");
            PublicationPhoenix.Publier(fake, fake, NouveauJeu(e1v2, e2), Productions(("e1", "sortie e1 v2"), ("e2", "sortie e2")),
                new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs);

            check(fake.UploadsRecus.Count == 2 && fake.UploadsRecus[1].Exemples.Count == 1,
                $"seule l'épreuve modifiée est envoyée au second passage (obtenu {fake.UploadsRecus.ElementAtOrDefault(1).Exemples?.Count ?? -1} exemple(s))");
            check(fake.UploadsRecus[1].Exemples[0].Metadata["epreuve_id"] == "e1", "et c'est bien e1");

            var exemplesE1 = fake.Exemples.Where(x => x.Metadata["epreuve_id"] == "e1").ToList();
            check(exemplesE1.Count == 2, $"deux exemples Phoenix coexistent pour e1 (obtenu {exemplesE1.Count})");
            var nouvelExemple = exemplesE1[^1];
            var runE1v2 = fake.RunsCrees.Last(r => r.Run.Sortie == "sortie e1 v2");
            check(runE1v2.Run.ExampleId == nouvelExemple.Id,
                $"le run de la version modifiée va au nouvel exemple, pas à l'ancien (obtenu {runE1v2.Run.ExampleId}, attendu {nouvelExemple.Id})");
        }

        Console.WriteLine("\nOrdre des exemples renvoyés par Phoenix sans importance");
        {
            var setup = new FakePhoenix();
            var jeu = NouveauJeu(E("e1", "familletest", "un"), E("e2", "familletest", "deux"));
            var productions = Productions(("e1", "sortie e1"), ("e2", "sortie e2"));
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };
            PublicationPhoenix.Publier(setup, setup, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs);

            var fake = new FakePhoenix { DatasetId = "dataset-inverse" };
            foreach (var ex in setup.Exemples.AsEnumerable().Reverse()) fake.Seeder(ex.Id, ex.Metadata, ex.MisAJour);

            PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs);

            check(fake.UploadsRecus.Count == 0, "rien à envoyer : le dataset porte déjà tout, même dans un ordre différent");
            var idExempleE1 = setup.Exemples.First(x => x.Metadata["epreuve_id"] == "e1").Id;
            var idExempleE2 = setup.Exemples.First(x => x.Metadata["epreuve_id"] == "e2").Id;
            check(idExempleE1 != "e1" && idExempleE2 != "e2", "les identifiants Phoenix factices sont distincts des Epreuve.Id");
            var runE1 = fake.RunsCrees.Last(r => r.Run.Sortie == "sortie e1");
            var runE2 = fake.RunsCrees.Last(r => r.Run.Sortie == "sortie e2");
            check(runE1.Run.ExampleId == idExempleE1 && runE2.Run.ExampleId == idExempleE2,
                "chaque run porte l'ExampleId de l'exemple dont les métadonnées portent son epreuve_id et son empreinte, indépendamment de l'ordre de la réponse");
        }

        Console.WriteLine("\nÉpreuve sans exemple correspondant après upload");
        {
            var fake = new FakePhoenix();
            fake.IgnorerEpreuveId.Add("e2");
            var jeu = NouveauJeu(E("e1", "familletest", "un"), E("e2", "familletest", "deux"));
            var productions = Productions(("e1", "sortie e1"), ("e2", "sortie e2"));
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };

            var erreurs = CapturerErreur(() =>
                PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs));

            check(erreurs.Contains("e2", StringComparison.Ordinal),
                $"un avertissement nomme l'épreuve sans exemple correspondant (obtenu « {erreurs.Trim()} »)");
            check(!fake.RunsCrees.Any(r => r.Run.Sortie == "sortie e2"), "l'épreuve sans exemple correspondant n'a pas de run");
            check(fake.RunsCrees.Any(r => r.Run.Sortie == "sortie e1"), "les autres épreuves ont bien leur run");
        }

        Console.WriteLine("\nDoublon d'exemples : le plus récent est retenu, un avertissement le signale");
        {
            var setup = new FakePhoenix();
            var jeu = NouveauJeu(E("e1", "familletest", "un"));
            var productions = Productions(("e1", "sortie e1"));
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };
            PublicationPhoenix.Publier(setup, setup, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs);
            var metaE1 = setup.Exemples.First(x => x.Metadata["epreuve_id"] == "e1").Metadata;

            var fake = new FakePhoenix { DatasetId = "dataset-doublon" };
            fake.Seeder("ancien-1", metaE1, DateTimeOffset.UtcNow.AddHours(-2));
            fake.Seeder("recent-1", metaE1, DateTimeOffset.UtcNow);

            var erreurs = CapturerErreur(() =>
                PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs));

            check(erreurs.Contains("e1", StringComparison.Ordinal), $"l'avertissement de doublon nomme l'épreuve (obtenu « {erreurs.Trim()} »)");
            var run = fake.RunsCrees.Last(r => r.Run.Sortie == "sortie e1");
            check(run.Run.ExampleId == "recent-1",
                $"le run se rattache au doublon le plus récent selon MisAJour (obtenu {run.Run.ExampleId})");
        }

        Console.WriteLine("\nTrouverDatasetAsync qui lève : publication abandonnée proprement");
        {
            var fake = new FakePhoenix { TrouverDatasetLeve = true };
            var jeu = NouveauJeu(E("e1", "familletest", "un"));
            var productions = Productions(("e1", "sortie e1"));
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };

            var leve = false;
            var erreurs = "";
            try
            {
                erreurs = CapturerErreur(() =>
                    PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs));
            }
            catch { leve = true; }

            check(!leve, "aucune exception ne sort de la publication quand TrouverDatasetAsync lève");
            check(fake.UploadsRecus.Count == 0, "UpsertDatasetAsync n'est pas appelé");
            check(fake.ExperimentsCrees.Count == 0, "aucune experiment n'est créée");
            check(erreurs.Trim().Length > 0, "un avertissement est émis");
        }

        Console.WriteLine("\nVie privée : épreuve non partageable ou citant du réel");
        {
            var fake = new FakePhoenix();
            var ePrivee = E("priv1", "familletest", "intitulé privé", partageable: false,
                entree: """{"secret":"CONTENU-SECRET-PRIVE-12345"}""", attendu: """{"secret":"ATTENDU-SECRET-PRIVE-67890"}""");
            var eTranscript = E("trans1", "familletest", "intitulé transcript", origine: "transcript",
                entree: """{"texte":"CONTENU-REEL-DU-TRANSCRIPT-ABCDE"}""", attendu: """{"texte":"ATTENDU-REEL-TRANSCRIPT-FGHIJ"}""");
            var jeu = NouveauJeu(ePrivee, eTranscript);
            var productions = Productions(("priv1", "sortie priv"), ("trans1", "sortie trans"));
            var resultat = new ResultatCampagne { EmpreinteJeu = "j" };
            resultat.Verdicts.Add(new LigneVerdict("priv1", "familletest",
                new Verdict("eval", "conformite", "conforme", 1.0, "explication", "preuve sans secret")));
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };

            PublicationPhoenix.Publier(fake, fake, jeu, productions, resultat, evaluateurs);

            var tout = JsonSerializer.Serialize(fake.UploadsRecus)
                + JsonSerializer.Serialize(fake.RunsCrees)
                + JsonSerializer.Serialize(fake.EvaluationsRecues);

            check(!tout.Contains("CONTENU-SECRET-PRIVE-12345", StringComparison.Ordinal),
                "le texte de l'Entree d'une épreuve non partageable n'apparaît dans aucun champ publié");
            check(!tout.Contains("ATTENDU-SECRET-PRIVE-67890", StringComparison.Ordinal), "ni celui de son Attendu");
            check(!tout.Contains("CONTENU-REEL-DU-TRANSCRIPT-ABCDE", StringComparison.Ordinal),
                "ni le texte de l'Entree d'une épreuve dont Origine vaut « transcript »");
            check(!tout.Contains("ATTENDU-REEL-TRANSCRIPT-FGHIJ", StringComparison.Ordinal), "ni celui de son Attendu");
        }

        Console.WriteLine("\nÉvaluations de run : un Verdict par évaluation, AnnotatorKind selon Deterministe");
        {
            var fake = new FakePhoenix();
            var jeu = NouveauJeu(E("e1", "familletest", "un"));
            var productions = Productions(("e1", "sortie e1"));
            var resultat = new ResultatCampagne { EmpreinteJeu = "j" };
            resultat.Verdicts.Add(new LigneVerdict("e1", "familletest",
                new Verdict("eval_code", "conformite", "conforme", 1.0, "explication code", "preuve code")));
            resultat.Verdicts.Add(new LigneVerdict("e1", "familletest",
                new Verdict("eval_llm", "accord", "accord", 1.0, "explication llm", "preuve llm")));
            resultat.Verdicts.Add(new LigneVerdict("e1", "familletest",
                new Verdict("eval_indecis", "conformite", "indeterminable", double.NaN, "indécis")));
            var evaluateurs = new List<IEvaluateur>
            {
                new FakeEvaluateur("eval_code", "familletest", true),
                new FakeEvaluateur("eval_llm", "familletest", false),
                new FakeEvaluateur("eval_indecis", "familletest", true),
            };

            PublicationPhoenix.Publier(fake, fake, jeu, productions, resultat, evaluateurs);

            check(fake.EvaluationsRecues.Count == 3, $"une RunEvaluation par verdict (obtenu {fake.EvaluationsRecues.Count})");
            var runId = fake.RunsCrees.Single().RunId;
            check(fake.EvaluationsRecues.All(e => e.ExperimentRunId == runId),
                "chaque RunEvaluation porte l'identifiant du run renvoyé par CreateRunAsync");

            var evalCode = fake.EvaluationsRecues.Single(e => e.Name == "eval_code");
            check(evalCode.Result.Label == "conforme", "Name = Evaluateur et label = Etiquette");
            check(evalCode.Result.Explication == "explication code", "explication = Explication");
            check(evalCode.Metadata is not null && evalCode.Metadata.TryGetValue("preuve", out var p) && p == "preuve code",
                "Preuve dans les métadonnées");
            check(evalCode.AnnotatorKind == AnnotatorKinds.Code, "un évaluateur déterministe porte AnnotatorKind = CODE");

            var evalLlm = fake.EvaluationsRecues.Single(e => e.Name == "eval_llm");
            check(evalLlm.AnnotatorKind == AnnotatorKinds.Llm, "un évaluateur non déterministe porte AnnotatorKind = LLM");

            var evalIndecis = fake.EvaluationsRecues.Single(e => e.Name == "eval_indecis");
            check(evalIndecis.Result.Score is null, "un verdict indécis (Score NaN) donne un Score null");

            check(fake.AnnotationsRecues.Count == 0, "la publication n'appelle jamais AnnotateAsync");
        }

        Console.WriteLine("\nUn client factice qui échoue à CHAQUE appel : aucune exception, un seul avertissement");
        {
            var fake = new FakePhoenix
            {
                TrouverDatasetLeve = true,
                ListerExemplesLeve = true,
                UpsertLeve = true,
                CreateExperimentLeve = true,
                CreateRunLeve = true,
                EvaluerRunLeve = true,
            };
            var jeu = NouveauJeu(E("e1", "familletest", "un"));
            var productions = Productions(("e1", "sortie e1"));
            var evaluateurs = new List<IEvaluateur> { new FakeEvaluateur("eval", "familletest", true) };

            var leve = false;
            try
            {
                CapturerErreur(() =>
                    PublicationPhoenix.Publier(fake, fake, jeu, productions, new ResultatCampagne { EmpreinteJeu = "j" }, evaluateurs));
            }
            catch { leve = true; }

            check(!leve, "un client factice qui échoue à chaque appel ne fait remonter aucune exception hors de la publication");
        }

        Console.WriteLine("\ncoachingia evaluer : le drapeau --phoenix, joué en direct sur le vrai jeu d'épreuves");
        {
            // La commande est jouée ici telle que le terminal la joue, sur le jeu
            // versionné du dépôt, avec un client factice à la place de Phoenix.
            var lensDir = Path.Combine(racine, "lenses");
            string[] Args(params string[] extra) => ["evaluer", "--racine", racine, .. extra];

            var sansDrapeau = new FakePhoenix();
            var (codeSans, sortieSans) = Jouer(() => EvaluerCommand.Run(Args(), lensDir, sansDrapeau, sansDrapeau));
            check(sansDrapeau.Journal.Count == 0 && sansDrapeau.AnnotationsRecues.Count == 0,
                $"sans --phoenix, le client factice ne reçoit aucun appel (obtenu {sansDrapeau.Journal.Count})");

            var avecDrapeau = new FakePhoenix();
            var (codeAvec, sortieAvec) = Jouer(() => EvaluerCommand.Run(Args("--phoenix"), lensDir, avecDrapeau, avecDrapeau));
            check(avecDrapeau.RunsCrees.Count > 0,
                $"avec --phoenix, la campagne est bien publiée : un run par épreuve jouée (obtenu {avecDrapeau.RunsCrees.Count})");
            check(sortieAvec == sortieSans,
                "avec ou sans --phoenix, la sortie standard est identique caractère pour caractère");
            check(codeAvec == codeSans,
                $"avec ou sans --phoenix, le code de retour est le même (obtenu {codeAvec}, attendu {codeSans})");

            var enPanne = new FakePhoenix
            {
                TrouverDatasetLeve = true, ListerExemplesLeve = true, UpsertLeve = true,
                CreateExperimentLeve = true, CreateRunLeve = true, EvaluerRunLeve = true,
            };
            int? codePanne = null;
            Exception? fuite = null;
            try { codePanne = Jouer(() => EvaluerCommand.Run(Args("--phoenix"), lensDir, enPanne, enPanne)).Code; }
            catch (Exception ex) { fuite = ex; }
            check(fuite is null,
                $"un Phoenix qui échoue à chaque appel ne fait sortir aucune exception de la commande (obtenu {fuite?.GetType().Name ?? "aucune"})");
            check(codePanne == codeSans,
                $"un Phoenix en panne ne change pas le code de retour de la campagne (obtenu {codePanne?.ToString() ?? "aucun"}, attendu {codeSans})");
        }
    }

    // ---- fabriques -----------------------------------------------------

    private static JeuEpreuves NouveauJeu(params Epreuve[] epreuves)
    {
        var jeu = new JeuEpreuves();
        jeu.Epreuves.AddRange(epreuves);
        return jeu;
    }

    private static Epreuve E(
        string id, string famille, string intitule,
        IReadOnlyList<string>? etiquettes = null,
        string origine = "synthetique", bool partageable = true,
        string entree = """{"texte":"entree"}""", string attendu = """{"texte":"attendu"}""")
        => new(id, famille, intitule, Json(entree), Json(attendu), partageable, origine, etiquettes ?? []);

    private static JsonElement Json(string texte) => JsonDocument.Parse(texte).RootElement.Clone();

    private static Dictionary<string, Production> Productions(params (string Id, string Texte)[] paires)
        => paires.ToDictionary(p => p.Id, p => new Production(p.Texte, "source-test"), StringComparer.Ordinal);

    private static string CapturerErreur(Action action)
    {
        var original = Console.Error;
        var tampon = new StringWriter();
        Console.SetError(tampon);
        try { action(); } finally { Console.SetError(original); }
        return tampon.ToString();
    }

    /// <summary>Joue une commande en capturant ses deux sorties ; rend son code et sa sortie standard.</summary>
    private static (int Code, string Sortie) Jouer(Func<int> commande)
    {
        var (sortie, erreur) = (Console.Out, Console.Error);
        var (tamponSortie, tamponErreur) = (new StringWriter(), new StringWriter());
        Console.SetOut(tamponSortie);
        Console.SetError(tamponErreur);
        try { return (commande(), tamponSortie.ToString()); }
        finally { Console.SetOut(sortie); Console.SetError(erreur); }
    }

    // ---- doublures --------------------------------------------------------

    private sealed class FakeEvaluateur(string nom, string famille, bool deterministe) : IEvaluateur
    {
        public string Nom => nom;
        public Bareme Bareme => Baremes.Conformite;
        public string Famille => famille;
        public bool Deterministe => deterministe;
        public Verdict? Evaluer(Epreuve epreuve, Production production) => null;
    }

    /// <summary>
    /// Tient l'état d'un dataset Phoenix minimal en mémoire : UpsertDatasetAsync
    /// y ajoute des exemples (avec un identifiant factice, distinct de
    /// Epreuve.Id), ListerExemplesAsync les relit. Chaque méthode peut être
    /// configurée pour lever, comme le ferait un vrai client contre un Phoenix
    /// en panne.
    /// </summary>
    private sealed class FakePhoenix : IPhoenixClient, IPhoenixExperiences
    {
        private readonly List<(string Id, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset? MisAJour)> _exemples = [];
        private int _prochainExempleId = 1;
        private int _prochainRunId = 1;

        public string? DatasetId { get; set; }
        public bool TrouverDatasetLeve { get; set; }
        public bool ListerExemplesLeve { get; set; }
        public bool UpsertLeve { get; set; }
        public bool CreateExperimentLeve { get; set; }
        public bool CreateRunLeve { get; set; }
        public bool EvaluerRunLeve { get; set; }
        public HashSet<string> IgnorerEpreuveId { get; } = new(StringComparer.Ordinal);

        public List<string> Journal { get; } = [];
        public List<(string Nom, IReadOnlyList<DatasetExample> Exemples)> UploadsRecus { get; } = [];
        public List<(string DatasetId, string Nom)> ExperimentsCrees { get; } = [];
        public List<(string ExperimentId, ExperimentRun Run, string RunId)> RunsCrees { get; } = [];
        public List<RunEvaluation> EvaluationsRecues { get; } = [];
        public List<SpanAnnotation> AnnotationsRecues { get; } = [];

        public IReadOnlyList<(string Id, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset? MisAJour)> Exemples => _exemples;

        public void Seeder(string id, IReadOnlyDictionary<string, string> metadata, DateTimeOffset? misAJour = null)
            => _exemples.Add((id, metadata, misAJour));

        public Task<string?> TrouverDatasetAsync(string nom, CancellationToken ct)
        {
            Journal.Add("TrouverDataset");
            if (TrouverDatasetLeve) throw new InvalidOperationException("panne factice : TrouverDatasetAsync");
            return Task.FromResult(DatasetId);
        }

        public Task<IReadOnlyList<ExemplePhoenix>> ListerExemplesAsync(string datasetId, CancellationToken ct)
        {
            Journal.Add("ListerExemples");
            if (ListerExemplesLeve) throw new InvalidOperationException("panne factice : ListerExemplesAsync");
            return Task.FromResult<IReadOnlyList<ExemplePhoenix>>(
                [.. _exemples.Select(e => new ExemplePhoenix(e.Id, e.Metadata, e.MisAJour))]);
        }

        public Task<string> UpsertDatasetAsync(string nom, IReadOnlyList<DatasetExample> exemples, CancellationToken ct)
        {
            Journal.Add("Upsert");
            if (UpsertLeve) throw new InvalidOperationException("panne factice : UpsertDatasetAsync");
            UploadsRecus.Add((nom, exemples));
            DatasetId ??= "dataset-1";
            foreach (var ex in exemples)
            {
                var epreuveId = ex.Metadata.GetValueOrDefault("epreuve_id");
                if (epreuveId is not null && IgnorerEpreuveId.Contains(epreuveId)) continue;
                _exemples.Add(($"exemple-{_prochainExempleId++}", ex.Metadata, DateTimeOffset.UtcNow));
            }
            return Task.FromResult(DatasetId);
        }

        public Task<string> CreateExperimentAsync(string datasetId, string nom, CancellationToken ct)
        {
            Journal.Add("CreateExperiment");
            if (CreateExperimentLeve) throw new InvalidOperationException("panne factice : CreateExperimentAsync");
            ExperimentsCrees.Add((datasetId, nom));
            return Task.FromResult("experiment-1");
        }

        public Task<string> CreateRunAsync(string experimentId, ExperimentRun run, CancellationToken ct)
        {
            Journal.Add("CreateRun");
            if (CreateRunLeve) throw new InvalidOperationException("panne factice : CreateRunAsync");
            var id = $"run-{_prochainRunId++}";
            RunsCrees.Add((experimentId, run, id));
            return Task.FromResult(id);
        }

        public Task EvaluerRunAsync(RunEvaluation evaluation, CancellationToken ct)
        {
            Journal.Add("EvaluerRun");
            if (EvaluerRunLeve) throw new InvalidOperationException("panne factice : EvaluerRunAsync");
            EvaluationsRecues.Add(evaluation);
            return Task.CompletedTask;
        }

        public Task AnnotateAsync(IReadOnlyList<SpanAnnotation> annotations, CancellationToken ct)
        {
            Journal.Add("Annotate");
            AnnotationsRecues.AddRange(annotations);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
