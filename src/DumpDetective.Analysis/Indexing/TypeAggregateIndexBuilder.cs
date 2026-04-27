using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Indexing;

internal sealed class TypeAggregateIndexBuilder
{
    private const ulong LohThresholdBytes = 85_000;
    private readonly Dictionary<ulong, MutableTypeAggregate> _aggregates = new(capacity: 1024);

    public void Add(in HeapEntry entry, int moduleId = -1)
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
        }

        aggregate.Count++;
        aggregate.TotalSize += entry.Size;

        if (entry.Size >= LohThresholdBytes)
        {
            aggregate.LohCount++;
            aggregate.LohSize += entry.Size;
        }
    }

    // Merges another builder's aggregates into this one.
    // Used to combine per-thread partial results from parallel segment scans.
    public void Merge(TypeAggregateIndexBuilder other)
    {
        foreach ((ulong methodTable, MutableTypeAggregate otherAgg) in other._aggregates)
        {
            ref MutableTypeAggregate aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _aggregates, methodTable, out bool existed);

            if (!existed)
            {
                aggregate.SampleAddress = otherAgg.SampleAddress;
                aggregate.ModuleId = otherAgg.ModuleId;
            }

            aggregate.Count     += otherAgg.Count;
            aggregate.TotalSize += otherAgg.TotalSize;
            aggregate.LohCount  += otherAgg.LohCount;
            aggregate.LohSize   += otherAgg.LohSize;
        }
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
                aggregate.SampleAddress);
        }

        return result;
    }

    private struct MutableTypeAggregate
    {
        public long Count;
        public ulong TotalSize;
        public long LohCount;
        public ulong LohSize;
        public ulong SampleAddress;
        public int ModuleId;
    }
}
