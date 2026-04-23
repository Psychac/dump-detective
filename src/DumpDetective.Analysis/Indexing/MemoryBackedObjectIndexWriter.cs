using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Indexing;

internal sealed class MemoryBackedObjectIndexWriter
{
    private const int ProgressReportEveryObjects = 100_000;

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        Action<long, TimeSpan>? progress = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TypeAggregateIndexBuilder aggregateBuilder = new();
        var entries = new List<HeapEntry>(capacity: 1024);
        long objectCount = 0;

        foreach (HeapEntry entry in HeapStreamer.Stream(heap))
        {
            cancellationToken.ThrowIfCancellationRequested();

            entries.Add(entry);
            aggregateBuilder.Add(entry);
            objectCount++;

            if (progress is not null && objectCount % ProgressReportEveryObjects == 0)
            {
                progress(objectCount, stopwatch.Elapsed);
            }
        }

        stopwatch.Stop();

        // OPT-#14: Convert to array at build time; list is never mutated after this point.
        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: "<memory>",
            ObjectCount: objectCount,
            Elapsed: stopwatch.Elapsed,
            TypeAggregates: aggregateBuilder.Build(),
            InMemoryEntries: entries.ToArray());
    }
}
