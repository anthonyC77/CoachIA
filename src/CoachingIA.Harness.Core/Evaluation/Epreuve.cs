using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoachingIA.Harness.Core.Evaluation;

/// <summary>
/// Un cas du jeu d'or : ce qu'on donne à l'outil, et ce qu'on attend de lui.
///
/// <c>Intitule</c> n'est pas décoratif : c'est le pendant du libellé des
/// vérifications du harnais — une phrase française qui énonce la promesse mise
/// à l'épreuve. Un cas dont on ne sait pas dire ce qu'il éprouve n'a rien à
/// faire dans le jeu.
///
/// <c>Entree</c> et <c>Attendu</c> restent du JSON brut : chaque famille a sa
/// forme, et un modèle typé par famille dupliquerait le schéma en C# alors que
/// le dépôt traite déjà les lentilles comme de la donnée.
/// </summary>
public sealed record Epreuve(
    string Id,
    string Famille,
    string Intitule,
    JsonElement Entree,
    JsonElement Attendu,
    bool Partageable,
    string Origine,
    IReadOnlyList<string> Etiquettes)
{
    /// <summary>Les origines qui citent de vrais prompts et ne peuvent pas être versionnées telles quelles.</summary>
    public bool CiteDuReel => string.Equals(Origine, "transcript", StringComparison.Ordinal);
}

/// <summary>Ce que le composant évalué a produit sur une épreuve.</summary>
/// <param name="Texte">Une représentation lisible, pour le rapport.</param>
/// <param name="Source">Qui a produit : « heuristique », « claude -p », « segmenteur »…</param>
/// <param name="Sujet">L'objet typé que les évaluateurs de la famille savent lire.</param>
public sealed record Production(string Texte, string Source, CoutAppel? Cout = null, object? Sujet = null)
{
    /// <summary>L'épreuve n'a pas pu être jouée. Les évaluateurs rendront une indécision.</summary>
    public string? Panne { get; init; }
}

/// <summary>
/// Le jeu d'épreuves chargé depuis <c>evals/</c>.
///
/// Tolérant comme le corpus de maturité : un fichier absent ou cassé se
/// signale et ne fait pas tomber la campagne. Une évaluation qui refuse de
/// démarrer parce qu'un fichier manque est une évaluation qu'on désactive.
/// </summary>
public sealed class JeuEpreuves
{
    public List<Epreuve> Epreuves { get; } = [];
    public List<string> Fichiers { get; } = [];
    public List<string> Avertissements { get; } = [];

    /// <summary>Le SHA-256 court du jeu. Deux campagnes sur des jeux différents ne se comparent pas.</summary>
    public string Empreinte { get; private set; } = "vide";

    public bool EstVide => Epreuves.Count == 0;

    /// <summary>
    /// Charge tous les <c>*.json</c> d'un dossier de cas. <paramref name="exigerPartageable"/>
    /// refuse les cas privés : c'est le garde de vie privée du dossier versionné.
    /// </summary>
    public static JeuEpreuves Charger(string dossier, bool exigerPartageable)
    {
        var jeu = new JeuEpreuves();
        if (!Directory.Exists(dossier))
        {
            jeu.Avertissements.Add($"aucun jeu d'épreuves sous {dossier}");
            return jeu;
        }

        var empreinte = new StringBuilder();
        foreach (var fichier in Directory.GetFiles(dossier, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string texte;
            try { texte = File.ReadAllText(fichier); }
            catch (IOException ex) { jeu.Avertissements.Add($"{Path.GetFileName(fichier)} illisible : {ex.Message}"); continue; }

            try
            {
                using var doc = JsonDocument.Parse(texte, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                jeu.Lire(doc.RootElement, Path.GetFileName(fichier), exigerPartageable);
                jeu.Fichiers.Add(Path.GetFileName(fichier));
                empreinte.Append(texte.ReplaceLineEndings("\n"));
            }
            catch (JsonException ex)
            {
                jeu.Avertissements.Add($"{Path.GetFileName(fichier)} mal formé : {ex.Message}");
            }
        }

        if (empreinte.Length > 0)
            jeu.Empreinte = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(empreinte.ToString())))[..8];

        return jeu;
    }

    private void Lire(JsonElement racine, string fichier, bool exigerPartageable)
    {
        var famille = racine.TryGetProperty("famille", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() ?? "" : "";

        if (!racine.TryGetProperty("epreuves", out var liste) || liste.ValueKind != JsonValueKind.Array)
        {
            Avertissements.Add($"{fichier} ne contient pas de tableau « epreuves »");
            return;
        }

        foreach (var e in liste.EnumerateArray())
        {
            var id = Texte(e, "id");
            if (id.Length == 0) { Avertissements.Add($"{fichier} : une épreuve sans identifiant est ignorée"); continue; }

            var propre = e.TryGetProperty("famille", out var pf) && pf.ValueKind == JsonValueKind.String
                ? pf.GetString() ?? famille : famille;
            if (propre.Length == 0) { Avertissements.Add($"{fichier} : l'épreuve {id} n'a pas de famille"); continue; }

            var partageable = !e.TryGetProperty("partageable", out var p) || p.ValueKind != JsonValueKind.False;
            var origine = Texte(e, "origine");
            if (origine.Length == 0) origine = "synthetique";

            // Le garde de vie privée. Un cas privé qui atterrit dans le dossier
            // versionné est un incident, pas une coquille : on le refuse et on
            // le dit fort.
            if (exigerPartageable && (!partageable || string.Equals(origine, "transcript", StringComparison.Ordinal)))
            {
                Avertissements.Add(
                    $"{fichier} : l'épreuve {id} n'est pas partageable (origine « {origine} ») et ne peut pas vivre dans le jeu versionné");
                continue;
            }

            Epreuves.Add(new Epreuve(
                id, propre, Texte(e, "intitule"),
                Copier(e, "entree"), Copier(e, "attendu"),
                partageable, origine, Etiquettes(e)));
        }
    }

    private static string Texte(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // Le JsonDocument est libéré à la sortie de Charger : on clone les sous-arbres
    // qu'on garde, sinon on lirait de la mémoire recyclée.
    private static JsonElement Copier(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) ? v.Clone() : default;

    private static IReadOnlyList<string> Etiquettes(JsonElement e)
    {
        if (!e.TryGetProperty("etiquettes", out var t) || t.ValueKind != JsonValueKind.Array) return [];
        return [.. t.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0)];
    }
}
