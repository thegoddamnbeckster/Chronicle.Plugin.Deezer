using System.Collections.Concurrent;

namespace Chronicle.Plugin.Deezer;

/// <summary>
/// A small in-memory memory of things just fetched: the same album is needed for every one of its tracks, and a search hit is
/// needed again a moment later when its full record is asked for. Entries expire after a few minutes and the cache is emptied
/// when it grows large; it holds nothing between runs and is never authoritative.
/// </summary>
internal sealed class TtlCache<T>(TimeSpan lifetime, int capacity = 500, Func<DateTimeOffset>? clock = null)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, T Value)> _items = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public bool TryGet(string key, out T value)
    {
        if (_items.TryGetValue(key, out var hit) && _now() - hit.At < lifetime)
        {
            value = hit.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(string key, T value)
    {
        if (_items.Count >= capacity) _items.Clear();
        _items[key] = (_now(), value);
    }

    public int Count => _items.Count;
}
