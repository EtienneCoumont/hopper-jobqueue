using System.Collections.Concurrent;

namespace HopperJobQueue.Api.Auth;

/// <summary>
/// Tampon en mémoire pour <c>last_used_at</c> : les usages sont notés ici à chaque requête
/// authentifiée et écrits en base au plus une fois par minute par la tâche de fond — jamais
/// un update par requête sur le chemin chaud du polling.
/// </summary>
public sealed class KeyUsageTracker
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> _pending = new();

    public void Touch(long keyId) => _pending[keyId] = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<long, DateTimeOffset> Drain()
    {
        if (_pending.IsEmpty)
        {
            return new Dictionary<long, DateTimeOffset>();
        }

        var snapshot = new Dictionary<long, DateTimeOffset>();
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var at))
            {
                snapshot[key] = at;
            }
        }

        return snapshot;
    }
}
