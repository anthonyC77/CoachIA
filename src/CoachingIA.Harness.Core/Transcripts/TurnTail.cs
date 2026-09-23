using System.Collections.Concurrent;

namespace CoachingIA.Harness.Core.Transcripts;

/// <summary>
/// Lit la queue d'un transcript JSONL pour en tirer le modèle et les compteurs
/// de jetons du dernier tour clos par un hook Stop ou StopFailure.
///
/// Ne parse aucune ligne : c'est <see cref="TranscriptReader"/> qui lit le
/// JSONL, et <see cref="SessionBuilder"/> qui en reconstruit les tours.
/// TurnTail se contente de borner ce qu'on leur donne à lire — à la queue du
/// fichier plutôt qu'à son début, en copiant le segment voulu dans un fichier
/// temporaire — et mémorise, par session, le décalage d'où repartir.
///
/// Aucune méthode ne lève : un transcript absent, verrouillé ou tronqué en
/// cours d'écriture par Claude Code ne doit jamais faire échouer le hook Stop,
/// qui bloque le tour de l'apprenant. Au pire, <see cref="Read"/> renvoie
/// null et aucun span LLM ne part.
/// </summary>
public sealed class TurnTail
{
    /// <summary>Ce qu'une lecture de queue a trouvé pour le tour fermé.</summary>
    public sealed record Result(
        string? Model,
        long PromptTokens,
        long CompletionTokens,
        long CacheReadTokens,
        long NewOffset,
        long BytesRead);

    // Décalage en octets, par session, d'où repartira la prochaine lecture.
    private readonly ConcurrentDictionary<string, long> _offsets = new(StringComparer.Ordinal);

    /// <summary>Le décalage mémorisé pour une session, 0 si elle n'a jamais été lue.</summary>
    public long OffsetOf(string sessionId) => _offsets.TryGetValue(sessionId, out var o) ? o : 0;

    public Result? Read(string sessionId, string? promptId, string transcriptPath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(transcriptPath)) return null;

        var from = OffsetOf(sessionId);
        string? tempPath = null;
        long newOffset;
        long bytesRead;

        try
        {
            try
            {
                using var source = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // Le fichier a pu être remplacé ou tronqué depuis la dernière
                // lecture (rotation, session rejouée sur le même chemin) :
                // plutôt que de ne plus jamais rien lire, on repart du début.
                if (from > source.Length) from = 0;
                source.Seek(from, SeekOrigin.Begin);

                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                var bytes = buffer.GetBuffer();
                var length = (int)buffer.Length;

                // Claude Code peut être en train d'écrire la dernière ligne du
                // segment qu'on vient de lire : la couper la rendrait illisible
                // maintenant, ET la moitié restante le resterait pour toujours
                // au prochain appel, puisque le décalage l'aurait déjà dépassée.
                // On ne fait donc progresser le décalage que jusqu'au dernier
                // saut de ligne complet du segment lu ; ce qui suit attend la
                // prochaine fermeture de tour.
                var lastNewline = length == 0 ? -1 : Array.LastIndexOf(bytes, (byte)'\n', length - 1);
                if (lastNewline < 0) return null; // rien de complet à lire pour l'instant

                var usable = lastNewline + 1;
                newOffset = from + usable;
                bytesRead = usable;

                tempPath = Path.Combine(Path.GetTempPath(), "coachingia-turntail-" + Guid.NewGuid().ToString("N") + ".jsonl");
                using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    dest.Write(bytes, 0, usable);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            try
            {
                var report = new ParseReport();
                var records = TranscriptReader.Read(tempPath, report).ToList();
                var sessions = SessionBuilder.Build(records);

                // Les octets sont consommés qu'on parvienne ou non à en tirer un
                // tour exploitable : relire la même queue au prochain hook ne
                // rapporterait rien de plus.
                _offsets[sessionId] = newOffset;

                var session = sessions.Find(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
                if (session is null || session.Turns.Count == 0) return null;

                Turn? turn;
                if (string.IsNullOrEmpty(promptId))
                {
                    // Aucun promptId à chercher : le dernier tour du segment est
                    // la meilleure estimation disponible.
                    turn = session.Turns[^1];
                }
                else
                {
                    turn = session.Turns.FindLast(t => string.Equals(t.PromptId, promptId, StringComparison.Ordinal));
                    // Un promptId fourni mais introuvable dans ce segment ne doit
                    // JAMAIS retomber sur le dernier tour : ce tour appartient à
                    // un autre prompt (par exemple parce que la ligne "user" de
                    // celui qu'on cherche était illisible et a disparu du modèle
                    // reconstruit). Mieux vaut ne rien dire que d'attribuer le
                    // modèle et les jetons d'un tour à un autre en silence.
                    if (turn is null) return null;
                }

                if (turn.Steps.Count == 0) return null;

                return new Result(
                    Model: turn.Steps[^1].Model,
                    PromptTokens: turn.PeakInputTokens,
                    CompletionTokens: turn.Steps.Sum(s => s.OutputTokens),
                    CacheReadTokens: turn.Steps.Sum(s => s.CacheReadTokens),
                    NewOffset: newOffset,
                    BytesRead: bytesRead);
            }
            catch (Exception)
            {
                // TranscriptReader absorbe déjà les lignes illisibles ; ce filet
                // couvre le reste (fichier temporaire disparu entre-temps, etc.)
                // plutôt que de parier la fermeture du tour sur cette garantie.
                return null;
            }
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { /* nettoyage au mieux */ }
            }
        }
    }
}
