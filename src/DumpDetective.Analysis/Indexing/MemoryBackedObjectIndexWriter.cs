using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Indexing;

internal sealed class MemoryBackedObjectIndexWriter : IObjectIndexWriter
{
    private const long ProgressInterval = 50_000;

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        string? dumpPath = null,
        DumpSizeTier sizeTier = DumpSizeTier.Medium)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Cap DOP so ClrMD's minidump page cache never holds more than this many segments'
        // pages resident simultaneously.
        const int MaxSegmentParallelism = 4;

        var masterBuilder = new TypeIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        var shapeCache = new ConcurrentDictionary<ulong, TypeShapeEntry>();
        // OPT: global flags cache eliminates redundant ComputeTypeFlags calls across segments,
        // reducing IsFinalizable string allocations from (uniqueTypes × segmentCount) to uniqueTypes.
        var globalFlagsCache = new ConcurrentDictionary<ulong, TypeAggregateFlags>();
        // Collected during Phase 2 scan — mirrors what DiskBackedObjectIndexWriter writes to TaskIndex.bin.
        // Stored in HeapIndexBuildResult so AsyncTaskAnalyzer can read the pre-filtered list directly
        // instead of scanning all InMemoryEntries (O(N_total) vs O(N_tasks)).
        var taskCandidates = new ConcurrentBag<(ulong Addr, ulong Mt)>();
        // Mirrors EventCandidateIndex.bin — pre-filtered delegate/event-handler addresses.
        // EventLeakAnalyzer (Priority 13) should prefer this over an O(N) full-index scan.
        var eventCandidates = new ConcurrentBag<(ulong Addr, ulong Mt)>();
        // String dedup index: built during phase 2 while dump pages are already hot.
        // Key: XxHash64 of raw UTF-16 bytes. Each segment accumulates a local dict;
        // merge is done under masterBuilder lock to avoid ConcurrentDictionary overhead.
        const int MaxDedupUnique = 500_000;    // hard cap on unique patterns tracked
        const int MaxDedupStringLength = 1024;
        // Adaptive sampling: recalibrate every window to skip AsString() when the
        // unique-hash yield rate drops (i.e. strings are highly duplicated).
        const int DedupSampleWindow = 5_000;   // objects between yield-rate checks
        const double DedupYieldCutoff1 = 0.05; // < 5% unique → sample 1 in 10
        const double DedupYieldCutoff2 = 0.01; // < 1% unique → sample 1 in 50
        var masterStringDedup = new Dictionary<ulong, StringDedupEntry>(capacity: 4096);
        var globalLengthSamples = new List<int>();
        var globalLengthBuckets = new Dictionary<string, int>(StringComparer.Ordinal);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxSegmentParallelism
        };

        // Single-pass scan: each segment writes into its own List<HeapEntry> so we only
        // traverse the minidump once.  Per-segment lists are combined into one flat array
        // after the parallel phase via a fast in-memory copy — no Phase 1 pre-scan needed.
        ClrSegment[] segments = heap.Segments.ToArray();
        List<HeapEntry>[] segmentEntryLists = new List<HeapEntry>[segments.Length];
        for (int k = 0; k < segments.Length; k++)
        {
            // Pre-size from the segment's committed byte range to avoid List<T> doubling
            // reallocations. 32 bytes is a conservative lower bound for a valid object
            // on x64 (MT(8)+sync(4 hidden in MT low bits)+fields). Capped at 4M to avoid
            // over-committing for unusually large or corrupt segments.
            ClrSegment seg = segments[k];
            ulong segBytes = seg.Length;   // committed bytes
            int initCap = (int)Math.Min(segBytes / 32UL, 4_000_000UL);
            segmentEntryLists[k] = new List<HeapEntry>(Math.Max(initCap, 64));
        }

        HeapEntry[] flatEntries = Array.Empty<HeapEntry>(); // assigned after Phase 2
        long objectCount = 0;

        progress?.Report(new(0, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));

        // ── Parallel scan — write into per-segment lists, no cross-thread writes ─────
        // Each segment has its own List<HeapEntry> written exclusively by the task that
        // processes that segment index, so no locking is required on the list itself.
        Parallel.For(
            0,
            segments.Length,
            parallelOptions,
            () => (Builder: new TypeIndexBuilder(), FlagsCache: new Dictionary<ulong, TypeAggregateFlags>(capacity: 64), StringDedup: new Dictionary<ulong, StringDedupEntry>(capacity: 64), LengthSamples: new List<int>(), LengthBuckets: new Dictionary<string, int>(StringComparer.Ordinal)),
            (i, _, state) =>
            {
                ClrSegment segment = segments[i];
                int segGen = MemorySegmentKindToGeneration(segment.Kind);
                // For Ephemeral segments (workstation GC), generation cannot be inferred from
                // the segment kind — all of Gen0/1/2 share one segment.  Resolve per-object.
                bool isEphemeral = segGen < 0;
                List<HeapEntry> segList = segmentEntryLists[i]; // written only by this task
                // Adaptive string dedup sampling: track yield rate and skip AsString()
                // when strings are highly duplicated (avoids GC pressure + dump I/O).
                int segStringsTotal = 0;   // all string objects seen by this segment
                int segStringsSampled = 0; // actual obj.AsString() calls made
                int segWindowCount = 0;    // objects in current recalibration window
                int segSampleDivisor = 1;  // 1 = scan all; 10 = every 10th; 50 = every 50th

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

                    var entry = new HeapEntry(obj.Address, mt, obj.Size);
                    int moduleId = moduleRegistry.GetOrAdd(obj.Type.Module);
                    int objGen = isEphemeral ? ResolveObjectGeneration(segment, obj.Address) : segGen;
                    state.Builder.Add(entry, moduleId, flags, objGen);

                    // Collect task candidates — same filter as DiskBackedObjectIndexWriter.
                    if ((flags & TypeAggregateFlags.IsTaskType) != 0)
                        taskCandidates.Add((obj.Address, mt));

                    // Collect delegate/event-handler candidates — mirrors EventCandidateIndex.bin.
                    if ((flags & TypeAggregateFlags.IsDelegateType) != 0)
                        eventCandidates.Add((obj.Address, mt));

                    // Build string dedup index while dump pages are hot from type resolution.
                    // Adaptive sampling: recalibrate every DedupSampleWindow objects to skip
                    // AsString() calls when the unique-hash yield rate is low (many dupes).
                    if ((flags & TypeAggregateFlags.IsStringType) != 0
                        && state.StringDedup.Count < MaxDedupUnique)
                    {
                        segStringsTotal++;
                        segWindowCount++;

                        // Recalibrate the sample divisor every window.
                        if (segWindowCount >= DedupSampleWindow)
                        {
                            segWindowCount = 0;
                            double yieldRate = segStringsSampled > 0
                                ? (double)state.StringDedup.Count / segStringsSampled
                                : 1.0;
                            segSampleDivisor = yieldRate < DedupYieldCutoff2 ? 50
                                             : yieldRate < DedupYieldCutoff1 ? 10
                                             : 1;
                        }

                        // Apply sampling: skip this string if it falls outside the sample window.
                        if (segSampleDivisor > 1 && segStringsTotal % segSampleDivisor != 0)
                            goto WriteEntry;

                        segStringsSampled++;
                        string? val = obj.AsString(maxLength: MaxDedupStringLength);
                        if (val is { Length: > 0 })
                        {
                            int charLen = val.Length;
                            if (state.LengthSamples.Count < 100_000) state.LengthSamples.Add(charLen);
                            string key = charLen switch
                            {
                                < 16 => "0-15",
                                < 32 => "16-31",
                                < 64 => "32-63",
                                < 128 => "64-127",
                                < 256 => "128-255",
                                < 512 => "256-511",
                                < 1024 => "512-1023",
                                < 4096 => "1024-4095",
                                < 16384 => "4096-16383",
                                < 65536 => "16384-65535",
                                _ => "65536+"
                            };
                            state.LengthBuckets.TryGetValue(key, out int kc);
                            state.LengthBuckets[key] = kc + 1;

                            ulong h = XxHash64.HashToUInt64(MemoryMarshal.AsBytes(val.AsSpan()));
                            if (state.StringDedup.TryGetValue(h, out StringDedupEntry? e))
                            { e.AddInstance(obj.Size, obj.Address, obj.Type?.MethodTable ?? 0); }
                            else
                            { state.StringDedup[h] = new StringDedupEntry(CreatePreview(val), obj.Size, obj.Address, obj.Type?.MethodTable ?? 0); }
                        }
                    }

                    WriteEntry:
                    segList.Add(entry);

                    long count = Interlocked.Increment(ref objectCount);
                    if (count % ProgressInterval == 0)
                        progress?.Report(new(count, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }

                return state;
            },
            state =>
            {
                lock (masterBuilder)
                {
                    masterBuilder.Merge(state.Builder);
                    foreach (var kvp in state.StringDedup)
                    {
                        if (masterStringDedup.TryGetValue(kvp.Key, out StringDedupEntry? me))
                        { me.Count += kvp.Value.Count; me.TotalSize += kvp.Value.TotalSize; }
                        else if (masterStringDedup.Count < MaxDedupUnique)
                        { masterStringDedup[kvp.Key] = kvp.Value; }
                    }

                    if (state.LengthSamples.Count > 0)
                    {
                        int remaining = Math.Max(0, 100_000 - globalLengthSamples.Count);
                        if (remaining > 0)
                        {
                            int take = Math.Min(remaining, state.LengthSamples.Count);
                            globalLengthSamples.AddRange(state.LengthSamples.Take(take));
                        }
                        foreach (var kv in state.LengthBuckets)
                        {
                            globalLengthBuckets.TryGetValue(kv.Key, out int cur);
                            globalLengthBuckets[kv.Key] = cur + kv.Value;
                        }
                    }
                }
            });

        int actualCount = (int)Math.Min(objectCount, int.MaxValue);

        // Combine per-segment lists into one flat array (in-memory memcpy — no dump I/O).
        // Per-segment lists provide exact counts so no trimming is needed.
        progress?.Report(new(objectCount, "merging segment data", Detail: $"{actualCount:N0} objects", Elapsed: stopwatch.Elapsed));
        {
            int runningOffset = 0;
            int[] offsets = new int[segments.Length];
            for (int k = 0; k < segments.Length; k++)
            {
                offsets[k] = runningOffset;
                runningOffset += segmentEntryLists[k].Count;
            }
            flatEntries = GC.AllocateUninitializedArray<HeapEntry>(Math.Max(runningOffset, 1));
            for (int k = 0; k < segments.Length; k++)
            {
                CollectionsMarshal.AsSpan(segmentEntryLists[k]).CopyTo(flatEntries.AsSpan(offsets[k]));
                // Report every segment — ThrottledProgress upstream drops excess calls.
                progress?.Report(new(objectCount, "merging segment data", Detail: $"{actualCount:N0} objects, segment {k + 1}/{segments.Length}", Elapsed: stopwatch.Elapsed));
            }
            segmentEntryLists = null!; // release lists for GC before post-scan work
        }

        // Post-scan: enumerate GC roots — mirrors WriteSatelliteFiles/RootIndexWriter in disk mode.
        // Stored in HeapIndexBuildResult so GCRootAnalyzer and FinalizableObjectAnalyzer can
        // consume pre-enumerated root data without re-walking the heap.
        progress?.Report(new(0, "enumerating GC roots", Detail: "0 roots", Elapsed: stopwatch.Elapsed));
        var rootList = new List<(ulong TargetAddr, ulong RootAddr, byte Kind)>(capacity: 4096);
        long rootCount = 0;
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            if (cancellationToken.IsCancellationRequested) break;
            rootList.Add((root.Object, root.Address, (byte)root.RootKind));
            rootCount++;
            // Every 1000 roots to keep call overhead low; ThrottledProgress upstream gates the rate.
            if (rootCount % 1000 == 0)
                progress?.Report(new(rootCount, "enumerating GC roots", Detail: $"{rootCount:N0} roots", Elapsed: stopwatch.Elapsed));
        }

        progress?.Report(new(rootCount, "enumerating GC roots", Detail: $"{rootCount:N0} roots", Elapsed: stopwatch.Elapsed));

        // Build distribution summary from pre-collected in-memory samples (CPU only — no dump I/O).
        DistributionSummary? distribution = null;
        try
        {
            var percentiles = new Dictionary<string, double>(StringComparer.Ordinal);
            int sampleCount = globalLengthSamples.Count;
            if (sampleCount > 0)
            {
                globalLengthSamples.Sort();
                percentiles["p50"] = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.50)];
                percentiles["p75"] = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.75)];
                percentiles["p90"] = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.90)];
                percentiles["p95"] = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.95)];
            }

            var freqBuckets = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["1"] = 0,
                ["2"] = 0,
                ["3-10"] = 0,
                ["11-100"] = 0,
                ["101-1000"] = 0,
                ["1001+"] = 0
            };
            foreach (var e in masterStringDedup.Values)
            {
                int c = e.Count;
                if (c <= 1) freqBuckets["1"]++;
                else if (c == 2) freqBuckets["2"]++;
                else if (c <= 10) freqBuckets["3-10"]++;
                else if (c <= 100) freqBuckets["11-100"]++;
                else if (c <= 1000) freqBuckets["101-1000"]++;
                else freqBuckets["1001+"]++;
            }

            distribution = new DistributionSummary(percentiles, globalLengthBuckets.Count > 0 ? globalLengthBuckets : new Dictionary<string, int>(), freqBuckets, globalLengthSamples.Count);
        }
        catch { distribution = null; }

        // Enumerate GC handles into an in-memory snapshot so analyzers can reuse without
        // re-enumerating runtime.EnumerateHandles() multiple times. Cap to a conservative
        // upper bound to avoid unbounded memory use in pathological dumps.
        const int MaxHandleSnapshot = 500_000;
        var handleList = new List<(ulong Addr, ulong Mt, byte Kind)>();
        try
        {
            progress?.Report(new(0, "enumerating GC handles", Detail: "0 handles", Elapsed: stopwatch.Elapsed));
            var runtime = heap.Runtime;
            long lastHandleReportMs = 0;
            var handleSw = Stopwatch.StartNew();
            foreach (var h in runtime.EnumerateHandles())
            {
                if (handleList.Count >= MaxHandleSnapshot) break;
                ulong addr = h.Object.Address;
                ulong mt = 0UL;
                if (addr != 0)
                {
                    var o = heap.GetObject(addr);
                    if (o.IsValid) mt = o.Type?.MethodTable ?? 0UL;
                }
                handleList.Add((addr, mt, (byte)h.HandleKind));

                long nowMs = handleSw.ElapsedMilliseconds;
                if (nowMs - lastHandleReportMs >= 1000)
                {
                    lastHandleReportMs = nowMs;
                    progress?.Report(new(handleList.Count, "enumerating GC handles",
                        Detail: $"{handleList.Count:N0} handles", Elapsed: stopwatch.Elapsed));
                }
            }
            progress?.Report(new(handleList.Count, "enumerating GC handles",
                Detail: $"{handleList.Count:N0} handles", Elapsed: stopwatch.Elapsed));
        }
        catch { }

        stopwatch.Stop();
        progress?.Report(new(objectCount, "index complete", Detail: $"{actualCount:N0} objects", Elapsed: stopwatch.Elapsed));

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Memory,
            IndexPath: "<memory>",
            ObjectCount: actualCount,
            Elapsed: stopwatch.Elapsed,
            TypeAggregates: masterBuilder.Build(),
            InMemoryEntries: flatEntries,
            InMemoryHandleSnapshot: handleList.Count > 0 ? handleList.ToArray() : null,
            Modules: moduleRegistry.Modules,
            GlobalSizeBuckets: masterBuilder.BuildSizeBuckets(),
            TypeShapeCache: shapeCache,
            InMemoryTaskCandidates: taskCandidates.ToArray(),
            InMemoryEventCandidates: eventCandidates.ToArray(),
            InMemoryRootCandidates: rootList.ToArray(),
            StringDedupIndex: masterStringDedup.Count > 0 ? masterStringDedup : null,
            StringDedupDistribution: distribution);
    }

    // ── Type classification helpers (mirror DiskBackedObjectIndexWriter) ───────

    private static string CreatePreview(string value)
    {
        string s = value.Length > 47 ? value[..47] + "..." : value;
        return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

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
        try { return (int)segment.GetGeneration(address); }
        catch { return -1; }
    }
}
