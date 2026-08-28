using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoachingIA.Harness.Core.Coaching;

public sealed record HarvestResult(int Filled, int Missed, int Failed);

/// <summary>
/// Complète les chiffres du corpus depuis Liquipedia.
///
/// Ce que la récolte apporte, et ce qu'elle n'apporte pas : elle apporte des
/// coûts, des portées, des temps de production — des faits que je retiens mal
/// et qui bougent aux patchs. Elle n'apporte pas de scènes, et surtout pas la
/// table des punitions : « il n'avait pas scouté, alors le Vaisseau de guerre
/// est arrivé sur une base sans anti-aérien » ne s'extrait d'aucune page de
/// wiki. Ça vient des parties qu'on a perdues.
///
/// Les règles d'usage de l'API sont tenues par le code, parce qu'elles ne sont
/// pas négociables : une requête toutes les deux secondes, un User-Agent qui dit
/// qui appelle, gzip, et le résultat rangé dans le corpus pour ne jamais
/// redemander la même chose. L'attribution CC BY-SA 3.0 est écrite dans le
/// fichier produit.
/// </summary>
public sealed class LiquipediaHarvester
{
    public const string Host = "liquipedia.net";
    public const double DelaySeconds = 2.0;

    private const string Endpoint = "https://liquipedia.net/starcraft2/api.php";
    private const string Attribution = "Chiffres des unités : Liquipedia (CC BY-SA 3.0), https://liquipedia.net/starcraft2";

    /// <summary>Un contact joignable est exigé par les conditions d'utilisation.</summary>
    public string UserAgent { get; init; } =
        "CoachingIA/0.6 (outil de coaching personnel, hors ligne; contact: antchat@hotmail.fr)";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Ce qu'on va chercher dans l'infobox, et sous quel nom on le range.</summary>
    private static readonly (string Field, string Label)[] Wanted =
    [
        ("minerals", "minerai"),
        ("gas", "gaz"),
        ("supply", "supply"),
        ("buildtime", "temps de production"),
        ("hp", "points de vie"),
        ("shield", "boucliers"),
        ("armor", "armure"),
        ("range", "portée"),
        ("speed", "vitesse"),
        ("attack", "attaque"),
    ];

    public HarvestResult Harvest(Corpus corpus, Action<string>? log = null)
    {
        using var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");

        int filled = 0, missed = 0, failed = 0;
        var last = DateTimeOffset.MinValue;

        foreach (var unit in corpus.Units)
        {
            // Déjà renseignée : on ne redemande pas. La mise en cache n'est pas
            // une optimisation, c'est une condition d'utilisation de l'API.
            if (unit.Figures.Count > 0) { filled++; continue; }

            var wait = TimeSpan.FromSeconds(DelaySeconds) - (DateTimeOffset.UtcNow - last);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            last = DateTimeOffset.UtcNow;

            try
            {
                var wiki = Fetch(client, PageName(unit));
                if (wiki is null) { missed++; log?.Invoke($"{unit.Name,-22} page introuvable"); continue; }

                var figures = Extract(wiki);
                if (figures.Count == 0) { missed++; log?.Invoke($"{unit.Name,-22} aucun chiffre lisible"); continue; }

                foreach (var (k, v) in figures) unit.Figures[k] = v;
                filled++;
                log?.Invoke($"{unit.Name,-22} {string.Join(", ", figures.Select(f => $"{f.Key} {f.Value}"))}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failed++;
                log?.Invoke($"{unit.Name,-22} réseau : {ex.Message}");
            }
        }

        if (filled > 0 && !corpus.Source.Contains("Liquipedia", StringComparison.OrdinalIgnoreCase))
            log?.Invoke(Attribution);

        return new HarvestResult(filled, missed, failed);
    }

    /// <summary>Le titre de page le plus probable : le nom anglais de l'unité.</summary>
    private static string PageName(CorpusUnit unit)
    {
        // Les alias contiennent la forme anglaise quand le nom français diffère.
        var english = unit.Alias.FirstOrDefault(a => a.Length > 3 && !a.Contains('é') && !a.Contains('è'))
                      ?? unit.Name;
        return char.ToUpperInvariant(english[0]) + english[1..];
    }

    private static string? Fetch(HttpClient client, string page)
    {
        var url = $"{Endpoint}?action=query&prop=revisions&rvprop=content&rvslots=main&format=json"
                + $"&titles={Uri.EscapeDataString(page)}";

        var response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("query", out var query)) return null;
        if (!query.TryGetProperty("pages", out var pages)) return null;

        foreach (var p in pages.EnumerateObject())
        {
            if (p.Value.TryGetProperty("missing", out _)) return null;
            if (!p.Value.TryGetProperty("revisions", out var revisions)) continue;
            foreach (var rev in revisions.EnumerateArray())
                if (rev.TryGetProperty("slots", out var slots)
                    && slots.TryGetProperty("main", out var main)
                    && main.TryGetProperty("*", out var content))
                    return content.GetString();
        }
        return null;
    }

    /// <summary>
    /// Lit les champs d'infobox qui nous intéressent, et rien d'autre.
    /// Public parce que c'est la partie qui peut se tromper, donc celle qu'il
    /// faut pouvoir éprouver sans réseau.
    /// </summary>
    public static Dictionary<string, string> Extract(string wikitext)
    {
        var figures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, label) in Wanted)
        {
            var m = Regex.Match(wikitext, $@"\|\s*{Regex.Escape(field)}\s*=\s*([^\r\n|]+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var value = Clean(m.Groups[1].Value);
            if (value.Length is > 0 and < 40) figures[label] = value;
        }
        return figures;
    }

    private static string Clean(string raw)
    {
        var text = Regex.Replace(raw, @"\{\{[^}]*\}\}", " ");   // modèles
        text = Regex.Replace(text, @"\[\[([^\]|]*\|)?([^\]]*)\]\]", "$2");   // liens
        text = Regex.Replace(text, @"<[^>]+>", " ");            // balises
        text = Regex.Replace(text, @"'{2,}", "");               // gras et italiques
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
