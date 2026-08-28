using System.Globalization;
using System.Text;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>Une ligne du rapport de dépense : un utilisateur, un modèle, un produit.</summary>
public sealed class SpendRow
{
    public string Email { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Product { get; set; } = "";
    public string Model { get; set; } = "";
    public long Requests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public decimal NetSpend { get; set; }
    public decimal GrossSpend { get; set; }
    public DateOnly? Date { get; set; }

    public ModelFamily Family => UsageAnalyzer.FamilyOf(Model);
    public long TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// Lecture du rapport de dépense exporté depuis l'espace d'administration
/// (Réglages → Analytics → « Combien Claude nous coûte »). C'est la seule
/// source qui couvre TOUTE l'équipe sur un plan Team : par personne, par
/// produit et par modèle, avec les jetons et le coût estimé.
///
/// Elle a deux limites qu'il faut assumer : l'export est manuel — l'API
/// d'analytics est réservée à Enterprise — et les données ont un jour de
/// retard. En échange, elle ne demande rien à installer sur les postes et voit
/// aussi bien Chat que Claude Code ou Cowork.
///
/// Les en-têtes exacts du CSV ne sont pas contractuels : la correspondance se
/// fait par mots-clés plutôt que par position ou par libellé exact, pour
/// survivre à un renommage de colonne.
/// </summary>
public static class SpendReportReader
{
    private static readonly (string Field, string[] Cues)[] Mapping =
    [
        ("email",       ["email", "user", "utilisateur", "membre"]),
        ("account",     ["account", "uuid", "compte"]),
        ("product",     ["product", "surface", "produit"]),
        ("model",       ["model", "modèle", "modele"]),
        ("requests",    ["request", "requête", "requete", "call", "appel"]),
        ("prompt",      ["prompt", "input", "entrée", "entree"]),
        ("completion",  ["completion", "output", "sortie"]),
        ("net",         ["net"]),
        ("gross",       ["gross", "brut"]),
        ("date",        ["date", "day", "jour"]),
    ];

    public static List<SpendRow> Read(string path, List<string> warnings)
    {
        var rows = new List<SpendRow>();
        var lines = ParseCsv(File.ReadAllText(path));
        if (lines.Count == 0) { warnings.Add("Fichier vide."); return rows; }

        var header = lines[0];
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var c = 0; c < header.Count; c++)
        {
            var norm = header[c].Trim().ToLowerInvariant();
            foreach (var (field, cues) in Mapping)
            {
                if (index.ContainsKey(field)) continue;
                // « model family » ne doit pas préempter « model » : on exige que
                // la colonne la plus spécifique gagne, en testant l'égalité d'abord.
                if (cues.Any(cue => norm == cue) || cues.Any(cue => norm.Contains(cue)))
                { index[field] = c; break; }
            }
        }

        foreach (var required in new[] { "email", "model" })
            if (!index.ContainsKey(required))
                warnings.Add($"Colonne « {required} » introuvable — vérifiez que le fichier est bien l'export de dépense.");
        if (!index.ContainsKey("email") || !index.ContainsKey("model")) return rows;

        for (var i = 1; i < lines.Count; i++)
        {
            var cells = lines[i];
            if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace)) continue;
            string Cell(string field) =>
                index.TryGetValue(field, out var c) && c < cells.Count ? cells[c].Trim() : "";

            rows.Add(new SpendRow
            {
                Email = Cell("email"),
                AccountId = Cell("account"),
                Product = Cell("product"),
                Model = Cell("model"),
                Requests = Long(Cell("requests")),
                PromptTokens = Long(Cell("prompt")),
                CompletionTokens = Long(Cell("completion")),
                NetSpend = Money(Cell("net")),
                GrossSpend = Money(Cell("gross")),
                Date = DateOnly.TryParse(Cell("date"), CultureInfo.InvariantCulture, out var d) ? d : null,
            });
        }
        return rows;
    }

    private static long Long(string s)
        => long.TryParse(s.Replace(" ", "").Replace(",", "").Replace(" ", ""),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static decimal Money(string s)
        => decimal.TryParse(s.Replace("$", "").Replace("€", "").Replace(" ", "").Replace(" ", ""),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    /// <summary>Analyse CSV minimale, suffisante pour un export : guillemets, doublage, retours de ligne.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    rows.Add(row); row = [];
                    break;
                default: cell.Append(ch); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}

public sealed class LearnerSpend
{
    public required string Email { get; init; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long Requests { get; set; }
    public decimal Spend { get; set; }
    public Dictionary<ModelFamily, long> ByFamily { get; } = [];
    public Dictionary<string, long> ByProduct { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long TotalTokens => PromptTokens + CompletionTokens;
    public double ShareOf(ModelFamily f)
        => TotalTokens == 0 ? 0 : (double)(ByFamily.TryGetValue(f, out var n) ? n : 0) / TotalTokens;
}

public static class SpendAggregator
{
    public static List<LearnerSpend> ByLearner(IEnumerable<SpendRow> rows)
    {
        var map = new Dictionary<string, LearnerSpend>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (r.Email.Length == 0) continue;
            if (!map.TryGetValue(r.Email, out var l))
                map[r.Email] = l = new LearnerSpend { Email = r.Email };
            l.PromptTokens += r.PromptTokens;
            l.CompletionTokens += r.CompletionTokens;
            l.Requests += r.Requests;
            l.Spend += r.NetSpend != 0 ? r.NetSpend : r.GrossSpend;
            l.ByFamily[r.Family] = (l.ByFamily.TryGetValue(r.Family, out var f) ? f : 0) + r.TotalTokens;
            var product = r.Product.Length == 0 ? "(inconnu)" : r.Product;
            l.ByProduct[product] = (l.ByProduct.TryGetValue(product, out var p) ? p : 0) + r.TotalTokens;
        }
        return [.. map.Values.OrderByDescending(l => l.TotalTokens)];
    }
}
