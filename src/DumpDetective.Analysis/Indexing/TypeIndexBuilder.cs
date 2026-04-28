using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Indexing;

internal sealed class TypeIndexBuilder
{
    private const ulong LohThresholdBytes = 85_000;
    private readonly Dictionary<ulong, MutableTypeAggregate> _aggregates = new(capacity: 1024);

    // GlobalSizeBuckets: 8 counters for the heap-wide object-size histogram.
    // Accumulated per-builder instance; merged into master during Merge().
    private readonly long[] _sizeBuckets = new long[SizeBucketHelper.BucketCount];

    public void Add(in HeapEntry entry, int moduleId = -1,
                    TypeAggregateFlags flags = TypeAggregateFlags.None,
                    int generation = -1)
    {
        // OPT-#5: Single ref-returning probe via CollectionsMarshal eliminates the TryGetValue copy-out
        // + _aggregates[key] = agg copy-back that the previous TryGetValue/assign pattern required.
        // This is the innermost loop of the entire indexing pipeline — zero struct copies per object.
        ref MutableTypeAggregate aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _aggregates, entry.MethodTable, out bool existed);

        if (!existed)
        {
            aggregate.SampleAddress = entry.Address;
            aggregate.ModuleId = moduleId;
            // Flags are type-level (same for all instances); set once on first encounter.
            aggregate.Flags = flags;
        }

        aggregate.Count++;
        aggregate.TotalSize += entry.Size;

        if (entry.Size >= LohThresholdBytes)
        {
            aggregate.LohCount++;
            aggregate.LohSize += entry.Size;
        }
        else
        {
            // Track Gen0/Gen1/Gen2 only for non-LOH objects (LOH is tracked separately).
            switch (generation)
            {
                case 0: aggregate.Gen0Count++; break;
                case 1: aggregate.Gen1Count++; break;
                case 2: aggregate.Gen2Count++; break;
                // generation == -1 (unknown) or 3 (LOH fallback) — no per-gen increment
            }
        }

        // Accumulate into the global size histogram.
        _sizeBuckets[SizeBucketHelper.GetBucketIndex(entry.Size)]++;
    }

    // Merges another builder's aggregates into this one.
    // Used to combine per-thread partial results from parallel segment scans.
    public void Merge(TypeIndexBuilder other)
    {
        foreach ((ulong methodTable, MutableTypeAggregate otherAgg) in other._aggregates)
        {
            ref MutableTypeAggregate aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _aggregates, methodTable, out bool existed);

            if (!existed)
            {
                aggregate.SampleAddress = otherAgg.SampleAddress;
                aggregate.ModuleId = otherAgg.ModuleId;
                aggregate.Flags    = otherAgg.Flags;
            }

            aggregate.Count     += otherAgg.Count;
            aggregate.TotalSize += otherAgg.TotalSize;
            aggregate.LohCount  += otherAgg.LohCount;
            aggregate.LohSize   += otherAgg.LohSize;
            aggregate.Gen0Count += otherAgg.Gen0Count;
            aggregate.Gen1Count += otherAgg.Gen1Count;
            aggregate.Gen2Count += otherAgg.Gen2Count;
        }

        // Merge size buckets.
        for (int i = 0; i < SizeBucketHelper.BucketCount; i++)
            _sizeBuckets[i] += other._sizeBuckets[i];
    }

    public IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> Build()
    {
        Dictionary<ulong, TypeAggregateIndexEntry> result = new(_aggregates.Count);

        foreach ((ulong methodTable, MutableTypeAggregate aggregate) in _aggregates)
        {
            result[methodTable] = new TypeAggregateIndexEntry(
                methodTable,
                aggregate.ModuleId,
                aggregate.Count,
                aggregate.TotalSize,
                aggregate.LohCount,
                aggregate.LohSize,
                aggregate.SampleAddress,
                aggregate.Gen0Count,
                aggregate.Gen1Count,
                aggregate.Gen2Count,
                aggregate.Flags);
        }

        return result;
    }

    /// <summary>
    /// Returns a copy of the accumulated 8-bucket object-size histogram.
    /// Call after <see cref="Merge"/> is complete to get the global totals.
    /// </summary>
    public long[] BuildSizeBuckets()
    {
        long[] copy = new long[SizeBucketHelper.BucketCount];
        _sizeBuckets.AsSpan().CopyTo(copy);
        return copy;
    }

    private struct MutableTypeAggregate
    {
        public long Count;
        public ulong TotalSize;
        public long LohCount;
        public ulong LohSize;
        public ulong SampleAddress;
        public int ModuleId;
        public int Gen0Count;
        public int Gen1Count;
        public int Gen2Count;
        public TypeAggregateFlags Flags;
    }
}
