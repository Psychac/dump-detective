namespace DumpDetective.Analysis.Algorithms;

/// <summary>
/// Space-Saving streaming heavy-hitters counter (Metwally, Agrawal &amp; El Abbadi, 2005):
/// finds an approximate top-K by frequency in a single pass with O(capacity) memory,
/// regardless of stream order.
///
/// A plain fixed-capacity dictionary that stops admitting new keys once full is biased
/// toward whatever happened to be seen first — a key with a huge true count arriving after
/// the cap is reached is dropped entirely (see
/// docs/analysis/phase1/dominator-analyzer-audit.md Area 6 item 3, first-come-first-served
/// admission bias). Space-Saving instead always evicts the tracked key with the globally
/// lowest count when an unseen key arrives at capacity, and re-admits the new key starting
/// from that evicted count. A key can therefore never be excluded in favor of a key with a
/// lower true count.
///
/// Guarantees (for a stream of <c>n</c> total increments against <c>capacity</c> slots):
/// reported <see cref="Entries"/> counts are always &gt;= true frequency (over-estimate,
/// never under), and any key whose true frequency exceeds <c>n / capacity</c> is guaranteed
/// to be present in the final table. Each entry's error bound (returned alongside its count
/// by <see cref="Entries"/> and <see cref="TryGetCount"/>) bounds the over-estimate:
/// <c>Count - Error</c> is a safe lower bound on the true frequency.
/// </summary>
/// <typeparam name="TKey">Key type; must support equality (used as a dictionary key).</typeparam>
public sealed class SpaceSavingCounter<TKey> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, Entry> _entries;

    // Ascending by count: bucketsByCount.First() is always the globally minimum-count bucket,
    // so eviction never needs a full scan of _entries.
    private readonly SortedDictionary<int, HashSet<TKey>> _bucketsByCount;

    private readonly struct Entry
    {
        public readonly int Count;
        public readonly int Error;

        public Entry(int count, int error)
        {
            Count = count;
            Error = error;
        }
    }

    public SpaceSavingCounter(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

        _capacity = capacity;
        _entries = new Dictionary<TKey, Entry>(capacity, comparer);
        _bucketsByCount = new SortedDictionary<int, HashSet<TKey>>();
    }

    /// <summary>Number of distinct keys currently tracked (&lt;= capacity).</summary>
    public int TrackedCount => _entries.Count;

    /// <summary>
    /// Records one occurrence (or <paramref name="increment"/> occurrences) of
    /// <paramref name="key"/>. Returns <c>true</c> when this call caused an approximate
    /// admission — an unseen key replacing the current minimum-count tracked key because
    /// capacity was already full — and <c>false</c> when the count is exact (key was already
    /// tracked, or capacity had a free slot).
    /// </summary>
    public bool Offer(TKey key, int increment = 1)
    {
        if (increment <= 0)
            throw new ArgumentOutOfRangeException(nameof(increment), increment, "Increment must be positive.");

        if (_entries.TryGetValue(key, out Entry existing))
        {
            RemoveFromBucket(key, existing.Count);
            int newCount = existing.Count + increment;
            _entries[key] = new Entry(newCount, existing.Error);
            AddToBucket(key, newCount);
            return false;
        }

        if (_entries.Count < _capacity)
        {
            _entries[key] = new Entry(increment, 0);
            AddToBucket(key, increment);
            return false;
        }

        (TKey minKey, int minCount) = PeekMinimum();
        RemoveFromBucket(minKey, minCount);
        _entries.Remove(minKey);

        int replacementCount = minCount + increment;
        _entries[key] = new Entry(replacementCount, minCount);
        AddToBucket(key, replacementCount);
        return true;
    }

    /// <summary>Exact/estimated count and error bound for a tracked key, or <c>false</c> if not tracked.</summary>
    public bool TryGetCount(TKey key, out int count, out int error)
    {
        if (_entries.TryGetValue(key, out Entry entry))
        {
            count = entry.Count;
            error = entry.Error;
            return true;
        }

        count = 0;
        error = 0;
        return false;
    }

    /// <summary>
    /// All tracked keys with their counts and error bounds, in no particular order. Callers
    /// that need only the top-N should sort/select from this rather than assuming order.
    /// </summary>
    public IEnumerable<(TKey Key, int Count, int Error)> Entries
    {
        get
        {
            foreach (KeyValuePair<TKey, Entry> kv in _entries)
                yield return (kv.Key, kv.Value.Count, kv.Value.Error);
        }
    }

    private (TKey Key, int Count) PeekMinimum()
    {
        foreach (KeyValuePair<int, HashSet<TKey>> bucket in _bucketsByCount)
        {
            foreach (TKey key in bucket.Value)
                return (key, bucket.Key);
        }

        throw new InvalidOperationException($"{nameof(SpaceSavingCounter<TKey>)} is empty.");
    }

    private void AddToBucket(TKey key, int count)
    {
        if (!_bucketsByCount.TryGetValue(count, out HashSet<TKey>? bucket))
        {
            bucket = new HashSet<TKey>();
            _bucketsByCount[count] = bucket;
        }

        bucket.Add(key);
    }

    private void RemoveFromBucket(TKey key, int count)
    {
        if (!_bucketsByCount.TryGetValue(count, out HashSet<TKey>? bucket))
            return;

        bucket.Remove(key);
        if (bucket.Count == 0)
            _bucketsByCount.Remove(count);
    }
}
