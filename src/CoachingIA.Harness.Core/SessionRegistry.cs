using System.Collections.Concurrent;
using System.Diagnostics;

namespace CoachingIA.Harness.Core;

/// <summary>
/// Les hooks arrivent en requetes HTTP independantes : rien ne relie nativement
/// l'appel d'outil de 14h02 a la session ouverte a 13h47. Ce registre tient les
/// spans encore ouverts (session, tour de prompt, sous-agent) pour que les spans
/// suivants sachent a qui se rattacher.
///
/// C'est volontairement en memoire : si le service redemarre au milieu d'une
/// session, les spans suivants deviennent racines plutot que de bloquer
/// l'ingestion. Une lignee perdue coute moins cher qu'une session perdue.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    private sealed record Entry(Activity Activity, DateTimeOffset OpenedAt)
    {
        public DateTimeOffset LastSeen { get; set; } = OpenedAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _open = new();
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;

    public SessionRegistry(TimeSpan idleTimeout, TimeProvider? time = null)
    {
        _idleTimeout = idleTimeout;
        _time = time ?? TimeProvider.System;
    }

    private static string Key(string scope, string id) => scope + ':' + id;

    public void Open(string scope, string id, Activity activity)
    {
        var key = Key(scope, id);
        var entry = new Entry(activity, _time.GetUtcNow());
        // Une reouverture du meme identifiant signale un hook double ou une reprise
        // de session : on ferme l'ancien span plutot que de le laisser fuir.
        if (_open.TryRemove(key, out var previous))
        {
            previous.Activity.SetTag(Coach.Outcome, "superseded");
            previous.Activity.Dispose();
        }
        _open[key] = entry;
    }

    public ActivityContext? ContextOf(string scope, string id)
    {
        if (id.Length == 0) return null;
        if (!_open.TryGetValue(Key(scope, id), out var entry)) return null;
        entry.LastSeen = _time.GetUtcNow();
        return entry.Activity.Context;
    }

    public Activity? Close(string scope, string id)
    {
        if (!_open.TryRemove(Key(scope, id), out var entry)) return null;
        return entry.Activity;
    }

    public int OpenCount => _open.Count;

    /// <summary>
    /// Ferme les spans dont plus aucun evenement n'arrive. Sans cela une session
    /// interrompue (fermeture brutale de l'editeur) resterait ouverte pour toujours
    /// et n'atteindrait jamais Phoenix, puisqu'un span n'est exporte qu'a sa fin.
    /// </summary>
    public int SweepIdle()
    {
        var cutoff = _time.GetUtcNow() - _idleTimeout;
        var closed = 0;
        foreach (var (key, entry) in _open)
        {
            if (entry.LastSeen > cutoff) continue;
            if (!_open.TryRemove(key, out _)) continue;
            entry.Activity.SetTag(Coach.Outcome, "abandoned");
            entry.Activity.Dispose();
            closed++;
        }
        return closed;
    }

    public void Dispose()
    {
        foreach (var (key, entry) in _open)
        {
            if (!_open.TryRemove(key, out _)) continue;
            entry.Activity.SetTag(Coach.Outcome, "harness_shutdown");
            entry.Activity.Dispose();
        }
    }

    public const string ScopeSession = "session";
    public const string ScopePrompt = "prompt";
    public const string ScopeAgent = "agent";
}
