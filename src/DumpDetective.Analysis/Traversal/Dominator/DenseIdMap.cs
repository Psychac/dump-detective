namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Open-addressed <c>ulong -&gt; int</c> map (linear probing, power-of-two capacity), promoted from
/// the <c>tools/DominatorSpike</c> prototype (see
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §D2/§Architecture) —
/// ~13 bytes/slot vs. <c>Dictionary&lt;ulong,int&gt;</c>'s ~28-32 bytes/entry, which matters at the
/// tens-of-millions-of-nodes scale this is sized for.
/// </summary>
internal sealed class DenseIdMap
{
    private ulong[] _keys;
    private int[] _values;
    private bool[] _occupied;
    private int _mask;
    private int _count;

    public DenseIdMap(int initialCapacityPow2 = 1 << 16)
    {
        if (initialCapacityPow2 <= 0 || (initialCapacityPow2 & (initialCapacityPow2 - 1)) != 0)
            throw new ArgumentException("Initial capacity must be a positive power of two.", nameof(initialCapacityPow2));

        _keys = new ulong[initialCapacityPow2];
        _values = new int[initialCapacityPow2];
        _occupied = new bool[initialCapacityPow2];
        _mask = initialCapacityPow2 - 1;
    }

    public int Count => _count;

    /// <summary>Estimated resident bytes: 13 bytes/slot (8 key + 4 value + 1 occupied) times capacity.</summary>
    public long EstimatedBytes => (long)_keys.Length * (8 + 4 + 1);

    public bool TryGetValue(ulong key, out int value)
    {
        int mask = _mask;
        int slot = Hash(key) & mask;
        while (_occupied[slot])
        {
            if (_keys[slot] == key)
            {
                value = _values[slot];
                return true;
            }
            slot = (slot + 1) & mask;
        }
        value = -1;
        return false;
    }

    /// <summary>Inserts <paramref name="key"/>. Caller must ensure the key isn't already present.</summary>
    public void Add(ulong key, int value)
    {
        if ((_count + 1) * 10 > _keys.Length * 7)
            Grow();

        InsertUnchecked(key, value);
        _count++;
    }

    private void InsertUnchecked(ulong key, int value)
    {
        int mask = _mask;
        int slot = Hash(key) & mask;
        while (_occupied[slot])
            slot = (slot + 1) & mask;

        _occupied[slot] = true;
        _keys[slot] = key;
        _values[slot] = value;
    }

    private void Grow()
    {
        ulong[] oldKeys = _keys;
        int[] oldValues = _values;
        bool[] oldOccupied = _occupied;

        int newCap = _keys.Length * 2;
        _keys = new ulong[newCap];
        _values = new int[newCap];
        _occupied = new bool[newCap];
        _mask = newCap - 1;

        for (int i = 0; i < oldKeys.Length; i++)
        {
            if (oldOccupied[i])
                InsertUnchecked(oldKeys[i], oldValues[i]);
        }
    }

    private static int Hash(ulong key)
    {
        unchecked
        {
            ulong h = key * 0x9E3779B97F4A7C15UL;
            return (int)(h >> 32) & int.MaxValue;
        }
    }
}
