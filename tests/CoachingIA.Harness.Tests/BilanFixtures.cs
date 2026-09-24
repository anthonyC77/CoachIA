using System.Text;
using System.Text.Json;

namespace CoachingIA.Harness.Tests;

/// <summary>
/// Un jeu de transcripts fabriqué pour le bilan durable : une session, une
/// tâche, une reprise — de quoi traverser tout le pipeline (inventaire,
/// segmentation, extraction, critique) sans jamais lire un vrai transcript.
///
/// Écrit sur le modèle de <see cref="TranscriptTests.Fixture"/> : mêmes types
/// de lignes, mêmes noms de champs. Réutilisée par la suite de tests des
/// activities de ce fichier, et par celle du workflow (tâche ultérieure).
/// </summary>
internal static class BilanFixtures
{
    /// <summary>
    /// Le prompt du témoin : pauvre au sens de la grille (aucun critère
    /// d'acceptation, aucun périmètre, aucune contrainte), et assez coûteux
    /// — une relance corrective suit — pour être choisi par <c>PromptPicker</c>
    /// comme le prompt de la semaine. Le repère « TEMOIN-7f3a2c » sert à le
    /// retrouver, intact, au bout du pipeline.
    /// </summary>
    public const string PromptTemoin = "Regarde le module TEMOIN-7f3a2c, il y a un souci.";

    /// <summary>La semaine du lundi 17/08/2026, où tombent les tâches écrites par <see cref="Ecrire"/>.</summary>
    public const string Semaine = "2026-W34";

    private static readonly DateTimeOffset Lundi = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Écrit un transcript JSONL dans <paramref name="dossier"/> et renvoie son chemin.</summary>
    public static string Ecrire(string dossier)
    {
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, "session-temoin.jsonl");
        File.WriteAllText(chemin, Fixture(), Encoding.UTF8);
        return chemin;
    }

    private static string Fixture()
    {
        var b = new StringBuilder();
        void Line(object o) => b.AppendLine(JsonSerializer.Serialize(o));
        const string sid = "sess-temoin";
        string At(int min) => Lundi.AddMinutes(min).ToString("O");

        // Le prompt témoin : pauvre, donc coûteux à reprendre.
        Line(new { type = "user", sessionId = sid, uuid = "u1", timestamp = At(0), cwd = "/home/dev/projet",
                   gitBranch = "main", version = "2.1.240", promptId = "p1", origin = new { kind = "human" },
                   message = new { role = "user", content = PromptTemoin } });

        Line(new { type = "assistant", sessionId = sid, uuid = "a1", timestamp = At(1), cwd = "/home/dev/projet",
                   gitBranch = "main", version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "tool_use",
                       usage = new { input_tokens = 12000, cache_read_input_tokens = 45000, cache_creation_input_tokens = 3000, output_tokens = 400 },
                       content = new object[] {
                           new { type = "tool_use", id = "tu1", name = "Grep", input = new { pattern = "module" } } } } });

        Line(new { type = "user", sessionId = sid, uuid = "u2", timestamp = At(2), version = "2.1.240",
                   message = new { role = "user", content = new object[] {
                       new { type = "tool_result", tool_use_id = "tu1", content = "2 matches" } } },
                   toolUseResult = new { durationMs = 120.0 } });

        Line(new { type = "assistant", sessionId = sid, uuid = "a2", timestamp = At(3), version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "end_turn",
                       usage = new { input_tokens = 13000, cache_read_input_tokens = 46000, cache_creation_input_tokens = 0, output_tokens = 300 },
                       content = new object[] { new { type = "text", text = "Voilà ce que j'ai trouvé." } } } });

        // Relance corrective : reste dans la même tâche, alimente ReworkTurns
        // et fait donc de ce prompt le plus coûteux de la semaine.
        Line(new { type = "user", sessionId = sid, uuid = "u3", timestamp = At(4), version = "2.1.240",
                   promptId = "p2", origin = new { kind = "human" },
                   message = new { role = "user", content = "Non, ce n'est pas ça, regarde plutôt le module de facturation." } });

        Line(new { type = "assistant", sessionId = sid, uuid = "a3", timestamp = At(5), version = "2.1.240",
                   message = new { model = "claude-opus-5", stop_reason = "end_turn",
                       usage = new { input_tokens = 14000, cache_read_input_tokens = 47000, cache_creation_input_tokens = 0, output_tokens = 200 },
                       content = new object[] { new { type = "text", text = "Corrigé." } } } });

        return b.ToString();
    }
}
