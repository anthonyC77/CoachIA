using System.Text.RegularExpressions;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>La gravité d'un reproche fait à une scène.</summary>
public enum SceneIssueLevel { Erreur, Doute }

public sealed record SceneIssue(SceneIssueLevel Level, string Rule, string Detail)
{
    public override string ToString() => $"[{Rule}] {Detail}";
}

public sealed record SceneReport(string Key, string? Race, string Text, IReadOnlyList<SceneIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Level == SceneIssueLevel.Erreur);
    public bool IsClean => Issues.Count == 0;
}

/// <summary>
/// Vérifie des <strong>affirmations</strong>, pas du vocabulaire.
///
/// Un validateur qui contrôlerait chaque mot rejetterait la moitié du français
/// et n'attraperait rien d'intéressant. Celui-ci ne s'occupe que de choses
/// qu'une scène affirme et que le corpus peut démentir :
///
///   1. Une unité citée doit exister. Une unité inventée est un contresens même
///      quand la phrase sonne bien.
///   2. Une unité que la scène donne au joueur (« tes Marines ») doit appartenir
///      à son camp. C'est l'erreur la plus facile à commettre en écrivant un
///      pack de race, et la plus visible pour celui qui joue.
///   3. Une scène de détection doit nommer quelque chose d'invisible, et la
///      réponse qu'elle propose doit être un détecteur. Sinon elle raconte une
///      peur, pas une mécanique.
///   4. Une scène anti-aérienne doit nommer une unité qui vole.
///   5. Aucune formulation périmée par le patch de référence.
///   6. Une scène doit accrocher au moins un fait du corpus. Une image
///      entièrement abstraite n'est pas fausse — elle est simplement
///      interchangeable, et c'est ce qu'on essaie d'éviter.
///
/// Les règles 1 à 5 lèvent des erreurs, la 6 un doute : on refuse de bloquer un
/// pack sur une question de goût.
/// </summary>
public sealed class SceneValidator(Corpus corpus)
{
    private static readonly Regex Possessive =
        new(@"\b(?:tes|ton|ta|mes|mon|ma|nos|notre)\s+([A-Za-zÀ-ÿ][\w'’-]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Une scène qui annonce une menace invisible doit la nommer.</summary>
    private static readonly Regex ThreatTalk =
        new(@"invisible|cloak|enterr", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Une scène qui parle de détection sans menace nommée reste licite si elle reste générale.</summary>
    private static readonly Regex DetectionTalk =
        new(@"détect", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AntiAirTalk =
        new(@"anti[- ]a[ée]rien|dans les airs|tire en l'air", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Ces mots ressemblent à des noms d'unités sans en être. Les citer n'est pas une faute.</summary>
    private static readonly HashSet<string> NotUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "base", "bases", "partie", "parties", "carte", "armée", "armées", "compo", "composition",
        "build", "plan", "prompt", "tâche", "semaine", "attaque", "boucle", "outil", "outils",
        "économie", "production", "banque", "fenêtre", "scout", "harass", "drop", "test", "tests",
        "caméra", "replay", "ouverture", "timing", "engagement", "unité", "unités", "micro", "macro",
    };

    public Corpus Corpus { get; } = corpus;

    public SceneReport Check(string key, string? race, string text)
    {
        var issues = new List<SceneIssue>();
        var mentioned = Mentioned(text).ToList();

        CheckKnownUnits(text, issues);
        CheckOwnSide(text, race, issues);
        CheckDetection(text, mentioned, issues);
        CheckAntiAir(text, mentioned, issues);
        CheckObsolete(text, issues);
        CheckGrounding(text, mentioned, issues);

        return new SceneReport(key, race, text, issues);
    }

    /// <summary>Toutes les unités du corpus effectivement nommées dans la scène.</summary>
    public IEnumerable<CorpusUnit> Mentioned(string text)
        => Corpus.Units.Where(u => u.AllNames().Any(n => Contains(text, n)));

    private static bool Contains(string text, string word)
        => Regex.IsMatch(text, $@"(?<![\w'’-]){Regex.Escape(word)}s?(?![\w'’-])", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------ règle 1

    private void CheckKnownUnits(string text, List<SceneIssue> issues)
    {
        // On ne regarde que ce qui a la forme d'un nom d'unité : un mot capitalisé
        // en milieu de phrase. Chercher plus large ferait un validateur bavard,
        // et un validateur bavard finit désactivé.
        foreach (Match m in Regex.Matches(text, @"(?<![.!?…]\s)(?<!^)\b([A-ZÀ-Þ][a-zà-ÿ]{2,}(?:\s+[A-ZÀ-Þ][a-zà-ÿ]+)?)\b"))
        {
            var candidate = m.Groups[1].Value;
            if (NotUnits.Contains(candidate)) continue;
            if (Corpus.Find(candidate) is not null) continue;
            if (Corpus.FindBuilding(candidate) is not null) continue;
            if (Corpus.Mechanics.Any(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase))) continue;
            if (IsRace(candidate)) continue;
            // Un nom composé dont la première moitié est connue : Fusion Core,
            // Force Fields, Neural Parasite… ce sont des sorts et des bâtiments,
            // pas des unités. On ne les inventorie pas, on ne les reproche pas.
            if (Corpus.Find(candidate.Split(' ')[0]) is not null) continue;
            if (Corpus.FindBuilding(candidate.Split(' ')[0]) is not null) continue;
            // « Vaisseau » ouvre « Vaisseau de guerre » : la majuscule s'arrête au
            // premier mot, le nom continue en minuscules. Reprocher la moitié
            // d'un nom connu serait un faux positif garanti.
            if (Corpus.Named.Any(u => u.AllNames().Any(n =>
                    n.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase)))) continue;

            issues.Add(new SceneIssue(SceneIssueLevel.Doute, "unité inconnue",
                $"« {candidate} » n'est ni dans les unités ni dans les mécaniques du corpus."));
        }
    }

    private static bool IsRace(string word)
        => word is "Zerg" or "Terran" or "Protoss" or "Zergs" or "Terrans" or "Protosses";

    // ------------------------------------------------------------ règle 2

    private void CheckOwnSide(string text, string? race, List<SceneIssue> issues)
    {
        if (string.IsNullOrEmpty(race)) return;

        foreach (Match m in Possessive.Matches(text))
        {
            var word = m.Groups[1].Value;
            var unit = Corpus.Find(word);
            if (unit is null || unit.Race.Length == 0) continue;
            if (string.Equals(unit.Race, race, StringComparison.OrdinalIgnoreCase)) continue;

            issues.Add(new SceneIssue(SceneIssueLevel.Erreur, "mauvais camp",
                $"la scène donne « {m.Value} » au joueur, mais {unit.Name} est une unité {unit.Race} "
                + $"et ce pack est le pack {race}."));
        }
    }

    // ------------------------------------------------------------ règle 3

    private void CheckDetection(string text, List<CorpusUnit> mentioned, List<SceneIssue> issues)
    {
        var menace = mentioned.Any(u => u.Cloaked);
        var reponse = mentioned.Any(u => u.Detector)
                      || Regex.IsMatch(text, @"\bscan\b", RegexOptions.IgnoreCase);

        // Annoncer une menace invisible sans jamais la nommer : la scène décrit
        // une peur au lieu d'une mécanique, et on ne peut rien en apprendre.
        if (ThreatTalk.IsMatch(text) && !menace && !reponse)
        {
            issues.Add(new SceneIssue(SceneIssueLevel.Erreur, "invisible sans nom",
                "la scène parle d'unités invisibles ou enterrées sans nommer ni la menace, ni ce qui la révèle."));
            return;
        }

        // Parler de détection en général est licite. Mais si la scène nomme des
        // unités, l'une d'elles doit être concernée — sinon elle sous-entend
        // qu'une unité détecte alors qu'elle ne détecte pas.
        if (DetectionTalk.IsMatch(text) && mentioned.Count > 0 && !menace && !reponse)
            issues.Add(new SceneIssue(SceneIssueLevel.Erreur, "détection mal attribuée",
                "la scène parle de détection en ne nommant que des unités qui ne détectent rien "
                + $"et que rien ne cache : {string.Join(", ", mentioned.Select(u => u.Name))}."));
    }

    // ------------------------------------------------------------ règle 4

    private void CheckAntiAir(string text, List<CorpusUnit> mentioned, List<SceneIssue> issues)
    {
        if (!AntiAirTalk.IsMatch(text)) return;
        if (mentioned.Any(u => u.Air)) return;

        issues.Add(new SceneIssue(SceneIssueLevel.Erreur, "anti-aérien sans air",
            "la scène parle d'anti-aérien sans nommer une seule unité qui vole."));
    }

    // ------------------------------------------------------------ règle 5

    private void CheckObsolete(string text, List<SceneIssue> issues)
    {
        // Le patch 5.0.16 ramène le départ de douze à huit ouvriers : toute
        // ouverture citée en supply absolu vient d'avant et est fausse.
        foreach (Match m in Regex.Matches(text, @"\b(\d{1,2})\s*(hatch|pool|gate|rax|nexus|ouvriers?|drones?)\b", RegexOptions.IgnoreCase))
            issues.Add(new SceneIssue(SceneIssueLevel.Erreur, "supply d'avant-patch",
                $"« {m.Value} » cite une ouverture en supply absolu ; le patch {Corpus.Patch.Version} "
                + "a ramené le départ à huit ouvriers et périme ces comptes."));
    }

    // ------------------------------------------------------------ règle 6

    private void CheckGrounding(string text, List<CorpusUnit> mentioned, List<SceneIssue> issues)
    {
        if (mentioned.Count > 0) return;
        if (Corpus.Buildings.Any(b => b.AllNames().Any(n => Contains(text, n)))) return;
        if (Corpus.Mechanics.Any(m => Contains(text, m.Name))) return;
        if (Corpus.Vernacular.Any(v => Contains(text, v.Term))) return;

        issues.Add(new SceneIssue(SceneIssueLevel.Doute, "sans ancrage",
            "aucune unité, mécanique ni terme du jeu : la scène pourrait être écrite pour n'importe quel univers."));
    }

    // ------------------------------------------------------- pack entier

    /// <summary>Relit toutes les scènes d'une lentille, camps compris.</summary>
    public List<SceneReport> CheckLens(Lens lens)
    {
        var reports = new List<SceneReport>();

        foreach (var (key, scenes) in lens.Signals)
            foreach (var scene in scenes)
                reports.Add(Check(key, null, scene));

        foreach (var (raceId, race) in lens.Races)
            foreach (var (key, scenes) in race.Signals)
                foreach (var scene in scenes)
                    reports.Add(Check(key, raceId, scene));

        return reports;
    }
}
