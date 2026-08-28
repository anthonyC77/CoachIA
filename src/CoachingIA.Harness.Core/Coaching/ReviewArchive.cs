using System.Security.Cryptography;
using System.Text;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>Ce qu'un enregistrement a fait du fichier précédent.</summary>
public enum WriteOutcome { Created, Unchanged, Archived }

public sealed record WriteResult(string Path, WriteOutcome Outcome, string? ArchivedTo);

/// <summary>
/// Écrit un bilan sans jamais perdre le précédent.
///
/// Le nom canonique — <c>2026-W34.html</c> — reste stable : c'est celui qu'on
/// met en favori et que les scripts cherchent. Quand un nouveau bilan diffère
/// de l'ancien, l'ancien part dans <c>archives/</c> horodaté à sa propre date de
/// modification, et non à celle du jour : on y lit « la version du 26 août »,
/// pas « la version archivée aujourd'hui ».
///
/// Un bilan régénéré à l'identique ne crée rien. Relancer trois fois la même
/// commande ne doit pas produire trois copies — ce serait perdre l'historique
/// dans le bruit plutôt que dans l'écrasement.
/// </summary>
public static class ReviewArchive
{
    public const string ArchiveFolder = "archives";

    public static WriteResult Write(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
            return new WriteResult(path, WriteOutcome.Created, null);
        }

        if (SameContent(path, content))
            return new WriteResult(path, WriteOutcome.Unchanged, null);

        var archived = Rotate(directory, path, fileName);
        File.WriteAllText(path, content);
        return new WriteResult(path, WriteOutcome.Archived, archived);
    }

    private static bool SameContent(string path, string content)
    {
        try
        {
            return Hash(File.ReadAllText(path)) == Hash(content);
        }
        catch (IOException)
        {
            // Illisible pour une raison quelconque : on préfère archiver à tort
            // que risquer d'écraser quelque chose qu'on n'a pas pu comparer.
            return false;
        }
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"))));

    private static string Rotate(string directory, string path, string fileName)
    {
        var archiveDir = Path.Combine(directory, ArchiveFolder);
        Directory.CreateDirectory(archiveDir);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var stamp = File.GetLastWriteTime(path).ToString("yyyyMMdd-HHmm");

        var target = Path.Combine(archiveDir, $"{stem}-{stamp}{extension}");
        // Deux rotations dans la même minute : on suffixe plutôt que d'écraser
        // l'archive, sans quoi la précaution se retournerait contre elle-même.
        var counter = 2;
        while (File.Exists(target))
            target = Path.Combine(archiveDir, $"{stem}-{stamp}-{counter++}{extension}");

        File.Move(path, target);
        return target;
    }

    /// <summary>Les versions archivées d'un bilan, de la plus récente à la plus ancienne.</summary>
    public static IEnumerable<string> History(string directory, string week, string extension = ".html")
    {
        var archiveDir = Path.Combine(directory, ArchiveFolder);
        if (!Directory.Exists(archiveDir)) return [];

        // On trie sur l'horodatage lu dans le nom, pas sur le nom lui-même :
        // « …-0930-2.html » précède « …-0930.html » en ordre lexical alors qu'il
        // est plus récent, et l'historique se lirait à l'envers.
        return Directory.EnumerateFiles(archiveDir, $"{week}-*{extension}")
            .Select(f => (Path: f, Key: KeyOf(Path.GetFileNameWithoutExtension(f), week)))
            .OrderByDescending(x => x.Key.When)
            .ThenByDescending(x => x.Key.Rank)
            .Select(x => x.Path)
            .ToList();
    }

    /// <summary>Relit « 2026-W34-20260820-0930-2 » comme une date et un rang.</summary>
    private static (DateTime When, int Rank) KeyOf(string stem, string week)
    {
        var rest = stem.Length > week.Length + 1 && stem.StartsWith(week, StringComparison.Ordinal)
            ? stem[(week.Length + 1)..] : stem;

        var parts = rest.Split('-');
        var rank = 1;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var n)) rank = n;

        return parts.Length >= 2 && DateTime.TryParseExact(
                   parts[0] + "-" + parts[1], "yyyyMMdd-HHmm", null,
                   System.Globalization.DateTimeStyles.None, out var when)
            ? (when, rank)
            : (DateTime.MinValue, rank);   // nom inattendu : relégué en fin d'historique
    }
}
