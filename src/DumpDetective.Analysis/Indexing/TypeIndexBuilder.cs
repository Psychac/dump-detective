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
        bool isLoh = entry.Size >= LohThresholdBytes;

        // OPT-#5: Single ref-returning probe via CollectionsMarshal eliminates the TryGetValue copy-out
        // + _aggregates[key] = agg copy-back that the previous TryGetValue/assign pattern required.
        // This is the innermost loop of the entire indexing pipeline — zero struct copies per object.
        ref MutableTypeAggregate aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _aggregates, entry.MethodTable, out bool existed);

        if (!existed)
        {
            aggregate.SampleAddress = entry.Address;
            aggregate.SamplePriority = SamplePriority(isLoh, generation);
            aggregate.ModuleId = moduleId;
            // Flags are type-level (same for all instances); set once on first encounter.
            aggregate.Flags = flags;
        }
        else
        {
            // Tie-break: prefer a longer-lived sample (LOH/Gen2 over Gen1 over Gen0/unknown — I-9,
            // docs/analysis/phase1/reference-chain-analyzer-audit.md) since a Gen0 sample is likely
            // to be transient and gives a less confident single-sample retention verdict downstream
            // (e.g. ReferenceChainAnalyzer). Within the same tier, lowest address wins — deterministic
            // and independent of scan order, since segments assigned to the same thread are not
            // guaranteed to be processed in address order.
            byte priority = SamplePriority(isLoh, generation);
            if (priority > aggregate.SamplePriority
                || (priority == aggregate.SamplePriority && entry.Address < aggregate.SampleAddress))
            {
                aggregate.SampleAddress = entry.Address;
                aggregate.SamplePriority = priority;
            }
        }

        aggregate.Count++;
        aggregate.TotalSize += entry.Size;

        if (isLoh)
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
                case 2: aggregate.Gen2Count++; aggregate.Gen2TotalSize += entry.Size; break;
                    // generation == -1 (unknown) or 3 (LOH fallback) — no per-gen increment
            }
        }

        // Accumulate into the global size histogram.
        _sizeBuckets[SizeBucketHelper.GetBucketIndex(entry.Size)]++;
    }

    private static byte SamplePriority(bool isLoh, int generation) =>
        isLoh || generation == 2 ? (byte)2 : generation == 1 ? (byte)1 : (byte)0;

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
                aggregate.SamplePriority = otherAgg.SamplePriority;
                aggregate.ModuleId = otherAgg.ModuleId;
                aggregate.Flags = otherAgg.Flags;
            }
            // Same generation-tier-then-lowest-address tie-break as Add(), applied across partial
            // builders — deterministic and independent of the non-deterministic order in which
            // parallel segment builders get merged.
            else if (otherAgg.SamplePriority > aggregate.SamplePriority
                || (otherAgg.SamplePriority == aggregate.SamplePriority && otherAgg.SampleAddress < aggregate.SampleAddress))
            {
                aggregate.SampleAddress = otherAgg.SampleAddress;
                aggregate.SamplePriority = otherAgg.SamplePriority;
            }

            aggregate.Count += otherAgg.Count;
            aggregate.TotalSize += otherAgg.TotalSize;
            aggregate.LohCount += otherAgg.LohCount;
            aggregate.LohSize += otherAgg.LohSize;
            aggregate.Gen0Count += otherAgg.Gen0Count;
            aggregate.Gen1Count += otherAgg.Gen1Count;
            aggregate.Gen2Count += otherAgg.Gen2Count;
            aggregate.Gen2TotalSize += otherAgg.Gen2TotalSize;
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
                aggregate.Flags,
                aggregate.Gen2TotalSize);
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
        // Not persisted to TypeAggregateIndexEntry/disk — purely a build-time tie-break input for
        // SampleAddress (I-9, docs/analysis/phase1/reference-chain-analyzer-audit.md). Discarded
        // once Build() picks the winning SampleAddress.
        public byte SamplePriority;
        public int ModuleId;
        public int Gen0Count;
        public int Gen1Count;
        public int Gen2Count;
        public ulong Gen2TotalSize;
        public TypeAggregateFlags Flags;
    }
}
