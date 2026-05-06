namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Simple streaming reservoir sampler (Vitter's algorithm R variant).
/// Deterministic when seeded with a fixed <paramref name="seed"/>.
/// </summary>
internal sealed class ReservoirSampler<T>
{
    private readonly T[] _reservoir;
    private readonly Random _rng;
    private long _seen;

    public ReservoirSampler(int capacity, int seed)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _reservoir = new T[capacity];
        _rng = new Random(seed);
        _seen = 0;
    }

    public int Capacity => _reservoir.Length;

    public void Add(T item)
    {
        _seen++;
        int cap = Capacity;
        if (cap == 0) return;

        if (_seen <= cap)
        {
            _reservoir[(int)_seen - 1] = item;
            return;
        }

        // pick a random index in [0, seen)
        long r = (long)(_rng.NextDouble() * _seen);
        if (r < cap)
        {
            _reservoir[(int)r] = item;
        }
    }

    public IReadOnlyList<T> Samples()
    {
        int filled = (int)Math.Min(_seen, Capacity);
        var list = new List<T>(filled);
        for (int i = 0; i < filled; i++)
            list.Add(_reservoir[i]!);
        return list;
    }
}
