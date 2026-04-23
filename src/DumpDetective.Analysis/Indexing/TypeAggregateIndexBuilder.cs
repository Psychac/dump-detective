using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Indexing;

internal sealed class TypeAggregateIndexBuilder
{
    private const ulong LohThresholdBytes = 85_000;
    private readonly Dictionary<ulong, MutableTypeAggregate> _aggregates = new(capacity: 1024);

    public void Add(in HeapEntry entry)
    {
        // OPT-#5: Single ref-returning probe via CollectionsMarshal eliminates the TryGetValue copy-out
        // + _aggregates[key] = agg copy-back that the previous TryGetValue/assign pattern required.
        // This is the innermost loop of the entire indexing pipeline — zero struct copies per object.
        ref MutableTypeAggregate aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _aggregates, entry.MethodTable, out bool existed);

        if (!existed)
            aggregate.SampleAddress = entry.Address;

        aggregate.Count++;
        aggregate.TotalSize += entry.Size;

        if (entry.Size >= LohThresholdBytes)
        {
            aggregate.LohCount++;
            aggregate.LohSize += entry.Size;
        }
    }

    public IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> Build()
    {
        Dictionary<ulong, TypeAggregateIndexEntry> result = new(_aggregates.Count);

        foreach ((ulong methodTable, MutableTypeAggregate aggregate) in _aggregates)
        {
            result[methodTable] = new TypeAggregateIndexEntry(
                methodTable,
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
    }
}
