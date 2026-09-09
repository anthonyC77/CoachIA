using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Evaluation.Evaluateurs;

/// <summary>Une observation réduite à ce que la lentille n'a pas le droit de changer.</summary>
public sealed record ValeurObservation(string SignalKey, int Level, double Value);

/// <summary>
/// Un bilan construit pour être mis à l'épreuve, avec tout ce que les
/// évaluateurs ont besoin de lire — précalculé ici pour qu'aucun d'eux n'ait à
/// toucher <c>SignalSpecs</c>, façade statique globale qu'une autre suite de
/// tests mute déjà.
/// </summary>
public sealed record BilanObserve(
    WeeklyReview Revue,
    string Markdown,
    string Empreinte,
    string EmpreinteBis,
    IReadOnlyList<ValeurObservation> Neutre,
    IReadOnlyDictionary<string, IReadOnlyList<ValeurObservation>> AutresLentilles,
    bool? WinAtteintCible,
    double WinDelta,
    double PlancherProgres,
    int MaxObservations,
    IReadOnlyDictionary<string, bool> SignalEnDefaut,
    IReadOnlyList<string> TermesNeutres,
    DateOnly Lundi,
    WeeklyReview? RevueImagee,
    string MarkdownImage);

/// <summary>
/// Construit un bilan à partir d'une épreuve, puis le reconstruit une seconde
/// fois de façon indépendante pour pouvoir mesurer sa reproductibilité.
///
/// <para>Les sessions sont <strong>synthétiques et datées en absolu</strong>.
/// La porte des tests ne doit jamais lire les transcripts réels : elle serait
/// lente, non reproductible, et différente sur chaque poste. La semaine visée
/// est passée explicitement plutôt que déduite de l'horloge, sans quoi le même
/// jeu rendrait un résultat différent d'une semaine à l'autre.</para>
/// </summary>
public sealed class ProducteurBilan : IProducteur
{
    /// <summary>Un lundi fixe. Toute l'arithmétique des épreuves part de là.</summary>
    private static readonly DateOnly LundiOrigine = new(2026, 1, 5);

    private readonly string _lensDir;
    private readonly LensCatalog _catalogue;

    public string Famille => "bilan";

    public ProducteurBilan(string lensDir)
    {
        _lensDir = lensDir;
        _catalogue = LensCatalog.Load(lensDir);

        // On monte explicitement le corpus plutôt que d'hériter de celui qu'une
        // suite précédente aurait laissé dans la façade statique. Une évaluation
        // dont le résultat dépend de l'ordre des suites ne mesure rien.
        var avertissements = new List<string>();
        SignalSpecs.Use(MaturityCorpus.Load(_lensDir, avertissements));
    }

    public Production Produire(Epreuve epreuve)
    {
        if (epreuve.Entree.ValueKind != JsonValueKind.Object
            || !epreuve.Entree.TryGetProperty("semaines", out var semaines)
            || semaines.ValueKind != JsonValueKind.Array
            || semaines.GetArrayLength() == 0)
            return Panne(epreuve, "l'épreuve n'a pas de tableau « semaines » dans son entrée");

        var specs = new List<(int Decalage, bool Brouillon, int Taches)>();
        foreach (var s in semaines.EnumerateArray())
            specs.Add((
                Entier(s, "decalage", 0),
                Booleen(s, "brouillon", true),
                Entier(s, "taches", 3)));

        var lundiVise = LundiOrigine.AddDays(7 * specs[^1].Decalage);

        var revue = Construire(specs, "neutre", lundiVise, out var markdown, out var html);
        if (revue is null) return Panne(epreuve, "aucune tâche n'a été produite pour la semaine visée");

        // Deuxième construction, entièrement indépendante : c'est la seule façon
        // de vérifier la présupposition de ReviewArchive, à savoir qu'un bilan
        // régénéré à l'identique l'est vraiment.
        _ = Construire(specs, "neutre", lundiVise, out var markdownBis, out var htmlBis);

        // La lentille neutre ne porte aucune scène : les promesses qui parlent
        // de l'image ne s'y exerceraient jamais et se déclareraient vertes sans
        // rien avoir vérifié. On construit donc aussi un rendu habillé.
        var autres = new Dictionary<string, IReadOnlyList<ValeurObservation>>(StringComparer.Ordinal);
        WeeklyReview? imagee = null;
        var markdownImage = "";
        foreach (var lentille in (string[])["starcraft2", "echecs"])
        {
            var autre = Construire(specs, lentille, lundiVise, out var md, out _);
            if (autre is null) continue;
            autres[lentille] = Valeurs(autre);
            if (lentille == "starcraft2") { imagee = autre; markdownImage = md; }
        }

        var neutre = new LensWriter(_catalogue.Resolve("neutre")).ForWeek(revue.Week);
        var termes = new List<string>();
        for (var niveau = 1; niveau <= 5; niveau++) termes.Add(neutre.TermFor(niveau, ""));

        var enDefaut = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var o in revue.Observations.Concat(imagee?.Observations ?? []))
        {
            var spec = SignalSpecs.Find(o.SignalKey);
            enDefaut[o.SignalKey] = spec is not null && spec.Gap(o.Value) > 0;
        }

        bool? atteintCible = null;
        var delta = 0.0;
        if (revue.Win is { } win)
        {
            var spec = SignalSpecs.Find(win.SignalKey);
            atteintCible = spec is not null && spec.Meets(win.After);
            if (spec is not null)
            {
                var echelle = spec.IsRatio ? 1.0 : Math.Max(1.0, spec.Target);
                delta = (spec.HigherIsBetter ? win.After - win.Before : win.Before - win.After) / echelle;
            }
        }

        var sujet = new BilanObserve(
            revue, markdown,
            Empreinte(markdown + html), Empreinte(markdownBis + htmlBis),
            Valeurs(revue), autres,
            atteintCible, delta,
            new WeeklyReviewBuilder().ProgressFloor,
            new WeeklyReviewBuilder().MaxObservations,
            enDefaut, termes, lundiVise, imagee, markdownImage);

        return new Production(
            revue.Week + " : " + revue.Observations.Count + " observation(s)",
            "bilan", Sujet: sujet);
    }

    private WeeklyReview? Construire(
        List<(int Decalage, bool Brouillon, int Taches)> specs, string lentille,
        DateOnly lundiVise, out string markdown, out string html)
    {
        var sessions = specs
            .Select(s => Session(LundiOrigine.AddDays(7 * s.Decalage), s.Brouillon, s.Taches))
            .ToList();

        var writer = new LensWriter(_catalogue.Resolve(lentille));
        var revue = new WeeklyReviewBuilder().Build(sessions, UsageAnalyzer.WeekKey(Quand(lundiVise)), writer);

        if (revue.IsEmpty) { markdown = ""; html = ""; return null; }

        var pourEcriture = writer.ForWeek(revue.Week);
        markdown = ReviewRenderer.ToMarkdown(revue, pourEcriture);
        html = HtmlReviewRenderer.Render(revue, pourEcriture);
        return revue;
    }

    private static IReadOnlyList<ValeurObservation> Valeurs(WeeklyReview revue)
        => [.. revue.Observations.Select(o => new ValeurObservation(o.SignalKey, o.Level, o.Value))];

    private static string Empreinte(string texte)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texte.ReplaceLineEndings("\n"))));

    private static DateTimeOffset Quand(DateOnly jour)
        => new(jour.Year, jour.Month, jour.Day, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Une session fabriquée. « Brouillon » produit des tâches sans critère
    /// d'acceptation, sans vérification et avec des reprises ; l'inverse produit
    /// des tâches propres. De quoi faire bouger les signaux dans les deux sens
    /// sans jamais toucher un transcript réel.
    /// </summary>
    private static TranscriptSession Session(DateOnly lundi, bool brouillon, int taches)
    {
        var depart = Quand(lundi);
        var session = new TranscriptSession
        {
            SessionId = "eval-" + lundi.ToString("yyyyMMdd") + (brouillon ? "-b" : "-p"),
            StartedAt = depart,
            EndedAt = depart.AddHours(2),
        };

        for (var t = 0; t < taches; t++)
        {
            var quand = depart.AddMinutes(t * 30);
            var prompt = brouillon
                ? "Regarde le module " + t
                : "Ajoute un test sur le module " + t + ". Il doit passer au vert avant la fin.";

            var tour = new Turn
            {
                SessionId = session.SessionId, Prompt = prompt,
                StartedAt = quand, EndedAt = quand.AddMinutes(8), StopReason = "end_turn",
            };
            tour.Steps.Add(new AssistantStep
            {
                At = quand, Model = "claude-opus-5",
                InputTokens = brouillon ? 40_000 : 15_000,
                CacheReadTokens = brouillon ? 40_000 : 90_000,
                OutputTokens = 900,
            });
            for (var c = 0; c < 6; c++)
                tour.ToolCalls.Add(new ToolCall
                {
                    Id = "c" + t + c, Name = brouillon ? "Edit" : "Grep", CalledAt = quand.AddMinutes(c),
                });

            if (!brouillon)
                tour.ToolCalls.Add(new ToolCall
                {
                    Id = "v" + t, Name = "Bash", CalledAt = quand.AddMinutes(7),
                    InputJson = "{\"command\":\"dotnet test\"}",
                });
            session.Turns.Add(tour);

            if (brouillon)
            {
                var reprise = new Turn
                {
                    SessionId = session.SessionId, Prompt = "Non, ce n'est pas ça",
                    StartedAt = quand.AddMinutes(9), EndedAt = quand.AddMinutes(12), StopReason = "end_turn",
                };
                reprise.Steps.Add(new AssistantStep
                {
                    At = quand.AddMinutes(9), Model = "claude-opus-5",
                    InputTokens = 41_000, CacheReadTokens = 41_000, OutputTokens = 300,
                });
                session.Turns.Add(reprise);
            }
        }

        session.EndedAt = session.Turns[^1].EndedAt;
        return session;
    }

    private static Production Panne(Epreuve epreuve, string pourquoi)
        => new("épreuve " + epreuve.Id + " non jouable", "bilan") { Panne = pourquoi };

    private static int Entier(JsonElement e, string nom, int defaut)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : defaut;

    private static bool Booleen(JsonElement e, string nom, bool defaut)
        => e.TryGetProperty(nom, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True : defaut;
}

/// <summary>
/// Base commune aux évaluateurs de promesse.
///
/// Chaque promesse cite sa source dans son commentaire. Ce n'est pas de la
/// documentation : c'est le garde anti-Goodhart. Le jour où une promesse gêne,
/// la pente naturelle est de modifier l'évaluateur plutôt que le code — citer
/// la source oblige à ouvrir le README et à assumer qu'on change la promesse.
/// </summary>
public abstract class PromesseBilan : IEvaluateur
{
    public abstract string Nom { get; }
    public string Famille => "bilan";
    public Bareme Bareme => Baremes.Promesse;

    public Verdict? Evaluer(Epreuve epreuve, Production production)
    {
        if (production.Panne is { } panne) return Bareme.Indecis(Nom, panne);
        if (production.Sujet is not BilanObserve bilan) return null;
        return Juger(bilan);
    }

    protected abstract Verdict? Juger(BilanObserve bilan);

    protected Verdict Tenue(string explication) => Bareme.Rendre(Nom, "tenue", explication);
    protected Verdict Rompue(string explication, string? preuve = null)
        => Bareme.Rendre(Nom, "rompue", explication, preuve);
}

/// <summary>README:139 — « aucune observation sans exemple ». Chaque constat cite une tâche réelle et datée.</summary>
public sealed class ObservationAvecExemple : PromesseBilan
{
    public override string Nom => "observation_avec_exemple";

    protected override Verdict? Juger(BilanObserve bilan)
    {
        if (bilan.Revue.Observations.Count == 0) return null;

        foreach (var o in bilan.Revue.Observations)
        {
            if (o.TaskTitle.Length == 0)
                return Rompue("une observation ne cite aucune tâche.", o.SignalKey);
            if (o.Evidence.Length == 0)
                return Rompue("une observation ne porte pas la phrase qui explique son chiffre.", o.SignalKey);
            if (o.TaskDate < bilan.Lundi || o.TaskDate >= bilan.Lundi.AddDays(7))
                return Rompue("une observation cite une tâche hors de la semaine jugée.",
                    o.SignalKey + " : " + o.TaskDate.ToString("yyyy-MM-dd"));
        }

        return Tenue("les " + bilan.Revue.Observations.Count
            + " observations citent toutes une tâche datée de la semaine, et la phrase qui explique le chiffre.");
    }
}

/// <summary>
/// README:142 — « aucune félicitation de politesse ». La réussite n'apparaît
/// que si un progrès réel dépasse le plancher <em>et</em> franchit la cible.
///
/// Sans réussite, l'évaluateur ne s'applique pas : il rend null plutôt que de
/// se déclarer vert sur une semaine où il n'y avait rien à féliciter.
/// </summary>
public sealed class PasDeFelicitationPolie : PromesseBilan
{
    public override string Nom => "pas_de_felicitation_polie";

    protected override Verdict? Juger(BilanObserve bilan)
    {
        if (bilan.Revue.Win is not { } win) return null;

        if (bilan.WinDelta < bilan.PlancherProgres)
            return Rompue("une réussite est annoncée pour un progrès sous le plancher du bruit.",
                win.SignalKey + " : delta " + bilan.WinDelta.ToString("0.000") + " < " + bilan.PlancherProgres.ToString("0.000"));

        if (bilan.WinAtteintCible != true)
            return Rompue("une réussite est annoncée alors que le signal reste sous sa cible.",
                win.SignalKey + " : " + win.After.ToString("0.000"));

        return Tenue("la réussite annoncée franchit la cible et dépasse le plancher (" + win.SignalKey + ").");
    }
}

/// <summary>
/// README:378 — « le fait passe devant ». Le constat précède l'image, et
/// retirer l'image laisse une phrase complète.
///
/// La séparabilité se vérifie structurellement : aucune ligne du rendu ne porte
/// à la fois le fait et l'image. Tant que ce sont deux lignes, supprimer l'une
/// laisse l'autre entière.
/// </summary>
public sealed class FaitAvantImage : PromesseBilan
{
    public override string Nom => "fait_avant_image";

    protected override Verdict? Juger(BilanObserve bilan)
    {
        // La lentille neutre n'habille rien : c'est le rendu lentillé qui porte
        // les images, donc lui seul peut éprouver l'ordre du fait et de l'image.
        var habillees = (bilan.RevueImagee?.Observations ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.Flourish)).ToList();
        if (habillees.Count == 0) return null;

        var lignes = bilan.MarkdownImage.Split('\n');
        foreach (var o in habillees)
        {
            var fait = o.Statement.Trim();
            var image = Fragment(o.Flourish!);

            var iFait = bilan.MarkdownImage.IndexOf(fait, StringComparison.OrdinalIgnoreCase);
            var iImage = bilan.MarkdownImage.IndexOf(image, StringComparison.OrdinalIgnoreCase);

            if (iFait < 0) return Rompue("le fait mesuré n'apparaît pas dans le rendu.", fait);
            if (iImage < 0) return Rompue("l'image n'apparaît pas dans le rendu.", image);
            if (iImage < iFait)
                return Rompue("l'image passe devant le fait qu'elle habille.", fait + " / " + image);

            foreach (var ligne in lignes)
                if (ligne.Contains(fait, StringComparison.OrdinalIgnoreCase)
                    && ligne.Contains(image, StringComparison.OrdinalIgnoreCase))
                    return Rompue("le fait et l'image partagent une ligne : retirer l'image mutilerait le constat.", ligne.Trim());
        }

        return Tenue("les " + habillees.Count
            + " observations habillées gardent le fait devant, sur une ligne distincte de l'image.");
    }

    /// <summary>
    /// Un fragment interne de l'image : le rendu peut lui capitaliser la
    /// première lettre ou lui ajouter un point, ce qui casserait une comparaison
    /// bord à bord.
    /// </summary>
    private static string Fragment(string image)
    {
        var texte = image.Trim();
        if (texte.Length <= 2) return texte;
        var debut = 1;
        var longueur = Math.Min(30, texte.Length - debut - (texte.EndsWith('.') ? 1 : 0));
        return longueur <= 0 ? texte : texte.Substring(debut, longueur);
    }
}

/// <summary>README:387 — l'image ne s'affiche que tant que le défaut existe.</summary>
public sealed class ImageSeulementSiDefaut : PromesseBilan
{
    public override string Nom => "image_seulement_si_defaut";

    protected override Verdict? Juger(BilanObserve bilan)
    {
        var habillees = (bilan.RevueImagee?.Observations ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.Flourish)).ToList();
        if (habillees.Count == 0) return null;

        foreach (var o in habillees)
            if (!bilan.SignalEnDefaut.TryGetValue(o.SignalKey, out var defaut) || !defaut)
                return Rompue("une image habille un signal qui n'est pas en défaut.", o.SignalKey);

        return Tenue("les " + habillees.Count + " images habillent toutes un signal effectivement en défaut.");
    }
}

/// <summary>
/// README:380 — « la lentille ne mesure rien ». Le même jeu rendu sous trois
/// lentilles doit donner des valeurs identiques au bit près.
/// </summary>
public sealed class LentilleNeMesureRien : PromesseBilan
{
    public override string Nom => "lentille_ne_mesure_rien";

    protected override Verdict? Juger(BilanObserve bilan)
    {
        if (bilan.AutresLentilles.Count == 0) return null;

        foreach (var (nom, valeurs) in bilan.AutresLentilles)
        {
            if (valeurs.Count != bilan.Neutre.Count)
                return Rompue("la lentille « " + nom + " » ne produit pas le même nombre d'observations que la neutre.",
                    valeurs.Count + " contre " + bilan.Neutre.Count);

            for (var i = 0; i < valeurs.Count; i++)
            {
                var a = bilan.Neutre[i];
                var b = valeurs[i];
                if (a.SignalKey == b.SignalKey && a.Level == b.Level
                    && a.Value.Equals(b.Value)) continue;

                return Rompue("la lentille « " + nom + " » change une mesure.",
                    a.SignalKey + "=" + a.Value.ToString("R") + " contre " + b.SignalKey + "=" + b.Value.ToString("R"));
            }
        }

        return Tenue("les " + (bilan.AutresLentilles.Count + 1)
            + " lentilles donnent exactement les mêmes signaux, paliers et valeurs.");
    }
}

/// <summary>
/// README:382 — « la lentille neutre est complète ». Toute clé absente d'une
/// autre lentille retombe sur elle : si elle a des trous, le repli découvre du
/// vide.
/// </summary>
public sealed class LentilleNeutreComplete : PromesseBilan
{
    public override string Nom => "lentille_neutre_complete";

    protected override Verdict? Juger(BilanObserve bilan)
    {
        for (var niveau = 0; niveau < bilan.TermesNeutres.Count; niveau++)
            if (string.IsNullOrWhiteSpace(bilan.TermesNeutres[niveau]))
                return Rompue("la lentille neutre n'a pas de nom pour le palier " + (niveau + 1) + ".",
                    "palier " + (niveau + 1));

        return Tenue("la lentille neutre nomme ses " + bilan.TermesNeutres.Count + " paliers.");
    }
}

/// <summary>WeeklyReview.cs:60 — trois observations au plus : au-delà, plus personne ne retient rien.</summary>
public sealed class TroisObservationsAuPlus : PromesseBilan
{
    public override string Nom => "trois_observations_au_plus";

    protected override Verdict? Juger(BilanObserve bilan)
    {
        var compte = bilan.Revue.Observations.Count;
        return compte <= bilan.MaxObservations
            ? Tenue("le bilan porte " + compte + " observation(s), pour un plafond de " + bilan.MaxObservations + ".")
            : Rompue("le bilan dépasse son propre plafond d'observations.",
                compte + " > " + bilan.MaxObservations);
    }
}

/// <summary>
/// La présupposition de <c>ReviewArchive</c> : un bilan régénéré à l'identique
/// doit être identique, sans quoi le no-op sur contenu inchangé ne se déclenche
/// jamais et le dossier d'archives se remplit à chaque exécution.
///
/// Rien ne testait cela, alors que tout le mécanisme d'archivage en dépend.
/// </summary>
public sealed class BilanReproductible : PromesseBilan
{
    public override string Nom => "bilan_reproductible";

    protected override Verdict? Juger(BilanObserve bilan)
        => bilan.Empreinte == bilan.EmpreinteBis
            ? Tenue("deux constructions indépendantes du même bilan donnent la même empreinte.")
            : Rompue("deux constructions du même bilan diffèrent : l'archivage ne pourra jamais conclure « inchangé ».",
                bilan.Empreinte[..12] + " contre " + bilan.EmpreinteBis[..12]);
}
