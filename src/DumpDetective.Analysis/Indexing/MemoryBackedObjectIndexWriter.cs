using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing;

internal sealed class MemoryBackedObjectIndexWriter : IObjectIndexWriter
{
    private const long ProgressInterval = 50_000;

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        string? dumpPath = null,
        DumpDetective.Core.Models.DumpSizeTier sizeTier = DumpDetective.Core.Models.DumpSizeTier.Medium)
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

        var masterBuilder  = new TypeIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        var allSegmentEntries = new System.Collections.Concurrent.ConcurrentBag<(HeapEntry[] Buffer, int Count)>();
        var shapeCache = new ConcurrentDictionary<ulong, TypeShapeEntry>();
        long objectCount = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxSegmentParallelism
        };

        Parallel.ForEach(
            heap.Segments,
            parallelOptions,
            () => (Builder: new TypeIndexBuilder(), FlagsCache: new Dictionary<ulong, TypeAggregateFlags>(capacity: 64)),
            (segment, _, state) =>
            {
                int segGen = MemorySegmentKindToGeneration(segment.Kind);
                // Use minimum .NET object size (24 bytes on x64) as the upper-bound estimate so the
                // initial rent is guaranteed to hold all objects without resizing in the common case.
                // Capped at 1_000_000 entries (~24 MB) to keep each ArrayPool loan reasonable;
                // segments with more objects than the cap grow via pool doubling below — old buffers
                // are returned to the pool rather than discarded as GC garbage, eliminating the
                // ~800 MB of HeapEntry[] backing-array churn observed in profiling.
                const int MaxInitialRent = 1_000_000;
                int initCapacity = (int)Math.Min(Math.Max((long)segment.Length / 24, 64), MaxInitialRent);
                HeapEntry[] segBuf = ArrayPool<HeapEntry>.Shared.Rent(initCapacity);
                int segCount = 0;

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;
                    ulong mt = obj.Type.MethodTable;
                    if (mt == 0)
                        continue;

                    if (segCount == segBuf.Length)
                    {
                        // Grow via pool: return old buffer, rent one twice as large.
                        HeapEntry[] bigger = ArrayPool<HeapEntry>.Shared.Rent(segBuf.Length * 2);
                        segBuf.AsSpan(0, segCount).CopyTo(bigger);
                        ArrayPool<HeapEntry>.Shared.Return(segBuf, clearArray: false);
                        segBuf = bigger;
                    }

                    // Compute type flags + shape once per unique MT (thread-local cache).
                    TypeAggregateFlags flags;
                    if (!state.FlagsCache.TryGetValue(mt, out flags))
                    {
                        flags = ComputeTypeFlags(obj.Type);
                        state.FlagsCache[mt] = flags;
                        shapeCache.TryAdd(mt, ComputeTypeShape(obj.Type));
                    }

                    var entry    = new HeapEntry(obj.Address, mt, obj.Size);
                    int moduleId = moduleRegistry.GetOrAdd(obj.Type.Module);
                    segBuf[segCount++] = entry;
                    state.Builder.Add(entry, moduleId, flags, segGen);

                    long count = Interlocked.Increment(ref objectCount);
                    if (count % ProgressInterval == 0)
                        progress?.Report(new(count, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }

                allSegmentEntries.Add((segBuf, segCount));
                return state;
            },
            state =>
            {
                lock (masterBuilder)
                    masterBuilder.Merge(state.Builder);
            });

        // Flatten per-segment pooled buffers into a single exact-sized array,
        // then return each rented buffer to the pool so it can be reused.
        int totalCount = (int)Math.Min(objectCount, int.MaxValue);
        var flatEntries = new HeapEntry[Math.Max(totalCount, 1)];
        int writeOffset = 0;
        foreach ((HeapEntry[] segBuf, int segCount) in allSegmentEntries)
        {
            segBuf.AsSpan(0, segCount).CopyTo(flatEntries.AsSpan(writeOffset));
            writeOffset += segCount;
            ArrayPool<HeapEntry>.Shared.Return(segBuf, clearArray: false);
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
            Modules: moduleRegistry.Modules,
            GlobalSizeBuckets: masterBuilder.BuildSizeBuckets(),
            TypeShapeCache: shapeCache);
    }

    // ── Type classification helpers (mirror DiskBackedObjectIndexWriter) ───────

    private static TypeAggregateFlags ComputeTypeFlags(ClrType type)
    {
        TypeAggregateFlags flags = TypeAggregateFlags.None;

        string? name = type.Name;
        if (name is not null)
        {
            if (name == "System.String")
                flags |= TypeAggregateFlags.IsStringType;

            if (name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
                flags |= TypeAggregateFlags.IsTaskType;
        }

        if (type.IsArray)
            flags |= TypeAggregateFlags.IsArrayType;

        if (type.IsFinalizable)
            flags |= TypeAggregateFlags.IsFinalizableType;

        ClrType? current = type.BaseType;
        for (int depth = 0; depth < 4 && current is not null; depth++)
        {
            string? baseName = current.Name;
            if (baseName is "System.MulticastDelegate" or "System.Delegate")
            {
                flags |= TypeAggregateFlags.IsDelegateType;
                break;
            }
            current = current.BaseType;
        }

        return flags;
    }

    private static TypeShapeEntry ComputeTypeShape(ClrType type)
    {
        short refFields = 0;
        short valFields = 0;
        foreach (ClrInstanceField field in type.Fields)
        {
            if (field.IsObjectReference) refFields++;
            else valFields++;
        }
        return new TypeShapeEntry(refFields, valFields);
    }

    private static int MemorySegmentKindToGeneration(GCSegmentKind kind) => kind switch
    {
        GCSegmentKind.Generation0 => 0,
        GCSegmentKind.Generation1 => 1,
        GCSegmentKind.Generation2 => 2,
        _ => -1,
    };
}
