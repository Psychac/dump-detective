namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Append-only growable buffer used in place of <see cref="List{T}"/> for
/// <see cref="ReachableGraphWalker"/>'s accumulators (§D4/§D6). Grows by allocating a new fixed-size
/// chunk when the current one fills up — unlike <see cref="List{T}"/>'s double-and-copy growth,
/// existing chunks are never touched again once written, so there's no "old and new backing array
/// both alive at once" transient spike, and total overshoot is bounded by one chunk (a few hundred
/// KB) instead of up to ~2x the final size. At the tens-of-millions-of-elements scale a 25GB dump's
/// edge list reaches (E ≈ 137M), that overshoot is the difference between a rounding error and
/// multiple extra GB held transiently during the walk.
/// </summary>
internal sealed class ChunkedBuffer<T>
{
    private const int ChunkSize = 1 << 16; // 65,536 elements/chunk

    private readonly List<T[]> _chunks = new();
    private int _countInLastChunk;

    public int Count { get; private set; }

    public void Add(T value)
    {
        if (_chunks.Count == 0 || _countInLastChunk == ChunkSize)
        {
            _chunks.Add(new T[ChunkSize]);
            _countInLastChunk = 0;
        }

        _chunks[^1][_countInLastChunk] = value;
        _countInLastChunk++;
        Count++;
    }

    public T this[int index]
    {
        get => _chunks[index / ChunkSize][index % ChunkSize];
        set => _chunks[index / ChunkSize][index % ChunkSize] = value;
    }

    /// <summary>Copies every chunk into one right-sized array — same single O(Count) copy cost as
    /// <see cref="List{T}.ToArray"/>, without the doubling overshoot that preceded it.</summary>
    public T[] ToArray()
    {
        var result = new T[Count];
        int written = 0;
        for (int c = 0; c < _chunks.Count; c++)
        {
            int toCopy = Math.Min(ChunkSize, Count - written);
            Array.Copy(_chunks[c], 0, result, written, toCopy);
            written += toCopy;
        }
        return result;
    }
}
