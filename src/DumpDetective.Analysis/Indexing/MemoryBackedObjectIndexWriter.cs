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

        // Cap DOP so ClrMD's minidump page cache never holds more than this many segments'
        // pages resident simultaneously.
        const int MaxSegmentParallelism = 4;

        var masterBuilder  = new TypeIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        var shapeCache     = new ConcurrentDictionary<ulong, TypeShapeEntry>();
        // OPT: global flags cache eliminates redundant ComputeTypeFlags calls across segments,
        // reducing IsFinalizable string allocations from (uniqueTypes × segmentCount) to uniqueTypes.
        var globalFlagsCache = new ConcurrentDictionary<ulong, TypeAggregateFlags>();
        // Collected during Phase 2 scan — mirrors what DiskBackedObjectIndexWriter writes to TaskIndex.bin.
        // Stored in HeapIndexBuildResult so AsyncTaskAnalyzer can read the pre-filtered list directly
        // instead of scanning all InMemoryEntries (O(N_total) vs O(N_tasks)).
        var taskCandidates  = new ConcurrentBag<(ulong Addr, ulong Mt)>();
        // Mirrors EventCandidateIndex.bin — pre-filtered delegate/event-handler addresses.
        // EventLeakAnalyzer (Priority 13) should prefer this over an O(N) full-index scan.
        var eventCandidates = new ConcurrentBag<(ulong Addr, ulong Mt)>();

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxSegmentParallelism
        };

        // ── Phase 1: count-only parallel scan ────────────────────────────────────────────
        // Enumerate each segment cheaply — IsValid check only, no type resolution, zero
        // heap allocations.  The resulting per-segment counts feed exact-size prefix sums
        // so Phase 2 can write directly into flatEntries without any intermediate buffers,
        // trimming, or over-allocation.
        ClrSegment[] segments = heap.Segments.ToArray();
        int[] perSegmentCounts = new int[segments.Length];

        progress?.Report(new(0, "pre-scanning heap", Detail: null, Elapsed: stopwatch.Elapsed));
        long phase1Count = 0;
        Parallel.For(0, segments.Length, parallelOptions, i =>
        {
            int count = 0;
            foreach (ClrObject obj in segments[i].EnumerateObjects())
            {
                if (obj.IsValid)
                {
                    count++;
                    long c = Interlocked.Increment(ref phase1Count);
                    if (c % ProgressInterval == 0)
                        progress?.Report(new(c, "pre-scanning heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }
            }
            perSegmentCounts[i] = count;
        });

        // Compute per-segment write offsets via prefix sums.
        int[] segmentOffsets = new int[segments.Length];
        int phase1Total = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            segmentOffsets[i] = phase1Total;
            phase1Total += perSegmentCounts[i];
        }

        // Allocate exactly the right number of slots — no over-allocation, no trim needed.
        // Phase 1 counts obj.IsValid inclusively; Phase 2 may skip a small number of those
        // (Type == null or MethodTable == 0).  actualCount tracks the true written count.
        HeapEntry[] flatEntries = GC.AllocateUninitializedArray<HeapEntry>(Math.Max(phase1Total, 1));
        long objectCount = 0;

        // ── Phase 2: full parallel scan — direct write into flatEntries ────────────────
        // Each segment writes into its own pre-computed contiguous slice of flatEntries at
        // segmentOffsets[i], so there are no cross-thread writes and no per-segment buffers.
        Parallel.For(
            0,
            segments.Length,
            parallelOptions,
            () => (Builder: new TypeIndexBuilder(), FlagsCache: new Dictionary<ulong, TypeAggregateFlags>(capacity: 64)),
            (i, _, state) =>
            {
                ClrSegment segment = segments[i];
                int segGen  = MemorySegmentKindToGeneration(segment.Kind);
                // For Ephemeral segments (workstation GC), generation cannot be inferred from
                // the segment kind — all of Gen0/1/2 share one segment.  Resolve per-object.
                bool isEphemeral = segGen < 0;
                int baseSlot = segmentOffsets[i];
                int written  = 0;
                int slotCap  = perSegmentCounts[i]; // safety: don't overrun this segment's slice

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;
                    ulong mt = obj.Type.MethodTable;
                    if (mt == 0)
                        continue;

                    // Compute type flags + shape once per unique MT globally.
                    TypeAggregateFlags flags;
                    if (!state.FlagsCache.TryGetValue(mt, out flags))
                    {
                        if (!globalFlagsCache.TryGetValue(mt, out flags))
                        {
                            flags = ComputeTypeFlags(obj.Type);
                            globalFlagsCache.TryAdd(mt, flags);
                            shapeCache.TryAdd(mt, ComputeTypeShape(obj.Type));
                        }
                        state.FlagsCache[mt] = flags;
                    }

                    var entry    = new HeapEntry(obj.Address, mt, obj.Size);
                    int moduleId = moduleRegistry.GetOrAdd(obj.Type.Module);
                    int objGen   = isEphemeral ? ResolveObjectGeneration(segment, obj.Address) : segGen;
                    state.Builder.Add(entry, moduleId, flags, objGen);

                    // Collect task candidates — same filter as DiskBackedObjectIndexWriter.
                    if ((flags & TypeAggregateFlags.IsTaskType) != 0)
                        taskCandidates.Add((obj.Address, mt));

                    // Collect delegate/event-handler candidates — mirrors EventCandidateIndex.bin.
                    if ((flags & TypeAggregateFlags.IsDelegateType) != 0)
                        eventCandidates.Add((obj.Address, mt));

                    // Write directly into this segment's reserved slice — no intermediate buffer.
                    if (written < slotCap)
                        flatEntries[baseSlot + written] = entry;

                    written++;

                    long count = Interlocked.Increment(ref objectCount);
                    if (count % ProgressInterval == 0)
                        progress?.Report(new(count, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }

                return state;
            },
            state =>
            {
                lock (masterBuilder)
                    masterBuilder.Merge(state.Builder);
            });

        int actualCount = (int)Math.Min(objectCount, int.MaxValue);

        // Phase 1 counts obj.IsValid; Phase 2 additionally filters Type==null / MT==0.
        // For a static dump those counts should be identical, but trim the rare difference.
        if (flatEntries.Length - actualCount > 50_000)
        {
            HeapEntry[] trimmed = GC.AllocateUninitializedArray<HeapEntry>(Math.Max(actualCount, 1));
            flatEntries.AsSpan(0, actualCount).CopyTo(trimmed);
            flatEntries = trimmed;
        }

        // Post-scan: enumerate GC roots — mirrors WriteSatelliteFiles/RootIndexWriter in disk mode.
        // Stored in HeapIndexBuildResult so GCRootAnalyzer and FinalizableObjectAnalyzer can
        // consume pre-enumerated root data without re-walking the heap.
        progress?.Report(new(objectCount, "enumerating GC roots", Detail: null, Elapsed: stopwatch.Elapsed));
        var rootList = new List<(ulong TargetAddr, ulong RootAddr, byte Kind)>(capacity: 4096);
        long rootCount = 0;
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            if (cancellationToken.IsCancellationRequested) break;
            rootList.Add((root.Object, root.Address, (byte)root.RootKind));
            if (++rootCount % 10_000 == 0)
                progress?.Report(new(objectCount, "enumerating GC roots", Detail: $"{rootCount:N0} roots", Elapsed: stopwatch.Elapsed));
        }

        stopwatch.Stop();
        progress?.Report(new(objectCount, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: "<memory>",
            ObjectCount: actualCount,
            Elapsed: stopwatch.Elapsed,
            TypeAggregates: masterBuilder.Build(),
            InMemoryEntries: flatEntries,
            Modules: moduleRegistry.Modules,
            GlobalSizeBuckets: masterBuilder.BuildSizeBuckets(),
            TypeShapeCache: shapeCache,
            InMemoryTaskCandidates: taskCandidates.ToArray(),
            InMemoryEventCandidates: eventCandidates.ToArray(),
            InMemoryRootCandidates: rootList.ToArray());
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
        _ => -1, // Ephemeral (workstation GC), LOH, POH — resolved per-object at call site
    };

    // Used when segGen < 0 (Ephemeral segment): asks ClrMD which generation the object belongs to.
    // Safe to call on all workstation-GC dumps; throws only if the address is completely invalid.
    private static int ResolveObjectGeneration(ClrSegment segment, ulong address)
    {
        try   { return (int)segment.GetGeneration(address); }
        catch { return -1; }
    }
}
