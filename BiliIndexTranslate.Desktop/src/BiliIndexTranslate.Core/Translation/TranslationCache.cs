namespace BiliIndexTranslate.Core.Translation;

/// <summary>Everything that changes the meaning of a cached entry is part of the key.</summary>
public readonly record struct CacheKey(string Text, string TargetLanguage, string Model, string EffectiveEndpoint, string PromptVersion, string SourceLanguage)
{
    public static CacheKey For(TranslationRequest r)
    {
        var endpoint = EndpointResolver.TryResolve(r.BaseUrl, r.UseRelay, r.RelayPrefix, out var ep, out _)
            ? ep!.NormalizedBase
            : (r.BaseUrl ?? string.Empty);
        return new CacheKey(r.Text, r.TargetLanguageCode, r.Model, endpoint, PromptBuilder.PromptVersion, r.SourceLanguage);
    }
}

public sealed record CacheEntry(string Translation, RequestRoute Route, DateTimeOffset StoredAt);

/// <summary>
/// In-memory LRU with TTL. Not persisted: nothing the user selected survives the process.
/// Thread-safe; reads are O(1) via a linked-list node map.
/// </summary>
public sealed class TranslationCache
{
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, CacheEntry Entry)>> _map = new();
    private readonly LinkedList<(CacheKey Key, CacheEntry Entry)> _lru = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;

    public TranslationCache(int capacity = 500, TimeSpan? ttl = null, Func<DateTimeOffset>? clock = null)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl ?? TimeSpan.FromHours(24);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int Count { get { lock (_gate) return _map.Count; } }
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public bool TryGet(CacheKey key, out CacheEntry entry)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (_clock() - node.Value.Entry.StoredAt <= _ttl)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    entry = node.Value.Entry;
                    Hits++;
                    return true;
                }
                _lru.Remove(node);
                _map.Remove(key);
            }
            Misses++;
            entry = null!;
            return false;
        }
    }

    public void Set(CacheKey key, string translation, RequestRoute route)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }
            var node = new LinkedListNode<(CacheKey, CacheEntry)>((key, new CacheEntry(translation, route, _clock())));
            _lru.AddFirst(node);
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }
}
