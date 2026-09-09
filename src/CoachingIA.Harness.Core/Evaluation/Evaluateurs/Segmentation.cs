using System.Globalization;
using System.Text;
using System.Text.Json;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Evaluation.Evaluateurs;

/// <summary>Ce que la découpe a produit sur une épreuve, et de quoi la juger.</summary>
public sealed record DecoupeObservee(
    IReadOnlyList<int> Frontieres,
    IReadOnlyList<string> Motifs,
    int Tours);

/// <summary>
/// Fabrique une session synthétique à partir d'une épreuve de segmentation, et
/// la fait découper par le vrai <see cref="TaskSegmenter"/>.
///
/// <para><strong>Pourquoi ces épreuves sont intégralement partageables.</strong>
/// <c>IsNewTask</c> ne consulte du prompt que trois choses : sa longueur, son
/// ouverture parmi deux listes fermées publiées dans le code source, et la
/// présence d'un mot référentiel d'une troisième liste fermée. Plus l'écart de
/// temps et l'aboutissement du tour précédent. <em>Rien d'autre.</em> Une
/// épreuve n'a donc pas à transporter un seul mot de l'apprenant : cinq champs
/// suffisent à reconstruire un tour que le segmenteur traite exactement comme
/// l'original.</para>
///
/// <para>La reconstruction se vérifie elle-même. Si le prompt fabriqué n'a pas
/// les propriétés que l'épreuve déclare, on ne joue pas l'épreuve : on rend une
/// panne. Une épreuve qui ment sur son contenu produirait un chiffre faux, ce
/// qui est pire que pas de chiffre.</para>
/// </summary>
public sealed class ProducteurSegmentation : IProducteur
{
    public string Famille => "segmentation";

    /// <summary>Un mot de remplissage qui n'est ni référentiel ni une ouverture connue.</summary>
    private const string Remplissage = " mot";

    /// <summary>Une ouverture neutre : ni corrective, ni de continuation, ni référentielle.</summary>
    private const string OuvertureNeutre = "Ajoute un module";

    /// <summary>Une date fixe : une épreuve rejouée demain doit donner le même résultat qu'aujourd'hui.</summary>
    private static readonly DateTimeOffset Origine = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public Production Produire(Epreuve epreuve)
    {
        if (epreuve.Entree.ValueKind != JsonValueKind.Object
            || !epreuve.Entree.TryGetProperty("tours", out var tours)
            || tours.ValueKind != JsonValueKind.Array)
            return Panne(epreuve, "l'épreuve n'a pas de tableau « tours » dans son entrée");

        var session = new TranscriptSession
        {
            SessionId = "eval-" + epreuve.Id,
            StartedAt = Origine,
            EndedAt = Origine,
        };

        var horloge = Origine;
        var rang = 0;
        foreach (var spec in tours.EnumerateArray())
        {
            rang++;
            var longueur = Entier(spec, "longueur", 40);
            var ouverture = Chaine(spec, "ouverture");
            var referentiel = Booleen(spec, "referentiel", false);
            var minutes = Reel(spec, "minutes_depuis", 1);
            var abouti = Booleen(spec, "abouti", true);

            var prompt = Fabriquer(longueur, ouverture, referentiel);
            if (!Verifier(prompt, longueur, ouverture, referentiel, out var pourquoi))
                return Panne(epreuve, "tour " + rang + " : " + pourquoi);

            // Le premier tour ne subit pas d'écart : il n'a pas de précédent.
            if (rang > 1) horloge = horloge.AddMinutes(minutes);

            session.Turns.Add(new Turn
            {
                SessionId = session.SessionId,
                Prompt = prompt,
                StartedAt = horloge,
                EndedAt = horloge.AddMinutes(1),
                StopReason = abouti ? "end_turn" : "tool_use",
            });
            horloge = horloge.AddMinutes(1);
        }

        if (session.Turns.Count == 0) return Panne(epreuve, "l'épreuve ne décrit aucun tour");
        session.EndedAt = session.Turns[^1].EndedAt;

        var taches = new TaskSegmenter().Segment(session);

        var frontieres = new List<int>();
        var motifs = new List<string>();
        var curseur = 0;
        foreach (var tache in taches)
        {
            frontieres.Add(curseur);
            curseur += tache.Turns.Count;
            motifs.AddRange(tache.Decisions);
        }

        var resume = string.Create(CultureInfo.InvariantCulture,
            $"{session.Turns.Count} tour(s) vers {taches.Count} tache(s), frontieres [{string.Join(", ", frontieres)}]");

        return new Production(resume, "segmenteur",
            Sujet: new DecoupeObservee(frontieres, motifs, session.Turns.Count));
    }

    private static Production Panne(Epreuve epreuve, string pourquoi)
        => new("épreuve " + epreuve.Id + " non jouable", "segmenteur") { Panne = pourquoi };

    /// <summary>
    /// Reconstruit un prompt qui a exactement les propriétés déclarées : la
    /// bonne longueur, la bonne ouverture, la bonne référentialité — et pas un
    /// mot de plus qui pourrait porter du sens.
    /// </summary>
    internal static string Fabriquer(int longueur, string ouverture, bool referentiel)
    {
        var noyau = new StringBuilder(ouverture.Length == 0 ? OuvertureNeutre : ouverture);

        // « ça » est le mot référentiel le plus court et le moins ambigu. On ne
        // l'ajoute que si l'ouverture n'en portait pas déjà un : certaines
        // ouvertures correctives (« ça ne marche ») sont référentielles par
        // construction.
        if (referentiel && !TaskSegmenter.EstReferentiel(noyau.ToString()))
            noyau.Append(" ça");

        while (noyau.Length < longueur) noyau.Append(Remplissage);
        var texte = noyau.Length > longueur ? noyau.ToString(0, longueur) : noyau.ToString();

        // Le remplissage tronqué peut tomber sur une espace. IsNewTask travaille
        // sur Trim() : un prompt qui finit par une espace y perdrait un
        // caractère, et la longueur éprouvée ne serait plus celle déclarée — ce
        // qui fait basculer les cas posés pile sur le seuil des prompts courts.
        // On rallonge le dernier mot plutôt que de laisser l'espace.
        return texte.Length > 0 && char.IsWhiteSpace(texte[^1])
            ? texte.TrimEnd().PadRight(longueur, 'o')
            : texte;
    }

    private static bool Verifier(string prompt, int longueur, string ouverture, bool referentiel, out string pourquoi)
    {
        if (prompt.Length != longueur)
        {
            pourquoi = "le noyau du prompt (" + prompt.Length + " car.) depasse la longueur declaree (" + longueur + ")";
            return false;
        }
        // La longueur que le segmenteur voit est celle d'après Trim(). Si les
        // deux divergent, l'épreuve n'éprouve pas ce qu'elle annonce.
        if (prompt.Trim().Length != longueur)
        {
            pourquoi = "le prompt fabrique perd " + (longueur - prompt.Trim().Length)
                     + " caractere(s) au Trim() : la longueur eprouvee ne serait pas celle declaree";
            return false;
        }
        if (ouverture.Length > 0 && !prompt.StartsWith(ouverture, StringComparison.Ordinal))
        {
            pourquoi = "le prompt fabrique n'ouvre pas par « " + ouverture + " »";
            return false;
        }
        if (TaskSegmenter.EstReferentiel(prompt) != referentiel)
        {
            pourquoi = referentiel
                ? "le prompt fabrique n'est pas referentiel alors que l'epreuve le declare tel"
                : "l'ouverture « " + ouverture + " » est referentielle : l'epreuve ne peut pas la declarer non referentielle";
            return false;
        }
        pourquoi = "";
        return true;
    }

    private static string Chaine(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Entier(JsonElement e, string nom, int defaut)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : defaut;

    private static double Reel(JsonElement e, string nom, double defaut)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : defaut;

    private static bool Booleen(JsonElement e, string nom, bool defaut)
        => e.TryGetProperty(nom, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True : defaut;
}

/// <summary>
/// La découpe tombe-t-elle sur les frontières de la référence ?
///
/// <para>Volontairement tout-ou-rien par épreuve, et non « exactitude par
/// tour ». Une frontière ratée en position 3 décale toutes les tâches
/// suivantes : l'exactitude par tour afficherait 90 % pour un découpage
/// entièrement faux en aval. Le chiffre serait rassurant et mensonger.</para>
///
/// <para>Les trois nombres qui servent vraiment à retoucher l'heuristique —
/// frontières manquées, frontières inventées, et Pk par fenêtre glissante —
/// vivent dans l'explication, pas dans le score. Ils diagnostiquent, ils ne
/// notent pas.</para>
/// </summary>
public sealed class EvaluateurFrontieres : IEvaluateur
{
    public string Nom => "frontiere_attendue";
    public string Famille => "segmentation";
    public Bareme Bareme => Baremes.Conformite;

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        var attendues = Frontieres(epreuve.Attendu);
        if (attendues is null) return null;   // l'épreuve ne déclare pas de frontières : hors de ce ressort
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not DecoupeObservee vue) return null;

        var obtenues = vue.Frontieres;
        var manquees = attendues.Where(f => !obtenues.Contains(f)).ToList();
        var inventees = obtenues.Where(f => !attendues.Contains(f)).ToList();
        var pk = Pk(attendues, obtenues, vue.Tours);

        var chiffres = string.Create(CultureInfo.InvariantCulture,
            $"{manquees.Count} manquee(s), {inventees.Count} inventee(s), Pk={pk:0.00}");

        if (manquees.Count == 0 && inventees.Count == 0)
            return Bareme.Rendre(Nom, "conforme", "la découpe tombe exactement sur la référence (" + chiffres + ").");

        var preuve = new StringBuilder();
        preuve.Append("attendu [").Append(string.Join(", ", attendues))
              .Append("] — obtenu [").Append(string.Join(", ", obtenues)).Append(']');
        var fautif = manquees.Count > 0 ? manquees[0] : inventees[0];
        preuve.Append(" ; motif au tour ").Append(fautif).Append(" : « ").Append(Motif(vue, fautif)).Append(" »");

        return Bareme.Rendre(Nom, "non_conforme",
            "la découpe s'écarte de la référence (" + chiffres + ").", preuve.ToString());
    }

    private static string Motif(DecoupeObservee vue, int tour)
        => tour >= 0 && tour < vue.Motifs.Count ? vue.Motifs[tour] : "motif inconnu";

    internal static IReadOnlyList<int>? Frontieres(JsonElement attendu)
    {
        if (attendu.ValueKind != JsonValueKind.Object) return null;
        if (!attendu.TryGetProperty("frontieres", out var f) || f.ValueKind != JsonValueKind.Array) return null;
        return [.. f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32())];
    }

    /// <summary>
    /// Pk : on fait glisser une fenêtre de k tours et on compte les désaccords
    /// sur « ces deux extrémités sont-elles dans la même tâche ? ». Zéro vaut
    /// accord parfait. La mesure ne s'effondre pas quand une frontière décale
    /// tout ce qui suit, contrairement à une exactitude par tour.
    /// </summary>
    public static double Pk(IReadOnlyList<int> reference, IReadOnlyList<int> hypothese, int tours)
    {
        if (tours < 2) return 0;
        var refSeg = Segments(reference, tours);
        var hypSeg = Segments(hypothese, tours);

        var k = Math.Max(2, (int)Math.Round((double)tours / Math.Max(1, reference.Count) / 2));
        if (k >= tours) k = tours - 1;

        var fenetres = 0;
        var desaccords = 0;
        for (var i = 0; i + k < tours; i++)
        {
            fenetres++;
            var memeRef = refSeg[i] == refSeg[i + k];
            var memeHyp = hypSeg[i] == hypSeg[i + k];
            if (memeRef != memeHyp) desaccords++;
        }
        return fenetres == 0 ? 0 : (double)desaccords / fenetres;
    }

    private static int[] Segments(IReadOnlyList<int> frontieres, int tours)
    {
        var seg = new int[tours];
        var courant = -1;
        for (var i = 0; i < tours; i++)
        {
            if (frontieres.Contains(i)) courant++;
            seg[i] = Math.Max(0, courant);
        }
        return seg;
    }
}

/// <summary>
/// La découpe s'appuie-t-elle sur la règle attendue ?
///
/// Deux découpes identiques obtenues pour des raisons différentes ne se valent
/// pas : la bonne frontière trouvée par la mauvaise règle retombera dès que le
/// cas changera un peu. C'est cet évaluateur qui dit <em>quelle</em> règle
/// retoucher, là où le précédent dit seulement qu'il y en a une à retoucher.
///
/// Facultatif : une épreuve qui ne déclare pas de motifs n'est pas de son
/// ressort, et il rend null plutôt que de peser dans un taux.
/// </summary>
public sealed class EvaluateurMotifs : IEvaluateur
{
    public string Nom => "motif_attendu";
    public string Famille => "segmentation";
    public Bareme Bareme => Baremes.Conformite;

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        if (epreuve.Attendu.ValueKind != JsonValueKind.Object) return null;
        if (!epreuve.Attendu.TryGetProperty("motifs", out var m) || m.ValueKind != JsonValueKind.Array) return null;
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not DecoupeObservee vue) return null;

        var attendus = m.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "").ToList();

        if (attendus.Count != vue.Motifs.Count)
            return Bareme.Rendre(Nom, "non_conforme",
                "la référence déclare " + attendus.Count + " motif(s), la découpe en a produit " + vue.Motifs.Count + ".",
                string.Join(" | ", vue.Motifs));

        for (var i = 0; i < attendus.Count; i++)
        {
            // Comparaison par fragment : la référence cite la raison, pas la
            // phrase entière, pour ne pas casser sur une virgule déplacée.
            if (vue.Motifs[i].Contains(attendus[i], StringComparison.Ordinal)) continue;
            return Bareme.Rendre(Nom, "non_conforme",
                "au tour " + i + ", la découpe invoque une autre règle que celle attendue.",
                "attendu « " + attendus[i] + " » — obtenu « " + vue.Motifs[i] + " »");
        }

        return Bareme.Rendre(Nom, "conforme",
            "les " + attendus.Count + " décisions invoquent bien les règles attendues.");
    }
}
