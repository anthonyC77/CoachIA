using CoachingIA.Harness.Core.Coaching;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Banc d'essai de l'archivage. La promesse tenue ici est simple : générer un
/// bilan ne doit jamais faire disparaître le précédent, et régénérer le même
/// bilan à l'identique ne doit pas non plus noyer l'historique sous des copies.
/// </summary>
public static class ArchiveTests
{
    public static void Run(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "coachingia-archive-" + Guid.NewGuid().ToString("n"));
        try
        {
            Console.WriteLine("Premier enregistrement");
            var first = ReviewArchive.Write(dir, "2026-W34.html", "<p>version une</p>");
            check(first.Outcome == WriteOutcome.Created, "un bilan qui n'existait pas est simplement créé");
            check(first.ArchivedTo is null, "et rien n'est archivé puisqu'il n'y avait rien");
            check(File.ReadAllText(first.Path) == "<p>version une</p>", "le contenu écrit est celui demandé");
            check(!Directory.Exists(Path.Combine(dir, ReviewArchive.ArchiveFolder)),
                  "aucun dossier d'archives n'est créé tant qu'il n'y a rien à archiver");

            Console.WriteLine("\nRégénération à l'identique");
            var again = ReviewArchive.Write(dir, "2026-W34.html", "<p>version une</p>");
            check(again.Outcome == WriteOutcome.Unchanged, "un bilan identique ne produit pas de nouvelle version");
            check(!ReviewArchive.History(dir, "2026-W34").Any(), "et n'encombre pas l'historique");

            Console.WriteLine("\nBilan modifié");
            // On vieillit le fichier pour vérifier que l'archive porte SA date
            // à lui, pas celle du jour : on doit y lire « la version du 20 août ».
            var stamped = new DateTime(2026, 8, 20, 9, 30, 0);
            File.SetLastWriteTime(first.Path, stamped);
            var second = ReviewArchive.Write(dir, "2026-W34.html", "<p>version deux</p>");
            check(second.Outcome == WriteOutcome.Archived, "un contenu différent déclenche une rotation");
            check(File.ReadAllText(second.Path) == "<p>version deux</p>",
                  "le nom canonique porte la version la plus récente");
            check(second.ArchivedTo is not null && File.Exists(second.ArchivedTo),
                  "l'ancienne version existe toujours sur le disque");
            check(File.ReadAllText(second.ArchivedTo!) == "<p>version une</p>",
                  "et c'est bien l'ancien contenu qui a été conservé");
            check(Path.GetFileName(second.ArchivedTo!) == "2026-W34-20260820-0930.html",
                  $"l'archive est horodatée à la date du fichier remplacé ({Path.GetFileName(second.ArchivedTo!)})");

            Console.WriteLine("\nDeux rotations dans la même minute");
            File.SetLastWriteTime(second.Path, stamped);
            var third = ReviewArchive.Write(dir, "2026-W34.html", "<p>version trois</p>");
            check(Path.GetFileName(third.ArchivedTo!) == "2026-W34-20260820-0930-2.html",
                  $"la collision est suffixée plutôt qu'écrasée ({Path.GetFileName(third.ArchivedTo!)})");
            check(File.ReadAllText(second.ArchivedTo!) == "<p>version une</p>",
                  "la première archive est intacte après la seconde rotation");

            Console.WriteLine("\nHistorique");
            var history = ReviewArchive.History(dir, "2026-W34").ToList();
            check(history.Count == 2, $"les deux versions précédentes sont retrouvées (obtenu {history.Count})");
            check(Path.GetFileName(history[0]) == "2026-W34-20260820-0930-2.html",
                  "la plus récente vient en tête");
            check(!ReviewArchive.History(dir, "2026-W33").Any(),
                  "l'historique d'une autre semaine ne déborde pas sur celle-ci");

            Console.WriteLine("\nSemaines et formats séparés");
            ReviewArchive.Write(dir, "2026-W34.md", "texte une");
            var md = ReviewArchive.Write(dir, "2026-W34.md", "texte deux");
            check(md.Outcome == WriteOutcome.Archived, "le markdown est archivé comme la page");
            check(ReviewArchive.History(dir, "2026-W34", ".md").Count() == 1,
                  "et son historique est distinct de celui du HTML");
            check(ReviewArchive.History(dir, "2026-W34").Count() == 2,
                  "l'historique HTML n'a pas bougé en écrivant le markdown");

            Console.WriteLine("\nFins de ligne");
            // Un même bilan relu sous Windows ne doit pas passer pour différent
            // au seul motif que les retours à la ligne ont changé de forme.
            var crlf = Path.Combine(dir, "2026-W35.html");
            File.WriteAllText(crlf, "une\r\ndeux\r\n");
            var eol = ReviewArchive.Write(dir, "2026-W35.html", "une\ndeux\n");
            check(eol.Outcome == WriteOutcome.Unchanged,
                  "CRLF et LF ne comptent pas pour une différence de contenu");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
