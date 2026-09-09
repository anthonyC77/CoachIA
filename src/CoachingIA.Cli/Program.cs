using System.Text.Json;
using CoachingIA.Harness.Core;
using CoachingIA.Harness.Core.Coaching;
using CoachingIA.Harness.Core.Transcripts;

// Outil en ligne de commande du harnais. Volontairement sans dépendance
// externe : il doit tourner sur un poste où rien n'est installé, avant même
// que Phoenix ou Docker ne soient en place.
//
//   coachingia probe              rapport de format, anonyme, partageable
//   coachingia analyze            sessions, tâches et signaux, sans rien exporter
//   coachingia segment            la découpe en tâches, avec ses justifications

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var root = Arg("--root") ?? TranscriptReader.DefaultRoot;
var outPath = Arg("--out");
var limit = int.TryParse(Arg("--limit"), out var l) ? l : 40;
var budget = long.TryParse(Arg("--budget"), out var b) ? b : 0;
var weeksBack = int.TryParse(Arg("--weeks"), out var wb) ? wb : 8;
var csvPath = Arg("--csv");
var lensId = Arg("--lens");
var raceId = Arg("--race");
var weekArg = Arg("--week");
var lensDir = Arg("--lenses") ?? DefaultLensDir();

// Le corpus de maturité se monte avant tout calcul : c'est lui qui porte les
// signaux notés, les problématiques et les paliers. Absent ou mal formé, la
// version intégrée prend le relais et le dit — l'outil doit tourner sur un
// poste où rien n'est en place, y compris sans dossier de lentilles.
{
    var corpusWarnings = new List<string>();
    SignalSpecs.Use(MaturityCorpus.Load(lensDir, corpusWarnings));
    foreach (var w in corpusWarnings) Console.Error.WriteLine("⚠ maturité : " + w);
}

static string DefaultLensDir()
{
    // On remonte depuis le binaire jusqu'au dossier « lenses » du dépôt : le
    // CLI doit marcher depuis `dotnet run` comme depuis un binaire publié.
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 6 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "lenses");
        if (Directory.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    return Path.Combine(Environment.CurrentDirectory, "lenses");
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (command == "web")
    return WebConsole.Serve(
        int.TryParse(Arg("--port"), out var p) ? p : 5099,
        Arg("--root"), outPath ?? "bilans", lensDir);
if (command == "corpus") return CorpusCommand.Run(args, lensDir, lensId ?? "starcraft2", raceId);
if (command == "evaluer") return EvaluerCommand.Run(args, lensDir);
if (command == "lens") return Lentille();
if (command == "team") return Team();

var files = TranscriptReader.FindTranscripts(root).ToList();
if (command is not ("help" or "--help" or "-h") && files.Count == 0)
{
    Console.Error.WriteLine($"Aucun transcript trouvé sous {root}");
    Console.Error.WriteLine("Indiquez le dossier avec --root, par exemple :");
    Console.Error.WriteLine(@"  coachingia probe --root ""%USERPROFILE%\.claude\projects""");
    return 2;
}

switch (command)
{
    case "probe": return Probe();
    case "analyze": return Analyze();
    case "segment": return Segment();
    case "usage": return Usage();
    case "defi": return Defi();
    case "moment": return Moment();
    case "bilan": return Bilan();
    case "retro": return Retro();
    default:
        Console.WriteLine("""
            coachingia — lecture des transcripts Claude Code

              probe     ce que contiennent vos transcripts, sans recopier une ligne
              analyze   sessions, tâches et signaux mesurés
              segment   la découpe en tâches et le pourquoi de chaque décision
              usage     jetons par semaine, modèles, types de travail, alertes
              team      sous-utilisation des sièges, depuis l'export de dépense
              lens      les lentilles disponibles, et comment elles parlent
              defi      le défi de la semaine, choisi sur vos propres signaux
              moment    ce que le coach dirait après votre dernière session
              bilan     le bilan de la semaine, en page web : réussite, prompt
                        réécrit, observations, conseils, défi
              retro     la rétrospective des mois écoulés : trajectoire de chaque
                        signal, bascules datées, mois par mois
              web       la console locale : les mêmes commandes, dans une page,
                        sur 127.0.0.1 (--port 5099 par défaut)
              corpus    le fond de connaissance d'une lentille : --valider les
                        scènes, --recolter les chiffres, --generer, --relire
              evaluer   la campagne d'évaluation de l'outil lui-même, comparée à
                        l'état approuvé : 0 rien n'a bougé, 1 un écart, 2 rien
                        mesuré. --juge y ajoute l'avis de claude -p, qui informe
                        sans jamais entrer dans l'état approuvé

            options : --root <dossier>  --out <fichier.json>  --limit <n>
                      --budget <jetons/semaine>  --weeks <n>  --csv <export.csv>
                      --lens <neutre|starcraft2|echecs>  --lenses <dossier>
                      --race <zerg|terran|protoss>  (votre camp, pour que les
                              scènes soient racontées de votre côté de la carte)
                      --variantes  (avec « lens » : relire toutes les scènes)
                      --week <2026-W34>  --juge (réécriture par claude -p)
                      --depuis <2026-03-01>  --jusqua <2026-08-31>  --mois <n>
            """);
        return 0;
}

int Probe()
{
    var report = TranscriptProbe.Run(files, limit);
    var p = report.Parse;

    Console.WriteLine($"""

        Sonde de format — {report.Files} fichier(s), {report.TotalBytes / 1024.0 / 1024.0:F1} Mo
        ────────────────────────────────────────────────────────
        lignes lues        {p.LinesRead,8:N0}
        illisibles         {p.LinesUnreadable,8:N0}   ({p.UnreadableRate:P2})
        prompts humains    {report.HumanPrompts,8:N0}
        tours injectés     {report.MetaPrompts,8:N0}
        """);

    if (report.PromptLengths.Count > 0)
    {
        var sorted = report.PromptLengths.Order().ToList();
        Console.WriteLine($"longueur des prompts : médiane {sorted[sorted.Count / 2]} car., " +
                          $"min {sorted[0]}, max {sorted[^1]}");
    }

    Section("versions de Claude Code", p.Versions);
    Section("surfaces (entrypoint)", report.Entrypoints);
    Section("types de lignes", p.RecordTypes);
    Section("blocs de contenu", report.ContentBlocks);
    Section("champs du bloc usage", report.UsageFields);
    Section("outils appelés", report.Tools, 15);

    Console.WriteLine("\nchamps rencontrés, par type de ligne");
    foreach (var (type, fields) in report.FieldsByType.OrderByDescending(kv => kv.Value.Values.Sum()))
        Console.WriteLine($"  {type,-16} {string.Join(", ", fields.OrderByDescending(f => f.Value).Select(f => f.Key))}");

    var missing = report.MissingCritical().ToList();
    Console.WriteLine();
    if (missing.Count > 0)
    {
        Console.WriteLine("⚠ champs critiques absents : " + string.Join(", ", missing));
        Console.WriteLine("  Le parseur ne pourra pas reconstruire les sessions en l'état.");
    }
    else Console.WriteLine("✓ tous les champs dont dépend le parseur sont présents");

    if (p.LooksBroken)
        Console.WriteLine($"⚠ {p.UnreadableRate:P1} de lignes illisibles : le format a probablement changé");

    foreach (var w in p.Warnings.Take(5)) Console.WriteLine("  · " + w);

    if (outPath is not null)
    {
        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            report.Files, report.TotalBytes, report.HumanPrompts, report.MetaPrompts,
            p.LinesRead, p.LinesUnreadable, p.RecordTypes, p.Versions,
            report.Entrypoints, report.ContentBlocks, report.UsageFields, report.Tools,
            report.FieldsByType, missing,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nrapport écrit dans {outPath}");
    }
    return missing.Count > 0 ? 1 : 0;
}

int Analyze()
{
    var (sessions, parse) = Load();
    var segmenter = new TaskSegmenter();
    var extractor = new SignalExtractor();

    Console.WriteLine($"\n{sessions.Count} session(s) relue(s), {parse.LinesUnreadable} ligne(s) illisible(s)\n");

    var allTasks = 0;
    var aggregate = new Dictionary<string, List<double>>(StringComparer.Ordinal);

    foreach (var session in sessions)
    {
        var tasks = segmenter.Segment(session);
        allTasks += tasks.Count;
        Console.WriteLine($"── {session.SessionId[..Math.Min(8, session.SessionId.Length)]} " +
                          $"· {session.StartedAt:yyyy-MM-dd HH:mm} · {session.Duration.TotalMinutes:F0} min " +
                          $"· {session.Turns.Count} tours · {session.ToolCallCount} outils · {tasks.Count} tâche(s)");
        foreach (var task in tasks)
        {
            Console.WriteLine($"   • {task.Title}");
            foreach (var s in extractor.ForTask(task, session))
            {
                if (double.IsNaN(s.Value)) continue;   // signal indéterminé : hors moyenne
                if (!aggregate.TryGetValue(s.Key, out var list)) aggregate[s.Key] = list = [];
                list.Add(s.Value);
            }
        }
    }

    Console.WriteLine($"\n{allTasks} tâche(s) au total. Moyennes par signal :\n");
    foreach (var (key, values) in aggregate.OrderBy(kv => kv.Key))
        Console.WriteLine($"  {key,-24} {values.Average(),8:F2}   (sur {values.Count} tâches)");
    return 0;
}

int Segment()
{
    var (sessions, _) = Load();
    var segmenter = new TaskSegmenter();
    var extractor = new SignalExtractor();

    foreach (var session in sessions)
    {
        var tasks = segmenter.Segment(session);
        Console.WriteLine($"\n══ session {session.SessionId[..Math.Min(8, session.SessionId.Length)]} — {tasks.Count} tâche(s)");
        foreach (var task in tasks)
        {
            Console.WriteLine($"\n  ▸ {task.Title}");
            Console.WriteLine($"    {task.Turns.Count} tour(s) · {task.ToolCalls} outils · " +
                              $"{task.ActiveDuration.TotalMinutes:F0} min actives " +
                              $"(sur {task.WallDuration.TotalMinutes:F0} min au mur)" +
                              (task.HasTaskEvents ? " · décomposée" : "") +
                              (task.InProgress ? " · en cours" : ""));
            foreach (var d in task.Decisions) Console.WriteLine($"      – {d}");
            foreach (var s in extractor.ForTask(task, session).Where(s => s.Level <= 2).Take(5))
                Console.WriteLine($"      · {s.Key} = {(double.IsNaN(s.Value) ? "  n/d" : s.Value.ToString("F2"))} — {s.Evidence}");
        }
    }
    return 0;
}


int Usage()
{
    var (sessions, _) = Load();
    var analyzer = new UsageAnalyzer { WeeklyTokenBudget = budget };
    var weeks = analyzer.ByWeek(sessions).TakeLast(weeksBack).ToList();
    if (weeks.Count == 0) { Console.WriteLine("Aucune activité trouvée."); return 0; }

    Console.WriteLine($"""

        Consommation hebdomadaire — {weeks.Count} semaine(s)
        {(budget > 0 ? $"enveloppe de référence : {budget:N0} jetons lus / semaine" : "aucune enveloppe de référence (--budget) : lecture en tendance")}
        """);

    Console.WriteLine($"\n  {"semaine",-10} {"jetons lus",13} {"part",7} {"sortie",10} {"cache",7} {"jours",6} {"tâches",7} {"sessions",9}");
    Console.WriteLine("  " + new string('─', 76));
    foreach (var w in weeks)
    {
        var share = budget > 0 ? ((double)w.TotalRead / budget).ToString("P0") : "—";
        Console.WriteLine($"  {w.Week,-10} {w.TotalRead,13:N0} {share,7} {w.TotalOutput,10:N0} " +
                          $"{w.CacheRatio,7:P0} {w.ActiveDays.Count,6} {w.Tasks,7} {w.Sessions.Count,9}");
    }

    var lastWeek = weeks[^1];
    Console.WriteLine($"\n  Modèles — {lastWeek.Week}");
    foreach (var m in lastWeek.Models.Values.OrderByDescending(m => m.TotalRead))
        Console.WriteLine($"    {m.Name,-24} {m.TotalRead,13:N0} jetons  {(lastWeek.TotalRead == 0 ? 0 : (double)m.TotalRead / lastWeek.TotalRead),6:P0}  " +
                          $"{m.Calls,5} appels");

    Console.WriteLine($"\n  Par famille (jetons / appels)");
    foreach (var f in new[] { ModelFamily.Haiku, ModelFamily.Sonnet, ModelFamily.Opus, ModelFamily.Autre })
    {
        var share = lastWeek.ShareOf(f);
        var callShare = lastWeek.CallShareOf(f);
        if (share == 0 && callShare == 0) continue;
        Console.WriteLine($"    {f,-8} {Bar(share)} {share,6:P0} des jetons · {callShare,6:P0} des appels");
    }

    Console.WriteLine($"\n  Types de travail — {lastWeek.Week}");
    var totalWork = lastWeek.Work.Values.Sum();
    foreach (var (kind, n) in lastWeek.Work.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"    {kind,-14} {Bar(totalWork == 0 ? 0 : (double)n / totalWork)} {n,3} tâche(s)");

    var alerts = analyzer.Alerts(weeks);
    Console.WriteLine();
    if (alerts.Count == 0) Console.WriteLine("  Aucune alerte.");
    foreach (var a in alerts)
        Console.WriteLine($"  {(a.Severity == "attention" ? "⚠" : "·")} {a.Title}\n      {a.Detail}");

    if (outPath is not null)
    {
        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            budget,
            weeks = weeks.Select(w => new
            {
                w.Week, monday = w.MondayOf.ToString("yyyy-MM-dd"),
                tokensRead = w.TotalRead, tokensOut = w.TotalOutput,
                cacheRatio = Math.Round(w.CacheRatio, 3),
                activeDays = w.ActiveDays.Count, sessions = w.Sessions.Count,
                w.Tasks, w.Turns, w.ToolCalls,
                activeMinutes = Math.Round(w.ActiveTime.TotalMinutes),
                models = w.Models.Values.Select(m => new { m.Name, family = m.Family.ToString(), m.Calls, tokens = m.TotalRead }),
                work = w.Work.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            }),
            alerts,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n  carte hebdomadaire écrite dans {outPath}");
        Console.WriteLine("  (aucun texte de prompt : elle se partage telle quelle)");
    }
    return 0;
}

int Team()
{
    if (csvPath is null || !File.Exists(csvPath))
    {
        Console.Error.WriteLine("""
            Indiquez l'export de dépense de votre organisation :
              coachingia team --csv rapport.csv --budget 5000000 --weeks 4

            Il s'obtient dans Réglages → Analytics → « Combien Claude nous coûte » → Exporter.
            Réservé aux Owners du plan Team. L'API d'analytics, elle, est Enterprise seulement.
            """);
        return 2;
    }

    var warnings = new List<string>();
    var rows = SpendReportReader.Read(csvPath, warnings);
    foreach (var w in warnings) Console.Error.WriteLine("⚠ " + w);
    if (rows.Count == 0) return 1;

    var learners = SpendAggregator.ByLearner(rows);
    var reference = budget > 0 ? budget * weeksBack : 0;

    Console.WriteLine($"""

        Utilisation des sièges — {learners.Count} personne(s), {rows.Count} ligne(s)
        {(reference > 0 ? $"référence : {budget:N0} jetons/semaine × {weeksBack} semaines = {reference:N0}" : "aucune référence (--budget) : classement par volume")}
        """);

    Console.WriteLine($"\n  {"personne",-30} {"jetons",13} {"part",7} {"dépense",11}  répartition modèle");
    Console.WriteLine("  " + new string('─', 92));
    foreach (var l in learners)
    {
        var share = reference > 0 ? ((double)l.TotalTokens / reference) : double.NaN;
        var mix = string.Join(" ", new[] { ModelFamily.Opus, ModelFamily.Sonnet, ModelFamily.Haiku }
            .Where(f => l.ShareOf(f) > 0.01)
            .Select(f => $"{f.ToString()[..1]} {l.ShareOf(f):P0}"));
        // InvariantGlobalization : pas de symbole monétaire fiable, on l'écrit.
        Console.WriteLine($"  {Trim(l.Email, 30),-30} {l.TotalTokens,13:N0} " +
                          $"{(double.IsNaN(share) ? "—" : share.ToString("P0")),7} {l.Spend,9:N2} $  {mix}");
    }

    if (reference > 0)
    {
        var under = learners.Where(l => (double)l.TotalTokens / reference < 0.5).ToList();
        var idle = learners.Where(l => l.TotalTokens == 0).ToList();
        Console.WriteLine();
        if (idle.Count > 0)
            Console.WriteLine($"  ⚠ {idle.Count} siège(s) sans aucun usage : {string.Join(", ", idle.Select(l => l.Email))}");
        if (under.Count > 0)
            Console.WriteLine($"""
                  ⚠ {under.Count} personne(s) sous 50 % de l'enveloppe sur la période.
                      {string.Join(", ", under.Take(8).Select(l => Trim(l.Email, 28)))}
                      La capacité non consommée ne se reporte pas. Avant de conclure à un
                      siège de trop, regardez si ce sont les mêmes qui posent peu de questions
                      ou ceux qui n'ont jamais été accompagnés.
                """);
        if (under.Count == 0 && idle.Count == 0)
            Console.WriteLine("  Aucun siège manifestement sous-utilisé sur la période.");
    }

    if (outPath is not null)
    {
        File.WriteAllText(outPath, JsonSerializer.Serialize(learners.Select(l => new
        {
            l.Email, l.TotalTokens, l.Requests, spend = l.Spend,
            utilisation = reference > 0 ? Math.Round((double)l.TotalTokens / reference, 3) : (double?)null,
            families = l.ByFamily.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            products = l.ByProduct,
        }), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n  rapport écrit dans {outPath}");
    }
    return 0;
}

static string Bar(double ratio)
{
    var filled = (int)Math.Round(Math.Clamp(ratio, 0, 1) * 18);
    return "[" + new string('#', filled) + new string('·', 18 - filled) + "]";
}

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";



int Bilan()
{
    var (sessions, _) = Load();
    var writer = Writer();

    // Par défaut la critique du prompt tourne hors ligne. --juge la confie à
    // Claude, qui rédige vraiment la réécriture — au prix d'un appel et de
    // quelques secondes.
    IPromptCritic critic = new HeuristicPromptCritic();
    ClaudePromptCritic? judged = null;
    if (Array.IndexOf(args, "--juge") >= 0)
        critic = judged = new ClaudePromptCritic(new HeuristicPromptCritic());

    var review = new WeeklyReviewBuilder { Critic = critic }.Build(sessions, weekArg, writer);
    if (judged?.LastError is { } err)
        Console.Error.WriteLine($"⚠ le juge n'a pas répondu ({err}) — critique hors ligne conservée.");

    // Une semaine vide n'a pas de bilan à écrire. Sans ce garde-fou on produit
    // un fichier nommé « —.html » qui ne dit rien et encombre le dossier.
    if (review.IsEmpty)
    {
        Console.Error.WriteLine(weekArg is null
            ? "  Aucune tâche sur la dernière semaine close — rien à bilanter."
            : $"  Aucune tâche sur {weekArg} — rien à bilanter.");
        Console.Error.WriteLine("  Vérifiez --root, ou visez une autre semaine avec --week 2026-W34.");
        return 1;
    }

    var dossier = outPath ?? "bilans";
    var page = ReviewArchive.Write(dossier, $"{review.Week}.html", HtmlReviewRenderer.Render(review, writer));
    var texte = ReviewArchive.Write(dossier, $"{review.Week}.md", ReviewRenderer.ToMarkdown(review, writer));

    Console.WriteLine($"\n  Bilan {review.Week}");
    Console.WriteLine($"    page   {Path.GetFullPath(page.Path)}   {Etat(page)}");
    Console.WriteLine($"    texte  {Path.GetFullPath(texte.Path)}   {Etat(texte)}");

    if (page.ArchivedTo is not null)
        Console.WriteLine($"    version précédente conservée dans {Path.GetFileName(page.ArchivedTo)}");

    var histoire = ReviewArchive.History(dossier, review.Week).ToList();
    if (histoire.Count > 0)
        Console.WriteLine($"    {histoire.Count} version(s) antérieure(s) de cette semaine sous {ReviewArchive.ArchiveFolder}/");

    if (review.PromptOfTheWeek is { } p)
        Console.WriteLine($"    prompt de la semaine critiqué ({p.Source}), {p.Missing.Count} critère(s) manquant(s)");

    Console.WriteLine(page.Outcome == WriteOutcome.Unchanged
        ? "\n  Rien n'a changé depuis la dernière génération."
        : "\n  Ouvrez la page dans votre navigateur : c'est là que la comparaison se lit.");
    return 0;

    static string Etat(WriteResult r) => r.Outcome switch
    {
        WriteOutcome.Created => "(nouveau)",
        WriteOutcome.Unchanged => "(inchangé)",
        _ => "(mis à jour, ancien archivé)",
    };
}

int Retro()
{
    var (sessions, _) = Load();
    var writer = Writer();

    var since = ParseDay(Arg("--depuis"));
    var until = ParseDay(Arg("--jusqua"));
    if (since is null && int.TryParse(Arg("--mois"), out var m) && m > 0)
        since = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddMonths(-m);

    var retro = new RetrospectiveBuilder().Build(sessions, writer, since, until);

    if (retro.IsEmpty)
    {
        Console.Error.WriteLine("  Aucune tâche sur la période demandée — rien à rejouer.");
        return 1;
    }

    var dossier = outPath ?? "bilans";
    var nom = $"retrospective-{retro.From:yyyy-MM-dd}-{retro.To:yyyy-MM-dd}";
    var page = ReviewArchive.Write(dossier, nom + ".html", HtmlRetrospectiveRenderer.Render(retro, writer));

    Console.WriteLine($"\n  Rétrospective du {retro.From:dd/MM/yyyy} au {retro.To:dd/MM/yyyy}");
    Console.WriteLine($"    {retro.WeeksActive} semaines travaillées sur {retro.WeeksCovered}"
        + (retro.WeeksSilent > 0 ? $", {retro.WeeksSilent} sans trace" : "")
        + $" · {retro.Tasks} tâches · {retro.Sessions} sessions");

    foreach (var (verdict, titre) in new[]
    {
        (TrendVerdict.Acquis, "acquis"),
        (TrendVerdict.EnRecul, "en recul"),
        (TrendVerdict.EnProgres, "en progrès"),
        (TrendVerdict.Stable, "sans mouvement"),
    })
    {
        var trails = retro.With(verdict).ToList();
        if (trails.Count == 0) continue;
        Console.WriteLine($"\n    {titre} : {string.Join(", ", trails.Select(t => t.Key))}");
    }

    if (retro.Milestones.Count > 0)
    {
        Console.WriteLine("\n    bascules :");
        foreach (var b in retro.Milestones)
            Console.WriteLine($"      {b.On:dd/MM/yyyy}  {b.Statement}");
    }

    Console.WriteLine($"\n    page  {Path.GetFullPath(page.Path)}");
    if (page.ArchivedTo is not null)
        Console.WriteLine($"    version précédente conservée dans {Path.GetFileName(page.ArchivedTo)}");
    return 0;

    static DateOnly? ParseDay(string? text)
        => DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}

LensWriter Writer()
{
    var warnings = new List<string>();
    var catalog = LensCatalog.Load(lensDir, warnings);
    foreach (var w in warnings) Console.Error.WriteLine("⚠ lentille : " + w);
    if (lensId is not null && !catalog.Knows(lensId))
        Console.Error.WriteLine($"⚠ lentille « {lensId} » inconnue — retour au neutre.");

    var lens = catalog.Resolve(lensId);
    if (raceId is not null && lens.Race(raceId) is null)
    {
        var camps = lens.Races.Count == 0 ? "aucun" : string.Join(", ", lens.Races.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Console.Error.WriteLine($"⚠ camp « {raceId} » inconnu pour cette lentille (disponibles : {camps}) — vocabulaire générique.");
    }
    return new LensWriter(lens, raceId);
}

int Lentille()
{
    var warnings = new List<string>();
    var catalog = LensCatalog.Load(lensDir, warnings);
    foreach (var w in warnings) Console.Error.WriteLine("⚠ " + w);

    if (lensId is null)
    {
        Console.WriteLine($"""

            Lentilles disponibles — dossier {lensDir}

            Une lentille ne change que le vocabulaire. Les mesures, les seuils et
            les conseils sont identiques d'une lentille à l'autre : seuls les mots
            pour les dire changent. Aucun point, aucun rang, aucune ligue.
            """);
        foreach (var lens in catalog.All.OrderBy(x => x.Id))
            Console.WriteLine($"\n  {lens.Id,-12} {lens.Name}\n               {lens.Tagline}");
            foreach (var lens in catalog.All.Where(x => x.Races.Count > 0).OrderBy(x => x.Id))
            Console.WriteLine($"\n  {lens.Id} se joue aussi par camp : --race {string.Join(" | --race ", lens.Races.Keys.OrderBy(x => x, StringComparer.Ordinal))}");
        Console.WriteLine("\n  Pour en voir une à l'œuvre :  coachingia lens --lens starcraft2 --race zerg");
        return 0;
    }

    var writer = Writer();
    var side = writer.Side;
    Console.WriteLine($"\n  {writer.Lens.Name} — {writer.Lens.Tagline}");
    if (writer.Lens.Patch.Length > 0)
        Console.WriteLine($"  Vocabulaire calé sur le patch {writer.Lens.Patch}.");
    if (side is not null)
    {
        Console.WriteLine($"  Camp : {(side.Name.Length > 0 ? side.Name : writer.SideId)}"
            + (side.Nemesis.Length > 0 ? $" · adversaire de prédilection dans les scènes : {side.Nemesis}" : ""));
        if (side.Tagline.Length > 0) Console.WriteLine($"  {side.Tagline}");
    }
    Console.WriteLine();
    for (var level = 1; level <= 5; level++)
    {
        var l = writer.ForLevel(level);
        Console.WriteLine($"  Palier {level} · {l.Term}");
        foreach (var line in Wrap(l.Analogy, 76)) Console.WriteLine("    " + line);
        if (l.Pitfall.Length > 0)
        {
            Console.WriteLine("    La faute classique :");
            foreach (var line in Wrap(l.Pitfall, 74)) Console.WriteLine("      " + line);
        }
        Console.WriteLine();
    }

    // La relecture du corpus : c'est là qu'on corrige un contresens sur une
    // unité, et c'est le seul geste qui garde le pack juste avec le temps.
    if (Array.IndexOf(args, "--variantes") >= 0)
    {
        Console.WriteLine("  Les scènes, problématique par problématique — la première est celle de cette semaine.\n");
        var cycle = writer.Cycle;
        var corpus = SignalSpecs.Corpus;

        // On parcourt tout ce qui peut porter une scène, pas seulement les
        // signaux notés : c'est la commande de relecture, et une scène qu'elle
        // n'affiche pas est une scène que personne ne corrigera jamais. C'est
        // très exactement ce qui était arrivé à graph_depth.
        foreach (var probleme in corpus.Problems.OrderBy(p => p.Level))
        {
            Console.WriteLine($"  ── palier {probleme.Level} · {probleme.Title}");
            Ecrire(probleme.Id, "problématique");
            foreach (var cle in probleme.Signals)
                Ecrire(cle, corpus.Signal(cle)?.IsGraded == true ? "noté" : "sans cible");
            Console.WriteLine();
        }
        Console.WriteLine($"  Cycle {cycle}. La semaine prochaine, chaque signal descend d'un cran.");

        void Ecrire(string cle, string genre)
        {
            var scenes = writer.VariantsFor(cle);
            if (scenes.Count == 0) return;

            var courante = writer.ForSignal(cle, "").Flourish;
            Console.WriteLine($"     {cle}  ({genre}, {scenes.Count} scène(s))");
            foreach (var scene in scenes)
            {
                var marque = scene == courante ? "→" : " ";
                foreach (var (line, i) in Wrap(scene, 70).Select((l, i) => (l, i)))
                    Console.WriteLine(i == 0 ? $"       {marque} {line}" : $"         {line}");
            }
        }
        return 0;
    }

    Console.WriteLine("  Un défi, dans cette lentille :");
    var sample = new ChallengeLibrary().Pick(
        new Dictionary<string, double> { ["has_acceptance_criteria"] = 0.1 }, writer);
    if (sample is not null) foreach (var line in Wrap(sample.Render(), 74)) Console.WriteLine("    " + line);
    return 0;
}

int Defi()
{
    var (sessions, _) = Load();
    var writer = Writer();
    var segmenter = new TaskSegmenter();
    var extractor = new SignalExtractor();

    var averages = new Dictionary<string, List<double>>(StringComparer.Ordinal);
    var taskCount = 0;
    foreach (var session in sessions)
        foreach (var task in segmenter.Segment(session))
        {
            taskCount++;
            foreach (var s in extractor.ForTask(task, session))
            {
                if (double.IsNaN(s.Value)) continue;
                if (!averages.TryGetValue(s.Key, out var list)) averages[s.Key] = list = [];
                list.Add(s.Value);
            }
        }

    if (taskCount == 0) { Console.WriteLine("Aucune tâche trouvée."); return 0; }
    var means = averages.ToDictionary(kv => kv.Key, kv => kv.Value.Average(), StringComparer.Ordinal);

    var challenge = new ChallengeLibrary().Pick(means, writer);
    Console.WriteLine($"\n  Sur {taskCount} tâche(s) · lentille {writer.Lens.Name}\n");
    if (challenge is null)
    {
        Console.WriteLine("  Aucun défi à proposer : tous les signaux mesurables sont au-dessus de leur cible.");
        return 0;
    }

    Console.WriteLine($"  Palier {challenge.Level} · signal {challenge.SignalKey}\n");
    Console.WriteLine("  Le défi de la semaine");
    foreach (var line in Wrap(challenge.Render(), 74)) Console.WriteLine("    " + line);
    Console.WriteLine("\n  Pourquoi celui-là");
    foreach (var line in Wrap(challenge.Why, 74)) Console.WriteLine("    " + line);
    Console.WriteLine("\n  Comment on saura");
    Console.WriteLine($"    {challenge.Verification}");
    return 0;
}

int Moment()
{
    var (sessions, _) = Load();
    var writer = Writer();
    var segmenter = new TaskSegmenter();
    var extractor = new SignalExtractor();
    var detector = new MomentDetector();

    var found = 0;
    foreach (var session in sessions)
        foreach (var task in segmenter.Segment(session))
        {
            if (task.InProgress) continue;
            var message = detector.Detect(task, extractor.ForTask(task, session), writer);
            if (message is null) continue;
            found++;
            Console.WriteLine($"\n  {task.StartedAt:yyyy-MM-dd}");
            foreach (var line in Wrap(message.Value.ToString(), 74)) Console.WriteLine("    " + line);
        }

    Console.WriteLine(found == 0
        ? "\n  Aucune session notable : le coach serait resté silencieux."
        : $"\n  {found} observation(s). En vrai, le coach n'en garderait qu'une par jour.");
    return 0;
}

static IEnumerable<string> Wrap(string text, int width)
{
    var line = new System.Text.StringBuilder();
    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.Length > 0 && line.Length + word.Length + 1 > width) { yield return line.ToString(); line.Clear(); }
        if (line.Length > 0) line.Append(' ');
        line.Append(word);
    }
    if (line.Length > 0) yield return line.ToString();
}

void Section(string title, Dictionary<string, int> data, int take = 10)
{
    if (data.Count == 0) return;
    Console.WriteLine($"\n{title}");
    foreach (var (k, v) in data.OrderByDescending(kv => kv.Value).Take(take))
        Console.WriteLine($"  {k,-34} {v,6:N0}");
}

(List<TranscriptSession>, ParseReport) Load()
{
    var parse = new ParseReport();
    var records = files.Take(limit).SelectMany(f => TranscriptReader.Read(f, parse));
    return (SessionBuilder.Build(records), parse);
}
