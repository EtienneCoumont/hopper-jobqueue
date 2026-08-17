using System.Collections.Concurrent;

namespace HopperJobQueue.Api.Auth;

/// <summary>
/// In-memory buffer for <c>last_used_at</c>: usages are recorded here on every
/// authenticated request and written to the database at most once per minute by the
/// background task — never one update per request on the hot polling path.
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
