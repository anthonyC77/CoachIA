using System.Text.Json;
using CoachingIA.Harness.Core.Evaluation;
using CoachingIA.Harness.Core.Evaluation.Evaluateurs;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// La porte de la brique d'évaluation.
///
/// <para>Hermétique par construction : aucun évaluateur d'ici n'appelle
/// <c>claude -p</c>, et aucun ne lit les transcripts réels. Ce qui tourne ici
/// doit tourner à l'identique sur n'importe quel poste, hors ligne, en
/// millisecondes.</para>
///
/// <para>La comparaison se fait au <c>verdict.json</c> approuvé, pas à un seuil
/// flottant : un seuil se négocie à la baisse le jour où il gêne. Un écart fait
/// échouer <strong>dans les deux sens</strong> — une épreuve qui se met à passer
/// demande à être approuvée, ce qui laisse une trace dans <c>git diff</c>.</para>
///
/// <para>Pour approuver l'état courant :
/// <c>$env:COACHINGIA_APPROUVER_EVALS = "true"</c> puis relancer les tests.</para>
/// </summary>
public static class EvaluationTests
{
    public static void Run(Action<bool, string> check, string racine)
    {
        var dossierCas = Path.Combine(racine, "evals", "cas");
        var cheminVerdict = Path.Combine(racine, "evals", "verdict.json");
        var lensDir = Path.Combine(racine, "lenses");

        Console.WriteLine("Barèmes");
        foreach (var bareme in Baremes.Tous)
            check(bareme.EstCoherent(out var pourquoi), $"le barème « {bareme.Id} » est bien formé ({pourquoi})");

        check(double.IsNaN(Baremes.Conformite.ScoreDe("nexistepas")),
              "une étiquette inconnue ne vaut ni réussite ni échec, mais indécision");
        check(Baremes.Conformite.ScoreDe(Bareme.Indeterminable) is double.NaN,
              "l'indécision ne compte pas comme un échec");
        check(Baremes.Conformite.Rendre("x", "nexistepas", "").Etiquette == Bareme.Indeterminable,
              "une étiquette hors barème produit un verdict indécis plutôt qu'une exception");
        check(Baremes.Conformite.ScoreDe("conforme") == 1.0 && Baremes.Conformite.ScoreDe("non_conforme") == 0.0,
              "le score est dérivé du barème, jamais saisi");

        Console.WriteLine("\nMesure de découpe");
        check(EvaluateurFrontieres.Pk([0, 3], [0, 3], 6) == 0.0,
              "deux découpes identiques ont un Pk nul");
        check(EvaluateurFrontieres.Pk([0, 3], [0], 6) > 0.0,
              "une frontière manquée coûte du Pk");
        check(EvaluateurFrontieres.Pk([0], [0, 1, 2, 3, 4, 5], 6) > 0.0,
              "une découpe qui coupe partout coûte du Pk");

        Console.WriteLine("\nJeu d'épreuves");
        var jeu = JeuEpreuves.Charger(dossierCas, exigerPartageable: true);
        foreach (var a in jeu.Avertissements) Console.WriteLine("    ⚠ " + a);

        check(!jeu.EstVide, $"le jeu d'épreuves versionné se charge (obtenu {jeu.Epreuves.Count} épreuve(s) dans {dossierCas})");
        check(jeu.Avertissements.Count == 0,
              $"le jeu se charge sans avertissement ({jeu.Avertissements.Count} signalé(s))");
        check(jeu.Epreuves.All(e => e.Partageable && !e.CiteDuReel),
              "aucune épreuve du dossier versionné ne cite un transcript réel — c'est un garde de vie privée, pas une intention");
        check(jeu.Epreuves.All(e => e.Intitule.Length > 0),
              "chaque épreuve dit ce qu'elle met à l'épreuve");
        check(jeu.Epreuves.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == jeu.Epreuves.Count,
              "les identifiants d'épreuve sont uniques");

        Console.WriteLine("\nCampagne");

        // La porte et la commande « coachingia evaluer » jouent la même
        // composition, prise au même endroit. Deux listes recopiées auraient
        // divergé au premier évaluateur ajouté, et la commande aurait mesuré
        // autre chose que ce que la porte garde.
        var producteurs = CampagneStandard.Producteurs(lensDir);
        var evaluateurs = CampagneStandard.Porte();

        check(evaluateurs.All(e => Baremes.Trouver(e.Bareme.Id) is not null),
              "chaque évaluateur déclare un barème connu");
        check(evaluateurs.Select(e => e.Nom).Distinct(StringComparer.Ordinal).Count() == evaluateurs.Count,
              "les noms d'évaluateur sont uniques");

        // Un évaluateur écrit puis jamais branché ne mesure rien et ne le dit
        // pas. La composition standard doit donc épuiser ce que le noyau sait
        // faire : ou il est dans la porte, ou il est déclaré juge.
        var connus = CampagneStandard.Porte().Select(e => e.GetType())
            .Concat(CampagneStandard.Juges(new CliFactice(null)).Select(e => e.GetType()))
            .ToHashSet();
        var oublies = typeof(IEvaluateur).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IEvaluateur).IsAssignableFrom(t))
            .Where(t => !connus.Contains(t))
            .Select(t => t.Name)
            .ToList();
        check(oublies.Count == 0,
              $"aucun évaluateur du noyau n'est laissé hors de la composition ({oublies.Count} oublié(s){(oublies.Count == 0 ? "" : " : " + string.Join(", ", oublies))})");

        var famillesSansProducteur = jeu.Epreuves.Select(e => e.Famille)
            .Distinct(StringComparer.Ordinal)
            .Where(f => !producteurs.Any(p => p.Famille == f))
            .ToList();
        check(famillesSansProducteur.Count == 0,
              $"chaque famille du jeu a son producteur dans la composition ({string.Join(", ", famillesSansProducteur)})");
        check(evaluateurs.All(e => e.Deterministe),
              "aucun évaluateur de la porte n'appelle un juge : la porte reste hermétique et rapide");

        var resultat = new Campagne(producteurs, evaluateurs).Jouer(jeu);
        foreach (var a in resultat.Avertissements) Console.WriteLine("    ⚠ " + a);

        check(resultat.Avertissements.Count == 0,
              $"la campagne se joue sans avertissement ({resultat.Avertissements.Count} signalé(s))");
        check(resultat.Verdicts.Count > 0, $"la campagne rend des verdicts (obtenu {resultat.Verdicts.Count})");
        check(resultat.Productions.Count == jeu.Epreuves.Count
              && jeu.Epreuves.All(e => resultat.Productions.ContainsKey(e.Id)),
              $"la campagne garde une production par épreuve jouée, celle que les évaluateurs ont jugée (obtenu {resultat.Productions.Count} pour {jeu.Epreuves.Count} épreuves)");

        var indecis = resultat.Verdicts.Where(l => l.Verdict.Indecis).ToList();
        check(indecis.Count == 0,
              $"aucune épreuve n'est restée indécise ({string.Join(", ", indecis.Take(3).Select(l => l.Epreuve + " : " + l.Verdict.Explication))})");

        check(resultat.Verdicts.All(l => l.Verdict.Explication.Length > 0),
              "chaque verdict porte la phrase qui le justifie — un chiffre seul ne se conteste pas");
        check(resultat.Verdicts.Where(l => !l.Verdict.Reussi && !l.Verdict.Indecis).All(l => l.Verdict.Preuve is { Length: > 0 }),
              "chaque échec cite le fragment fautif");

        // Les promesses du produit doivent être effectivement exercées : une
        // porte verte parce qu'aucun évaluateur ne s'est appliqué serait pire
        // qu'une porte rouge.
        foreach (var attendu in evaluateurs.Select(e => e.Nom))
            check(resultat.Verdicts.Any(l => l.Verdict.Evaluateur == attendu),
                  $"l'évaluateur « {attendu} » s'est appliqué à au moins une épreuve");

        // Les épreuves fabriquées fausses déclarent ce qu'elles doivent faire
        // dire. Sans ce contrôle, un évaluateur qui cesserait d'attraper quelque
        // chose ferait simplement basculer verdict.json de non_conforme à
        // conforme, et la porte annoncerait cette panne comme un « progrès ».
        var ecartsDeclares = new List<string>();
        var declarees = 0;
        foreach (var epreuve in jeu.Epreuves)
        {
            if (epreuve.Attendu.ValueKind != JsonValueKind.Object) continue;
            if (!epreuve.Attendu.TryGetProperty("etiquettes", out var voulues)
                || voulues.ValueKind != JsonValueKind.Object) continue;

            foreach (var p in voulues.EnumerateObject())
            {
                declarees++;
                var obtenu = resultat.Verdicts
                    .FirstOrDefault(l => l.Epreuve == epreuve.Id && l.Verdict.Evaluateur == p.Name);

                if (obtenu is null) { ecartsDeclares.Add($"{epreuve.Id}/{p.Name} : aucun verdict rendu"); continue; }
                var voulu = p.Value.GetString() ?? "";
                if (!string.Equals(obtenu.Verdict.Etiquette, voulu, StringComparison.Ordinal))
                    ecartsDeclares.Add($"{epreuve.Id}/{p.Name} : attendu {voulu}, obtenu {obtenu.Verdict.Etiquette}");
            }
        }

        foreach (var e in ecartsDeclares) Console.WriteLine("    ✗ " + e);
        check(ecartsDeclares.Count == 0,
              $"les {declarees} étiquettes déclarées par les épreuves sont obtenues"
              + (ecartsDeclares.Count == 0 ? "" : $" ({ecartsDeclares.Count} écart(s))"));

        var fabriquees = jeu.Epreuves.Count(e => e.Etiquettes.Contains("fabriquee-fausse", StringComparer.Ordinal));
        check(fabriquees >= 6,
              $"le jeu contient des cas fabriqués pour être attrapés (obtenu {fabriquees}) — sans eux, on ne saurait jamais si un évaluateur mord encore");

        Console.WriteLine("\nÉtat approuvé");
        if (string.Equals(Environment.GetEnvironmentVariable("COACHINGIA_APPROUVER_EVALS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cheminVerdict)!);
            File.WriteAllText(cheminVerdict, VerdictApprouve.Serialiser(resultat, evaluateurs));
            Console.WriteLine("    verdict approuvé réécrit : " + cheminVerdict);
        }

        var avertissements = new List<string>();
        var approuve = VerdictApprouve.Lire(cheminVerdict, avertissements);
        foreach (var a in avertissements) Console.WriteLine("    ⚠ " + a);

        check(approuve.Count > 0, $"un verdict approuvé existe sous {cheminVerdict}");

        var ecarts = VerdictApprouve.Comparer(approuve, resultat, evaluateurs);
        var regressions = ecarts.Where(e => e.Sens == "régression").ToList();
        var autres = ecarts.Where(e => e.Sens != "régression").ToList();

        foreach (var e in ecarts)
            Console.WriteLine($"    {e.Sens} : {e.Epreuve} / {e.Evaluateur} — {e.Avant} → {e.Apres}");

        check(regressions.Count == 0,
              $"la découpe et le bilan restent d'accord avec l'état approuvé ({regressions.Count} régression(s)"
              + (regressions.Count == 0 ? ")" : " : " + string.Join(", ", regressions.Select(r => r.Epreuve)) + ")"));
        check(autres.Count == 0,
              $"rien n'a bougé sans être approuvé ({autres.Count} écart(s)"
              + (autres.Count == 0 ? ")" : " : " + string.Join(", ", autres.Select(r => r.Epreuve + " " + r.Sens)) + ")"));

        // Le verdict approuvé ne doit jamais contenir de sortie de juge : sinon
        // il bougerait à chaque exécution, et l'archivage se remettrait à
        // tourner pour rien.
        check(!VerdictApprouve.Serialiser(resultat, evaluateurs).Contains("claude -p", StringComparison.Ordinal),
              "le verdict approuvé ne contient aucune sortie de juge — il doit rester strictement déterministe");

        Console.WriteLine("\nLa porte, et le code qu'elle rend");
        var verte = Porte.Juger(resultat, approuve, evaluateurs);
        check(verte.Code == 0, $"rien n'a bougé : la porte rend 0 (obtenu {verte.Code})");

        var bouge = new Dictionary<string, string>(approuve, StringComparer.Ordinal);
        var cleBougee = bouge.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        bouge[cleBougee] = bouge[cleBougee] switch
        {
            "conforme" => "non_conforme", "non_conforme" => "conforme",
            "tenue" => "rompue", _ => "tenue",
        };
        var rouge = Porte.Juger(resultat, bouge, evaluateurs);
        check(rouge.Code == 1, $"un seul écart suffit à faire rendre 1 (obtenu {rouge.Code})");
        check(Porte.Rendre(resultat, rouge).Contains(cleBougee.Split('|')[0], StringComparison.Ordinal),
              $"et le rapport nomme l'épreuve qui a bougé ({cleBougee})");

        var rien = Porte.Juger(new Campagne(producteurs, evaluateurs).Jouer(new JeuEpreuves()), approuve, evaluateurs);
        check(rien.Code == 2,
              $"une campagne qui n'a rien mesuré rend 2, jamais 0 — on ne dit pas vert ce qu'on n'a pas mesuré (obtenu {rien.Code})");

        var sansEtalon = Porte.Juger(resultat, new Dictionary<string, string>(StringComparer.Ordinal), evaluateurs);
        check(sansEtalon.Code == 2,
              $"sans état approuvé, la porte rend 2 plutôt que de déclarer tout nouveau (obtenu {sansEtalon.Code})");

        Console.WriteLine("\nLe juge, et sa place hors de la porte");
        var reference = new SansInvention();
        var cas = jeu.Epreuves.First(e => e.Famille == "reecriture");
        var vue = new ProducteurReecriture().Produire(cas);

        var cliAccord = new CliFactice("""{"structured_output":{"verdict":"conforme","raison":"tout ce que la réécriture cite figure déjà dans l'original"}}""");
        var accord = new JugeReecriture(cliAccord, reference).Evaluer(cas, vue);
        check(accord is not null && accord.Etiquette == "accord",
              $"le juge dit l'accord quand il retrouve la réponse du code (obtenu {accord?.Etiquette ?? "rien"})");
        check(cliAccord.DerniereInstruction is { } inst
              && inst.Contains(reference.Contrainte, StringComparison.Ordinal)
              && inst.Contains("Corrige le calcul de TVA", StringComparison.Ordinal),
              "l'instruction du juge porte la contrainte mesurée et le texte jugé, pas une question vague");
        check(!new JugeReecriture(cliAccord, reference).Deterministe,
              "le juge se déclare non déterministe — c'est ce qui le tient hors de l'état approuvé");

        var desaccord = new JugeReecriture(
            new CliFactice("""{"structured_output":{"verdict":"non_conforme","raison":"« Invoice.cs » sort de nulle part"}}"""),
            reference).Evaluer(cas, vue);
        check(desaccord is not null && desaccord.Etiquette == "desaccord"
              && desaccord.Preuve is { } cite && cite.Contains("Invoice.cs", StringComparison.Ordinal),
              $"un désaccord cite la phrase du juge plutôt que de la résumer ({desaccord?.Preuve})");

        var doute = new JugeReecriture(
            new CliFactice("""{"structured_output":{"verdict":"doute","raison":"question de goût"}}"""),
            reference).Evaluer(cas, vue);
        check(doute is not null && doute.Etiquette == "douteux",
              $"le juge a le droit de douter, comme SceneValidator (obtenu {doute?.Etiquette ?? "rien"})");

        var panne = new JugeReecriture(new CliFactice(null, "délai dépassé (90 s)"), reference).Evaluer(cas, vue);
        check(panne is not null && panne.Indecis && panne.Explication.Contains("délai", StringComparison.Ordinal),
              $"une panne du juge est une indécision qui dit pourquoi, jamais un échec ({panne?.Explication})");

        var avecJuge = CampagneStandard.Juges(cliAccord);
        var melange = new Campagne(producteurs, [.. evaluateurs, .. avecJuge]).Jouer(jeu);
        check(melange.Verdicts.Any(l => avecJuge.Any(j => j.Nom == l.Verdict.Evaluateur)),
              "la campagne sait jouer le juge quand on le lui donne");
        check(!VerdictApprouve.Serialiser(melange, [.. evaluateurs, .. avecJuge])
                  .Contains("juge", StringComparison.Ordinal),
              "et aucune de ses lignes n'entre dans l'état approuvé, qui doit rester reproductible");
        check(Porte.Juger(melange, approuve, [.. evaluateurs, .. avecJuge]).Code == 0,
              "un juge en désaccord ne fait pas tomber la porte : il informe, il ne garde rien");

        Console.WriteLine("\nCe que la campagne mesure");
        foreach (var r in resultat.ParEvaluateur())
            Console.WriteLine($"    {r.Nom,-28} {r.Reussis}/{r.Epreuves} réussis, {r.Echoues} échec(s), {r.Indecis} indécis");

        // Un taux sans les phrases qui l'expliquent est un chiffre qu'on ne peut
        // ni contester ni corriger — la même règle que pour une observation de
        // bilan. Ce qui ne passe pas doit dire pourquoi, ici comme là-bas.
        var aExpliquer = resultat.Verdicts.Where(l => !l.Verdict.Reussi).ToList();
        if (aExpliquer.Count > 0)
        {
            Console.WriteLine("\nCe qui ne passe pas, et pourquoi");
            foreach (var l in aExpliquer.OrderBy(l => l.Epreuve, StringComparer.Ordinal))
                Console.WriteLine($"    {l.Epreuve}/{l.Verdict.Evaluateur} — {l.Verdict.Explication}");
        }

        Console.WriteLine("\nLe contenu réellement livré");
        foreach (var l in resultat.Verdicts.Where(l => l.Verdict.Evaluateur == "pack_sans_erreur"))
            Console.WriteLine("    " + l.Verdict.Explication);
    }
}
