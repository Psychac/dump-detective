using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing;

internal sealed class MemoryBackedObjectIndexWriter
{
    private const long ProgressInterval = 50_000;

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Each segment gets its own entry list sized from its own byte length (see below).        // Each segment gets its own entry list sized from that segment's byte length.
        // This is far more accurate than distributing a whole-heap estimate across threads:
        // a per-thread list accumulates multiple segments and the threadCount divisor can
        // undershoot badly, causing List<T> to double many times and discard large HeapEntry[]
        // backing arrays (profiler showed ~955 MB wasted in discarded arrays).
        // Cap DOP so ClrMD's minidump page cache never holds more than this many segments'
        // pages resident simultaneously. Uncapped (default -1) causes ProcessorCount threads
        // to each hold a different segment's pages in cache concurrently, which multiplied the
        // working-set footprint proportional to core count after the parallel rearchitecture.
        // 4 concurrent segments gives ~4x speedup over sequential while bounding peak page pressure.
        const int MaxSegmentParallelism = 4;

        var masterBuilder = new TypeAggregateIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        var allSegmentEntries = new System.Collections.Concurrent.ConcurrentBag<List<HeapEntry>>();
        long objectCount = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxSegmentParallelism
        };

        Parallel.ForEach(
            heap.Segments,
            parallelOptions,
            () => new TypeAggregateIndexBuilder(),
            (segment, _, localBuilder) =>
            {
                // Size this segment's list from its own byte length — matches the 128-byte average
                // object size heuristic used for the whole-heap estimate, but applied per-segment
                // so the estimate is local to each segment rather than divided by thread count.
                // This eliminates the ÷ threadCount skew that caused heavily-loaded threads to
                // undershoot their capacity and trigger repeated List<T> doubling.
                int segEstimate = (int)Math.Min(Math.Max(segment.Length / 128, 64), int.MaxValue);
                var segEntries = new List<HeapEntry>(segEstimate);

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;
                    ulong mt = obj.Type.MethodTable;
                    if (mt == 0)
                        continue;
                    var entry = new HeapEntry(obj.Address, mt, obj.Size);
                    int moduleId = moduleRegistry.GetOrAdd(obj.Type.Module);
                    segEntries.Add(entry);
                    localBuilder.Add(entry, moduleId);

                    long count = Interlocked.Increment(ref objectCount);
                    if (count % ProgressInterval == 0)
                        progress?.Report(new(count, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }

                allSegmentEntries.Add(segEntries);
                return localBuilder;
            },
            localBuilder =>
            {
                lock (masterBuilder)
                    masterBuilder.Merge(localBuilder);
            });

        // Flatten per-segment lists into a single array.
        // objectCount is exact at this point so we allocate the array at the right size,
        // copying each segment list's contiguous span in one pass — no intermediate resizes.
        int totalCount = (int)Math.Min(objectCount, int.MaxValue);
        var flatEntries = new HeapEntry[Math.Max(totalCount, 1)];
        int writeOffset = 0;
        foreach (List<HeapEntry> segList in allSegmentEntries)
        {
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(segList)
                .CopyTo(flatEntries.AsSpan(writeOffset));
            writeOffset += segList.Count;
        }

        stopwatch.Stop();
        progress?.Report(new(objectCount, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: "<memory>",
            ObjectCount: objectCount,
            Elapsed: stopwatch.Elapsed,
            TypeAggregates: masterBuilder.Build(),
            InMemoryEntries: flatEntries,
            Modules: moduleRegistry.Modules);
    }
}
