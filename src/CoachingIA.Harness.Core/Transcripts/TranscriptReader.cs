using System.Text.Json;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Lecture d'un fichier JSONL, ligne à ligne, sans jamais lever d'exception sur
/// le contenu. Une ligne illisible est comptée et sautée : perdre une ligne
/// coûte un appel d'outil, lever une exception coûte la session entière — et,
/// en lot, toutes les suivantes.
/// </summary>
public static class TranscriptReader
{
    /// <summary>Emplacement par défaut des transcripts, sur les trois systèmes.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static IEnumerable<string> FindTranscripts(string? root = null)
    {
        var dir = root ?? DefaultRoot;
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories);
    }

    public static IEnumerable<TranscriptRecord> Read(string path, ParseReport report)
    {
        report.FilesRead++;
        var lineNumber = 0;

        // Lecture en flux : un transcript de session longue dépasse facilement
        // dix mégaoctets, et le harnais en lit des dizaines d'affilée.
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0) continue;
            report.LinesRead++;

            TranscriptRecord? record = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var parsed = doc.RootElement.Deserialize<TranscriptRecord>(TranscriptRecord.Json);
                if (parsed is not null)
                    record = parsed with { Raw = doc.RootElement.Clone(), LineNumber = lineNumber };
            }
            catch (JsonException)
            {
                report.LinesUnreadable++;
                if (report.Warnings.Count < 20)
                    report.Warnings.Add($"{Path.GetFileName(path)}:{lineNumber} — ligne JSON illisible, ignorée");
            }
            catch (Exception ex)
            {
                report.LinesUnreadable++;
                if (report.Warnings.Count < 20)
                    report.Warnings.Add($"{Path.GetFileName(path)}:{lineNumber} — {ex.GetType().Name}, ignorée");
            }

            if (record is null) continue;

            report.RecordsKept++;
            report.Count(record.Type ?? "<sans type>", report.RecordTypes);
            if (record.Version is { Length: > 0 } v) report.Count(v, report.Versions);
            yield return record;
        }
    }
}
