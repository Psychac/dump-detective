using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Indexing;

internal sealed class MemoryBackedObjectIndexWriter
{
    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        Action<long, TimeSpan>? progress = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Seed list capacity from heap segment sizes to avoid repeated doublings on large heaps.
        // Heuristic average object size: ~128 bytes. Cap to a reasonable max to avoid OOM.
        ulong totalBytes = 0;
        foreach (var seg in heap.Segments)
            totalBytes += seg.Length;
        int estimatedCount = (int)Math.Min(totalBytes / 128, 20_000_000);

        // Each thread accumulates its own entry list and type aggregate builder.
        // localFinally fires once per thread, so the merge is O(threadCount) not O(segmentCount).
        var masterBuilder = new TypeAggregateIndexBuilder();
        var allSegmentEntries = new System.Collections.Concurrent.ConcurrentBag<List<HeapEntry>>();
        long objectCount = 0;

        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        Parallel.ForEach(
            heap.Segments,
            parallelOptions,
            () => (Entries: new List<HeapEntry>(), Builder: new TypeAggregateIndexBuilder()),
            (segment, _, localState) =>
            {
                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;
                    ulong mt = obj.Type.MethodTable;
                    if (mt == 0)
                        continue;
                    var entry = new HeapEntry(obj.Address, mt, obj.Size);
                    localState.Entries.Add(entry);
                    localState.Builder.Add(entry);
                }
                return localState;
            },
            localState =>
            {
                allSegmentEntries.Add(localState.Entries);
                Interlocked.Add(ref objectCount, localState.Entries.Count);
                lock (masterBuilder)
                    masterBuilder.Merge(localState.Builder);
            });

        // Flatten per-thread entry lists into a single array.
        // The estimatedCount seed reduces List reallocations for large heaps.
        var entries = new List<HeapEntry>(capacity: Math.Max((int)Math.Min(objectCount, int.MaxValue), 1024));
        foreach (List<HeapEntry> segList in allSegmentEntries)
            entries.AddRange(segList);

        progress?.Invoke(objectCount, stopwatch.Elapsed);
        stopwatch.Stop();

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: "<memory>",
            ObjectCount: objectCount,
            Elapsed: stopwatch.Elapsed,
            TypeAggregates: masterBuilder.Build(),
            InMemoryEntries: entries.ToArray());
    }
}
