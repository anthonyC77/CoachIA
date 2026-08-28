using System.Text.Json;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// La sonde de la phase 00. Elle répond à une seule question : « qu'est-ce que
/// mes transcripts contiennent réellement, sur cette version, sur cette
/// surface ? » — et elle y répond sans jamais recopier une ligne de vos
/// conversations.
///
/// Ce qui sort : des noms de champs, des comptages, des versions, des noms
/// d'outils. Ce qui ne sort jamais : le texte des prompts, le contenu des
/// fichiers, les résultats d'outils. Le rapport est fait pour être partagé.
/// </summary>
public static class TranscriptProbe
{
    public static ProbeReport Run(IEnumerable<string> files, int maxFiles = 40)
    {
        var probe = new ProbeReport();
        var parse = probe.Parse;

        foreach (var file in files.Take(maxFiles))
        {
            var info = new FileInfo(file);
            probe.TotalBytes += info.Exists ? info.Length : 0;

            foreach (var record in TranscriptReader.Read(file, parse))
            {
                var type = record.Type ?? "<sans type>";
                if (!probe.FieldsByType.TryGetValue(type, out var fields))
                    probe.FieldsByType[type] = fields = new Dictionary<string, int>(StringComparer.Ordinal);

                if (record.Raw.ValueKind == JsonValueKind.Object)
                    foreach (var prop in record.Raw.EnumerateObject())
                        fields[prop.Name] = fields.TryGetValue(prop.Name, out var n) ? n + 1 : 0 + 1;

                if (record.Entrypoint is { Length: > 0 } e) parse.Count(e, probe.Entrypoints);

                if (record.Type == "assistant" && record.Message is { ValueKind: JsonValueKind.Object } msg)
                {
                    if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        foreach (var prop in usage.EnumerateObject())
                            parse.Count(prop.Name, probe.UsageFields);

                    if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object) continue;
                            if (block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String)
                                parse.Count(bt.GetString() ?? "?", probe.ContentBlocks);
                            if (block.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                                parse.Count(nm.GetString() ?? "?", probe.Tools);
                        }
                }

                if (record.Type == "user")
                {
                    if (SessionBuilder.IsHumanPrompt(record, out var text))
                    {
                        probe.HumanPrompts++;
                        probe.PromptLengths.Add(text.Length);
                    }
                    else if (record.IsMeta == true) probe.MetaPrompts++;
                }
            }
        }

        probe.Files = parse.FilesRead;
        return probe;
    }
}

public sealed class ProbeReport
{
    public ParseReport Parse { get; } = new();
    public int Files { get; set; }
    public long TotalBytes { get; set; }
    public int HumanPrompts { get; set; }
    public int MetaPrompts { get; set; }
    public List<int> PromptLengths { get; } = [];
    public Dictionary<string, Dictionary<string, int>> FieldsByType { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> UsageFields { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ContentBlocks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Tools { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Entrypoints { get; } = new(StringComparer.Ordinal);

    /// <summary>Les champs dont dépend le parseur. Leur absence est un signal d'alerte, pas un détail.</summary>
    public static readonly (string Type, string Field)[] Critical =
    [
        ("assistant", "message"), ("assistant", "timestamp"), ("assistant", "sessionId"),
        ("user", "message"), ("user", "timestamp"), ("user", "sessionId"),
    ];

    public IEnumerable<string> MissingCritical()
    {
        foreach (var (type, field) in Critical)
        {
            if (!FieldsByType.TryGetValue(type, out var fields)) { yield return $"{type} (type absent)"; continue; }
            if (!fields.ContainsKey(field)) yield return $"{type}.{field}";
        }
    }
}
