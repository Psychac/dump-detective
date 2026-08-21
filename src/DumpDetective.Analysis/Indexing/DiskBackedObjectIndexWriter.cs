using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Indexing.ForwardIndex;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Enums;
using DumpDetective.Analysis.Utilities;

namespace DumpDetective.Analysis.Indexing;

internal sealed class DiskBackedObjectIndexWriter : IObjectIndexWriter
{
    // Columnar Object* sections store one ulong per object per section — no per-section
    // header, since the container's TOC already carries each section's RecordCount.
    private const int ColumnSize = sizeof(ulong);
    // ObjectGenerations is a separate, narrower column (1 byte/sbyte vs. 8 bytes/ulong).
    private const int GenColumnSize = sizeof(sbyte);
    private const int ProgressReportEveryObjects = 100_000;
    // Per-bucket edge batch size for the reverse-index (see ReverseEdgeExtractor.RecordEdgesBatch):
    // amortizes the per-bucket lock over this many edges instead of taking it once per edge.
    private const int EdgeBatchSize = 2048;

    // TEMPORARY perf A/B toggle (see docs/cache/backlog.md, GC-root enumeration option 2):
    // set DD_SKIP_ROOT_INDEX_BUILD=1 to skip the eager Roots section write during Phase 1
    // and let RootSetCache's live-heap fallback build roots on demand in Phase 2 instead.
    // Remove once the A/B comparison picks a winner.
    private static readonly bool SkipRootIndexBuild =
        Environment.GetEnvironmentVariable("DD_SKIP_ROOT_INDEX_BUILD") == "1";

    // Escape hatch for the reverse-reference index (see docs/analysis/phase1-redesigns/full-reverse-index-plan.md):
    // set DD_SKIP_REVERSE_INDEX_BUILD=1 to skip forward-ref extraction during the heap scan if it
    // regresses build time on a given dump — analyzers that would use it simply fall back to
    // on-demand forward-ref enumeration, same as before this index existed.
    private static readonly bool SkipReverseIndexBuild =
        Environment.GetEnvironmentVariable("DD_SKIP_REVERSE_INDEX_BUILD") == "1";

    // Escape hatch for the forward-reference index (see
    // docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §D5): set
    // DD_SKIP_FORWARD_INDEX_BUILD=1 to skip it. Consumers (currently: the dominator-tree
    // reachability walk) fall back to a live ClrMD walk, same graceful-degradation contract as
    // every other optional satellite section.
    private static readonly bool SkipForwardIndexBuild =
        Environment.GetEnvironmentVariable("DD_SKIP_FORWARD_INDEX_BUILD") == "1";

    // Stage A's walk successors source defaults to ForwardEdgeLooseFileReader (see
    // docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §2/§8.8): after three
    // rounds of measurement, the final version (mmap'd .dat + an in-memory decoded directory,
    // binary-searched as a struct array) measured ~2x FASTER than a live ClrMD walk on a 25GB
    // real dump (833.7s vs. 1,663.9s Phase 1 build time) — the scale this project's whole purpose
    // targets — while being roughly at parity (not a meaningful regression) on a 3.3GB dump. Set
    // DD_FORCE_LIVE_CLRMD_WALK=1 to force the live-ClrMD walk instead (e.g. if the forward index
    // is unavailable for some other reason, or for future re-measurement).
    private static readonly bool ForceLiveClrMdWalk =
        Environment.GetEnvironmentVariable("DD_FORCE_LIVE_CLRMD_WALK") == "1";

    // Escape hatch for the SegmentIndex satellite section (see
    // docs/cache/cache-architecture.md): set DD_SKIP_SEGMENT_INDEX_BUILD=1 to skip it for
    // A/B build-time isolation. Cost is expected to be negligible (segment-count-sized, not
    // object-count-sized), so this is cheap insurance rather than an anticipated need — remove once
    // validated, same as the other temporary toggles above.
    private static readonly bool SkipSegmentIndexBuild =
        Environment.GetEnvironmentVariable("DD_SKIP_SEGMENT_INDEX_BUILD") == "1";

    // Escape hatch for Stage B (§10.3, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
    // set DD_SKIP_DOMINATOR_INDEX_BUILD=1 to force buildStageB false regardless of what
    // activeAnalyzers/enableExactDominatorTree say — same A/B-isolation contract as the other
    // Skip*Build flags above.
    private static readonly bool SkipDominatorIndexBuild =
        Environment.GetEnvironmentVariable("DD_SKIP_DOMINATOR_INDEX_BUILD") == "1";

    // §10.8 measurement pass (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
    // set DD_PERF_DOMINATOR_STAGEB=1 to print, in one Phase 1 run, everything §10.8 still needs a
    // real-dump number for — the unified walk's own wall-clock, each BuildAndPersistDominatorTree
    // sub-phase's wall-clock (metadata resolution, fold+LT, row mapping, child-index re-keying,
    // retained-bytes rollup), and the dominator child index's widest single row (hub-overflow
    // sizing). All from the one existing buildStageB pass — no second walk or separate run needed.
    private static readonly bool PerfLogDominatorStageB =
        Environment.GetEnvironmentVariable("DD_PERF_DOMINATOR_STAGEB") == "1";

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        string? dumpPath = null,
        DumpSizeTier sizeTier = DumpSizeTier.Medium,
        IReadOnlyList<IAnalyzer>? activeAnalyzers = null,
        bool enableExactDominatorTree = false)
    {
        ArgumentNullException.ThrowIfNull(dumpPath, nameof(dumpPath));
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Use canonical per-dump .dumpindex/ directory for the container file.
        DumpIndexPaths.EnsureDirectory(dumpPath);
        string containerPath = DumpIndexPaths.CacheContainer(dumpPath);

        // ── Fast-path: skip full heap scan if cache.bin has a valid TypeAggregates section ──
        // TypeAggregates is written LAST, after all other sections, so its presence
        // guarantees the previous build completed successfully.
        progress?.Report(new(0, "checking index cache", Detail: null, Elapsed: stopwatch.Elapsed));
        if (TryLoadFromCache(containerPath, dumpPath, out var cachedResult))
        {
            progress?.Report(new(cachedResult!.ObjectCount, "index cache hit",
                Detail: "loaded cache.bin — skipping heap scan",
                Elapsed: stopwatch.Elapsed));
            stopwatch.Stop();
            return cachedResult!;
        }

        // Cache miss (or no cache) — everything from here through the first Parallel.For tick was
        // previously unreported, showing as a silent gap that scales with dump size (observed ~2s
        // on a 3.3GB dump, ~20s on 25GB) before any progress appeared on screen. The likely
        // dominant cost is ClrHeap.Segments' first access below, which triggers ClrMD/DAC-side
        // segment-list resolution — but this is reported either way so it's visible regardless of
        // which sub-step actually turns out to be slow.
        progress?.Report(new(0, "preparing heap scan", Detail: null, Elapsed: stopwatch.Elapsed));

        long objectCount = 0;

        int writeBuffer = sizeTier switch
        {
            DumpSizeTier.Large => 4 * 1024 * 1024,
            DumpSizeTier.Medium => 1 * 1024 * 1024,
            _ => 128 * 1024,
        };
        // Each segment gets its own entry list sized from its own byte length (see below).

        // Cap DOP so ClrMD's minidump page cache never holds more than this many segments'
        // pages resident simultaneously. For Large dumps on SSDs, up to 8 concurrent segments
        // give additional throughput; smaller tiers use fewer to bound page-cache pressure.
        int maxSegmentParallelism = sizeTier switch
        {
            DumpSizeTier.Large => Math.Min(Environment.ProcessorCount, 8),
            DumpSizeTier.Medium => Math.Min(Environment.ProcessorCount, 4),
            _ => 2,
        };

        var masterBuilder = new TypeIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        // Satellite data collected during parallel scan, written serially afterwards.
        var shapeCache = new ConcurrentDictionary<ulong, TypeShapeEntry>();
        // Sparse: only populated for types with >=1 System.String field. Computed once per
        // unique MT alongside shapeCache below, so StringAnalyzer's ownership sampling doesn't
        // have to repeat this ClrType.Fields walk lazily on first encounter of each type.
        var stringFieldIndexCache = new ConcurrentDictionary<ulong, int[]>();
        // OPT: global flags cache eliminates redundant ComputeTypeFlags calls across segments,
        // reducing IsFinalizable string allocations from (uniqueTypes × segmentCount) to uniqueTypes.
        var globalFlagsCache = new ConcurrentDictionary<ulong, TypeAggregateFlags>();
        // OPT: global module-id cache — moduleRegistry.GetOrAdd takes a lock, so it must only be
        // reached once per unique MT globally, never once per object (module is a type-level property).
        var globalModuleIdCache = new ConcurrentDictionary<ulong, int>();
        var taskCandidates = new ConcurrentBag<(ulong Addr, ulong Mt, int StateFlags)>();
        var largeCandidates = new ConcurrentBag<(ulong Addr, ulong Mt, ulong Size)>();
        // Collected during scan to avoid a second walk of LOH/POH segments in LohFreeBlockWriter.
        var lohFreeBlockCandidates = new ConcurrentBag<(ulong SegStart, ulong Offset, ulong Size)>();
        // String dedup index built while dump pages are hot — zero extra I/O cost.
        const int MaxDedupUnique = 500_000;
        var masterStringDedup = new Dictionary<ulong, StringDedupEntry>(capacity: 4096);
        // Global distribution collectors (merged from per-thread state)
        var globalLengthSamples = new List<int>();
        var globalLengthBuckets = new Dictionary<string, int>(StringComparer.Ordinal);

        // OPT: each segment is scanned in parallel but serialized to its own scratch files.
        // Scratch files are concatenated in segment order after the scan instead of writing
        // directly to a shared stream under a lock — a shared-stream write order depends on
        // whichever thread's chunk finishes first, which is non-deterministic and made capped
        // scans (e.g. DominatorAnalyzer's MaxLeakScanObjects) see a different subset of objects
        // — and therefore different results — on every disk-mode run.
        //
        // Each segment writes three columnar scratch files (Address/MethodTable/Size) instead
        // of one interleaved file, so the concatenation phase can produce the three columnar
        // container sections directly — readers that only need one column (e.g. type
        // aggregation only touches MethodTable) then only pay for the bytes they read.
        int serialChunkEntries = Math.Max(writeBuffer / ColumnSize, 1);
        string indexDir = DumpIndexPaths.GetIndexDirectory(dumpPath);

        // Reverse-reference index: constructed here, but no longer fed during the per-object scan
        // below. It's populated after the scan completes by a BFS walk from the GC roots (see the
        // ReachableGraphWalker.Walk call right before WriteReverseIndexSections) — see
        // docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §7 for why only
        // BFS-reachable objects getting entries is not a loss of accuracy for any current consumer.
        int reverseIndexBucketCount = ReverseIndexConstants.CalculateBucketCount(new FileInfo(dumpPath).Length);
        ReverseEdgeExtractor? reverseEdgeExtractor = SkipReverseIndexBuild
            ? null
            : new ReverseEdgeExtractor(reverseIndexBucketCount, indexDir);

        // Forward-reference index (§D5): extracted in the per-object foreach below that enumerates
        // obj.EnumerateReferences(carefully: true), keyed by parent. Reuses the reverse index's
        // bucket-count formula (dump-size-based, not edge-count-based, so it applies equally well
        // here) even though the two indices are no longer built from the same pass.
        int forwardIndexBucketCount = ForwardIndexConstants.CalculateBucketCount(new FileInfo(dumpPath).Length);
        ForwardEdgeExtractor? forwardEdgeExtractor = SkipForwardIndexBuild
            ? null
            : new ForwardEdgeExtractor(forwardIndexBucketCount, indexDir);

        // §10.3 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): Stage B only
        // ever runs on top of Stage A actually running (reverseEdgeExtractor is not null is Stage A's
        // own existing gate, §7) — a narrower, already-shipped-code-grounded version of §3's
        // `canBuildReachableGraph` term. Deliberately does NOT gate Stage A's own construction above
        // on `IRequiresReachableGraphIndex` — that would change already-shipped Stage A's behavior,
        // which is out of scope here (see §10.3's note on this).
        bool buildStageB =
            reverseEdgeExtractor is not null
            && !SkipDominatorIndexBuild
            && enableExactDominatorTree
            && (activeAnalyzers?.Any(a => a is IRequiresDominatorTreeIndex) ?? false);

        // heap.Segments is lazily resolved by ClrMD on first access — reported separately from
        // "preparing heap scan" above so a profiling run can attribute time to this specific
        // DAC-side segment-list resolution rather than lumping it in with the setup around it.
        progress?.Report(new(0, "enumerating heap segments", Detail: null, Elapsed: stopwatch.Elapsed));
        ClrSegment[] segments = heap.Segments.ToArray();
        string[] segAddrScratchFiles = new string[segments.Length];
        string[] segMtScratchFiles = new string[segments.Length];
        string[] segSizeScratchFiles = new string[segments.Length];
        string[] segGenScratchFiles = new string[segments.Length];
        // SegmentIndex satellite (docs/cache/cache-architecture.md): each worker writes its
        // own segIdx slot exactly once below, so no lock is needed despite the parallel scan.
        long[] segRecordCounts = new long[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            segAddrScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.addr.tmp");
            segMtScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.mt.tmp");
            segSizeScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.size.tmp");
            segGenScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.gen.tmp");
        }

        using var containerWriter = new CacheContainerWriter(containerPath, dumpPath, progress);
        Stream stream = containerWriter.Stream;

        // Sub-phase allocation checkpoints (DD_PERF_INDEX_MEMORY=1). The stage total is ~10.5GB on a
        // 3.3GB dump, and attributing that to a phase is impossible from the outside — the whole
        // reason the previous "it's the bucket sorters" guess went unchallenged.
        var allocCheckpoints = new List<(string Phase, long Bytes)>();
        long allocMark = GC.GetTotalAllocatedBytes(precise: false);
        void MarkAlloc(string phase)
        {
            long now = GC.GetTotalAllocatedBytes(precise: false);
            allocCheckpoints.Add((phase, now - allocMark));
            allocMark = now;
        }

        // Peak concurrent bytes held by the per-worker columnar chunk buffers. Tracked on the same
        // footing as the whole-segment HeapEntry[] staging buffer it replaced (measured at 512 MB peak
        // on this dump) so the two are directly comparable in the log rather than one being measured
        // and the other asserted. Updated twice per segment, never per-object.
        long columnBufferLiveBytes = 0;
        long columnBufferPeakBytes = 0;

        void TrackColumnBufferDelta(long deltaBytes)
        {
            long live = Interlocked.Add(ref columnBufferLiveBytes, deltaBytes);
            long observedPeak = Interlocked.Read(ref columnBufferPeakBytes);
            while (live > observedPeak)
            {
                long prior = Interlocked.CompareExchange(ref columnBufferPeakBytes, live, observedPeak);
                if (prior == observedPeak)
                    break;
                observedPeak = prior;
            }
        }

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxSegmentParallelism
        };

        progress?.Report(new(0, "indexing heap", Detail: $"{segments.Length} segments, DOP={maxSegmentParallelism}", Elapsed: stopwatch.Elapsed));

        try
        {
        Parallel.For(
            0,
            segments.Length,
            parallelOptions,
            () => (Builder: new TypeIndexBuilder(), FlagsCache: new Dictionary<ulong, TypeAggregateFlags>(capacity: 64), ModuleIdCache: new Dictionary<ulong, int>(capacity: 64), StringDedup: new Dictionary<ulong, StringDedupEntry>(capacity: 64), LengthSamples: new List<int>(), LengthBuckets: new Dictionary<string, int>(StringComparer.Ordinal), ForwardEdgeBucketBuffers: forwardEdgeExtractor is null ? null : new List<(ulong Parent, ulong Child)>?[forwardIndexBucketCount], TaskStateFlagsFieldCache: new Dictionary<ulong, ClrInstanceField?>(capacity: 8)),
            (segIdx, _, state) =>
            {
                ClrSegment segment = segments[segIdx];
                // Determine generation from segment kind — avoids per-object GetGeneration call
                // for server GC where each segment is dedicated to a single generation.
                // For Ephemeral segments (workstation GC) segGen = -1; generation is resolved
                // per-object below via segment.GetGeneration(address).
                int segGen = SegmentKindToGeneration(segment.Kind);
                bool isEphemeral = segGen < 0;
                // LOH/POH: collect "Free" blob candidates to avoid a second segment walk.
                bool isLohOrPoh = segment.Kind == GCSegmentKind.Large
                               || segment.Kind == GCSegmentKind.Pinned;
                ulong segStart = isLohOrPoh ? segment.Start : 0;

                // Entries stream straight into this segment's columnar scratch files as they're
                // scanned — see SegmentColumnWriter for what this replaced and why. `using` scopes
                // disposal to the whole segment body, so the trailing partial chunk is flushed by
                // Complete() below and buffers/streams are released even if the scan throws.
                using var columnWriter = new SegmentColumnWriter(
                    segAddrScratchFiles[segIdx], segMtScratchFiles[segIdx],
                    segSizeScratchFiles[segIdx], segGenScratchFiles[segIdx],
                    serialChunkEntries, writeBuffer, TrackColumnBufferDelta);

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;
                    ulong mt = obj.Type.MethodTable;
                    if (mt == 0)
                        continue;

                    // Compute type flags + module id + shape once per unique MT. moduleRegistry.GetOrAdd
                    // takes a lock, so it must be reached at most once per unique MT globally — never
                    // once per object, or the lock serializes the entire parallel scan.
                    TypeAggregateFlags flags;
                    int moduleId;
                    if (!state.FlagsCache.TryGetValue(mt, out flags))
                    {
                        if (!globalFlagsCache.TryGetValue(mt, out flags))
                        {
                            flags = ComputeTypeFlags(obj.Type);
                            globalFlagsCache.TryAdd(mt, flags);
                            (TypeShapeEntry shape, int[] stringFieldIndices) = ComputeTypeShapeAndStringFields(obj.Type);
                            shapeCache.TryAdd(mt, shape);
                            if (stringFieldIndices.Length > 0)
                                stringFieldIndexCache.TryAdd(mt, stringFieldIndices);
                        }
                        state.FlagsCache[mt] = flags;
                        // GetOrAdd (not TryGetValue+TryAdd) — the factory may race and run more than
                        // once, but that's cheap and idempotent, unlike leaving a window where this
                        // entry can be observed missing after globalFlagsCache already has it.
                        moduleId = globalModuleIdCache.GetOrAdd(mt, _ => moduleRegistry.GetOrAdd(obj.Type.Module));
                        state.ModuleIdCache[mt] = moduleId;
                    }
                    else
                    {
                        moduleId = state.ModuleIdCache[mt];
                    }

                    int objGen = isEphemeral ? ResolveObjectGeneration(segment, obj.Address) : segGen;
                    var entry = new HeapEntry(obj.Address, mt, obj.Size, (sbyte)objGen);
                    columnWriter.Add(entry);
                    state.Builder.Add(entry, moduleId, flags, objGen);

                    // Forward-reference index (§D5): record every outgoing edge for this object,
                    // keyed by parent. "carefully" matches the enumeration mode validated in
                    // Investigation 1 (see pre-implementation-validation.md) and, unlike a raw
                    // field walk, also covers array elements — the dominant edge source for
                    // collection-held leaks.
                    //
                    // The reverse-edge index used to be populated here too (one batch per edge,
                    // keyed by child), but is now built after this scan completes by a BFS walk
                    // from the GC roots — see the walk-based build right before
                    // WriteReverseIndexSections is called below. See
                    // docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §7.
                    if (forwardEdgeExtractor is not null)
                    {
                        var forwardEdgeBuffers = state.ForwardEdgeBucketBuffers;
                        foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                        {
                            if (!reference.IsValid)
                                continue;

                            ulong child = reference.Address;

                            if (forwardEdgeExtractor is not null)
                            {
                                int fwdBucketIdx = (int)ForwardIndexConstants.ParentBucketHash(obj.Address, forwardIndexBucketCount);
                                List<(ulong Parent, ulong Child)>? fwdBucketBuf = forwardEdgeBuffers![fwdBucketIdx];
                                if (fwdBucketBuf is null)
                                {
                                    fwdBucketBuf = new List<(ulong Parent, ulong Child)>(EdgeBatchSize);
                                    forwardEdgeBuffers[fwdBucketIdx] = fwdBucketBuf;
                                }

                                fwdBucketBuf.Add((obj.Address, child));
                                if (fwdBucketBuf.Count >= EdgeBatchSize)
                                    forwardEdgeExtractor.RecordEdgesBatch(fwdBucketIdx, fwdBucketBuf);
                            }
                        }
                    }

                    // Collect satellite candidates (written serially after the parallel loop).
                    if ((flags & TypeAggregateFlags.IsTaskType) != 0)
                    {
                        // obj.Type is already resolved here, so reading m_stateFlags now costs
                        // one field lookup (cached per-MT below) and one field read — this is
                        // strictly cheaper than the Phase 2 ClrMD re-read it eliminates.
                        int taskStateFlags = 0;
                        if (!state.TaskStateFlagsFieldCache.TryGetValue(mt, out ClrInstanceField? stateField))
                        {
                            stateField = obj.Type.GetFieldByName("m_stateFlags") ?? obj.Type.GetFieldByName("_stateFlags");
                            state.TaskStateFlagsFieldCache[mt] = stateField;
                        }
                        if (stateField != null)
                            taskStateFlags = stateField.Read<int>(obj, interior: false);

                        taskCandidates.Add((obj.Address, mt, taskStateFlags));
                    }
                    if (entry.Size >= 85_000)
                        largeCandidates.Add((obj.Address, mt, entry.Size));
                    // Collect LOH/POH free blocks during the scan — avoids a second segment walk
                    // that LohFreeBlockWriter.Write(heap,...) would otherwise require.
                    // Uses obj.IsFree (same detection ClrMD uses in memory mode's
                    // AccumulateSegmentObjectByAddress) instead of a type-name match, so both
                    // modes agree even if "Free" name resolution is ever unreliable.
                    if (isLohOrPoh && obj.IsFree)
                        lohFreeBlockCandidates.Add((segStart, obj.Address - segStart, entry.Size));

                    // Build string dedup index while dump pages are hot from type resolution.
                    if ((flags & TypeAggregateFlags.IsStringType) != 0 && state.StringDedup.Count < MaxDedupUnique)
                    {
                        string? val = obj.AsString(maxLength: 1024);
                        if (val is { Length: > 0 })
                        {
                            // record length sample (bounded per-thread)
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

                    long count = Interlocked.Increment(ref objectCount);
                    if (progress is not null && count % ProgressReportEveryObjects == 0)
                        progress.Report(new(count, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }

                // Flush the trailing partial chunk, then record this segment's final object count for
                // the SegmentIndex satellite section (written after the parallel scan below). Each
                // segIdx is written by exactly one worker, so no lock is needed. Entries were already
                // streamed to this segment's own scratch files during the scan — no shared-stream
                // lock, so segments make independent progress, and the files are concatenated in
                // segment order after the scan completes, one column at a time.
                columnWriter.Complete();
                segRecordCounts[segIdx] = columnWriter.EntryCount;

                return state;
            },
            state =>
            {
                // Flush any partially-filled per-bucket edge batches before this thread-local
                // state is discarded — otherwise the last <EdgeBatchSize edges recorded against
                // each bucket would be silently dropped.
                if (forwardEdgeExtractor is not null)
                {
                    var forwardEdgeBuffers = state.ForwardEdgeBucketBuffers!;
                    for (int b = 0; b < forwardEdgeBuffers.Length; b++)
                    {
                        List<(ulong Parent, ulong Child)>? fwdBucketBuf = forwardEdgeBuffers[b];
                        if (fwdBucketBuf is { Count: > 0 })
                            forwardEdgeExtractor.RecordEdgesBatch(b, fwdBucketBuf);
                    }
                }

                lock (masterBuilder)
                {
                    masterBuilder.Merge(state.Builder);
                    // merge per-thread string dedup
                    foreach (var kvp in state.StringDedup)
                    {
                        if (masterStringDedup.TryGetValue(kvp.Key, out StringDedupEntry? me))
                        {
                            me.Count += kvp.Value.Count;
                            me.TotalSize += kvp.Value.TotalSize;
                            if (me.SampleAddresses is null && kvp.Value.SampleAddresses is not null)
                                me.SampleAddresses = kvp.Value.SampleAddresses;
                            else if (me.SampleAddresses is not null && kvp.Value.SampleAddresses is not null && me.SampleAddresses.Length < 2)
                            {
                                foreach (var a in kvp.Value.SampleAddresses)
                                {
                                    if (me.SampleAddresses.Length == 1 && me.SampleAddresses[0] != a)
                                    { me.SampleAddresses = new ulong[] { me.SampleAddresses[0], a }; break; }
                                }
                            }
                            if (me.DominantMethodTable == 0 && kvp.Value.DominantMethodTable != 0)
                                me.DominantMethodTable = kvp.Value.DominantMethodTable;
                        }
                        else if (masterStringDedup.Count < MaxDedupUnique)
                        { masterStringDedup[kvp.Key] = kvp.Value; }
                    }

                    // merge length samples/buckets (bounded)
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
        }
        catch
        {
            DeleteScratchFiles(segAddrScratchFiles);
            DeleteScratchFiles(segMtScratchFiles);
            DeleteScratchFiles(segSizeScratchFiles);
            DeleteScratchFiles(segGenScratchFiles);
            if (reverseEdgeExtractor is not null)
            {
                try { reverseEdgeExtractor.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best-effort */ }
                DeleteReverseIndexScratchFiles(indexDir, reverseIndexBucketCount);
            }
            if (forwardEdgeExtractor is not null)
            {
                try { forwardEdgeExtractor.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best-effort */ }
                DeleteForwardIndexScratchFiles(indexDir, forwardIndexBucketCount);
            }
            throw;
        }

        MarkAlloc("parallel heap scan (incl. edge extraction)");

        if (Environment.GetEnvironmentVariable("DD_PERF_INDEX_MEMORY") == "1")
        {
            Console.Error.WriteLine(
                $"[PERF] IndexScan columnar buffers: {segments.Length} segments, DOP={maxSegmentParallelism}, " +
                $"{objectCount:N0} objects — peak concurrent {columnBufferPeakBytes / (1024.0 * 1024):N1} MB " +
                $"(chunk {serialChunkEntries:N0} entries/column), leaked-live {columnBufferLiveBytes:N0} B. " +
                $"Replaced a whole-segment HeapEntry[] staging buffer measured at 512.0 MB peak.");
        }

        // Concatenate the per-segment scratch files into the three columnar sections, one
        // column at a time, in segment order — this is what makes disk-mode entry order
        // deterministic and match memory-mode's segment-ordered output, and keeps each
        // column contiguous in the container so a reader that only needs MethodTable (type
        // aggregation) or Size (histograms) doesn't pay to read Address too.
        // §10.1/§10.4: when Stage B wants them, the Address/MethodTable/Size scratch files are kept
        // on disk past this point for ScratchFileObjectMetadataLookup — deleted explicitly once
        // Stage B's metadata resolution finishes (see the reverseEdgeExtractor block below), instead
        // of here.
        containerWriter.BeginSection(CacheSectionId.ObjectAddresses);
        uint addrChecksum = ConcatenateScratchFiles(stream, segAddrScratchFiles, writeBuffer, deleteAfterCopy: !buildStageB);
        containerWriter.EndSection(objectCount, addrChecksum);

        containerWriter.BeginSection(CacheSectionId.ObjectMethodTables);
        uint mtChecksum = ConcatenateScratchFiles(stream, segMtScratchFiles, writeBuffer, deleteAfterCopy: !buildStageB);
        containerWriter.EndSection(objectCount, mtChecksum);

        containerWriter.BeginSection(CacheSectionId.ObjectSizes);
        uint sizeChecksum = ConcatenateScratchFiles(stream, segSizeScratchFiles, writeBuffer, deleteAfterCopy: !buildStageB);
        containerWriter.EndSection(objectCount, sizeChecksum);

        containerWriter.BeginSection(CacheSectionId.ObjectGenerations);
        uint genChecksum = ConcatenateScratchFiles(stream, segGenScratchFiles, writeBuffer);
        MarkAlloc("columnar scratch concatenation");
        containerWriter.EndSection(objectCount, genChecksum);

        // §10.1/§10.4: build the (SegmentIndexEntry, scratch-file-paths) triples
        // ScratchFileObjectMetadataLookup needs, mirroring the SegmentIndex satellite's own
        // Start/End/FirstRecordIndex/RecordCount loop below — built here, before that satellite
        // write, since it needs to exist regardless of whether SkipSegmentIndexBuild is set.
        List<ScratchSegmentSource>? scratchSegmentSources = null;
        if (buildStageB)
        {
            scratchSegmentSources = new List<ScratchSegmentSource>(segments.Length);
            long cumulativeRecordIndex = 0;
            for (int i = 0; i < segments.Length; i++)
            {
                long recordCount = segRecordCounts[i];
                if (recordCount > 0)
                {
                    scratchSegmentSources.Add(new ScratchSegmentSource(
                        new SegmentIndexEntry(segments[i].Start, segments[i].End, cumulativeRecordIndex, (int)recordCount),
                        segAddrScratchFiles[i], segMtScratchFiles[i], segSizeScratchFiles[i]));
                }
                cumulativeRecordIndex += recordCount;
            }
        }

        // Capture the main heap scan elapsed time for HeapIndexBuildResult before satellite writes.
        // We keep the stopwatch running during satellite file writes so their progress reports
        // show a growing elapsed rather than a frozen timestamp.
        TimeSpan scanElapsed = stopwatch.Elapsed;
        progress?.Report(new(objectCount, "index complete", Detail: null, Elapsed: scanElapsed));

        // Write satellite sections serially after the parallel heap scan.
        List<string> satelliteWarnings = WriteSatelliteSections(containerWriter, heap,
            taskCandidates, largeCandidates, lohFreeBlockCandidates,
            cancellationToken, progress, stopwatch);

        // SegmentIndex (docs/cache/cache-architecture.md): a small per-segment table of
        // (Start, End, FirstRecordIndex, RecordCount) enabling ObjectAddressLookup's binary-search
        // point lookup, backing IHeapAnalysisCache.TryGetObjectMetadata. Segment boundaries/record
        // counts are already known for free from the scan above — this only writes a
        // segment-count-sized table, not object-count-sized. Cumulative offsets must match
        // ConcatenateScratchFiles' write order above (segment index order), which they do since
        // both iterate `segments` in the same order. Skipped/non-fatal like every other satellite
        // section — a build without SegmentIndex still works, just without TryGetObjectMetadata.
        if (!SkipSegmentIndexBuild)
        {
            try
            {
                progress?.Report(new(0, "writing SegmentIndex section", Detail: null, Elapsed: stopwatch.Elapsed));
                var segmentIndexEntries = new List<SegmentIndexEntry>(segments.Length);
                long cumulativeRecordIndex = 0;
                for (int i = 0; i < segments.Length; i++)
                {
                    long recordCount = segRecordCounts[i];
                    if (recordCount > 0)
                    {
                        segmentIndexEntries.Add(new SegmentIndexEntry(
                            segments[i].Start, segments[i].End, cumulativeRecordIndex, (int)recordCount));
                    }
                    cumulativeRecordIndex += recordCount;
                }

                containerWriter.BeginSection(CacheSectionId.SegmentIndex);
                SegmentIndexWriter.Write(containerWriter.Stream, segmentIndexEntries);
                containerWriter.EndSection(segmentIndexEntries.Count);
            }
            catch (Exception ex)
            {
                containerWriter.AbortSection();
                satelliteWarnings.Add($"SegmentIndex: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Write StringDedup section (compact binary) so subsequent analyses
        // can read prebuilt dedup data without re-scanning the heap.
        try
        {
            containerWriter.BeginSection(CacheSectionId.StringDedup);
            Stream ds = containerWriter.Stream;
            Span<byte> hdr = stackalloc byte[12];
            // Magic 'SDUP' (written little-endian), version=1, entryCount (int)
            BinaryPrimitives.WriteInt32LittleEndian(hdr, 0x50554453);
            BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(hdr[8..], masterStringDedup.Count);
            ds.Write(hdr);

            // Rent small reusable buffers to avoid large stack allocations inside the loop.
            byte[] recBuf = ArrayPool<byte>.Shared.Rent(64);
            byte[] addrBuf = ArrayPool<byte>.Shared.Rent(8);
            try
            {
                foreach (var kvp in masterStringDedup)
                {
                    ulong hash = kvp.Key;
                    var e = kvp.Value;
                    // rec layout: hash(8) | count(4) | totalSize(8) | dominantMt(8) | sampleCount(1) | previewLen(2)
                    Span<byte> rec = recBuf.AsSpan();
                    BinaryPrimitives.WriteUInt64LittleEndian(rec, hash);
                    BinaryPrimitives.WriteInt32LittleEndian(rec[8..], e.Count);
                    BinaryPrimitives.WriteUInt64LittleEndian(rec[12..], e.TotalSize);
                    BinaryPrimitives.WriteUInt64LittleEndian(rec[20..], e.DominantMethodTable);
                    int sampleCount = e.SampleAddresses?.Length ?? 0;
                    rec[28] = (byte)Math.Min(sampleCount, 2);
                    ushort previewLen = 0;
                    string preview = e.Preview ?? string.Empty;
                    if (!string.IsNullOrEmpty(preview))
                    {
                        previewLen = (ushort)Math.Min(ushort.MaxValue, System.Text.Encoding.UTF8.GetByteCount(preview));
                    }
                    BinaryPrimitives.WriteUInt16LittleEndian(rec[29..], previewLen);
                    ds.Write(rec.Slice(0, 31));
                    if (sampleCount > 0)
                    {
                        for (int i = 0; i < Math.Min(2, sampleCount); i++)
                        {
                            BinaryPrimitives.WriteUInt64LittleEndian(addrBuf, e.SampleAddresses![i]);
                            ds.Write(addrBuf, 0, 8);
                        }
                    }
                    if (previewLen > 0)
                    {
                        byte[] tmp = ArrayPool<byte>.Shared.Rent(previewLen);
                        try
                        {
                            int written = System.Text.Encoding.UTF8.GetBytes(preview, 0, preview.Length, tmp, 0);
                            ds.Write(tmp, 0, written);
                        }
                        finally { ArrayPool<byte>.Shared.Return(tmp); }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recBuf);
                ArrayPool<byte>.Shared.Return(addrBuf);
            }
            ds.Flush();
            containerWriter.EndSection(masterStringDedup.Count);
        }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            satelliteWarnings.Add($"StringDedup: {ex.GetType().Name}: {ex.Message}");
        }

        // Persist lightweight distribution metadata as an opaque UTF-8 JSON section so readers
        // can populate a DistributionSummary without needing a full heap scan.
        try
        {
            if (globalLengthSamples.Count > 0 || masterStringDedup.Count > 0)
            {
                // compute percentiles
                IReadOnlyDictionary<string, double> percentiles = new Dictionary<string, double>(StringComparer.Ordinal);
                int sampleCount = globalLengthSamples.Count;
                if (sampleCount > 0)
                {
                    globalLengthSamples.Sort();
                    double p50 = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.50)];
                    double p75 = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.75)];
                    double p90 = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.90)];
                    double p95 = globalLengthSamples[(int)Math.Floor((sampleCount - 1) * 0.95)];
                    percentiles = new Dictionary<string, double> { ["p50"] = p50, ["p75"] = p75, ["p90"] = p90, ["p95"] = p95 };
                }

                // frequency buckets from masterStringDedup counts
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

                var distribution = new DistributionSummary(percentiles, globalLengthBuckets.Count > 0 ? globalLengthBuckets : new Dictionary<string, int>(), freqBuckets, sampleCount);

                var jsOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                byte[] jsonBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(distribution, jsOpts);

                containerWriter.BeginSection(CacheSectionId.StringDedupMeta);
                containerWriter.Stream.Write(jsonBytes, 0, jsonBytes.Length);
                containerWriter.Stream.Flush();
                containerWriter.EndSection(1);
            }
        }
        catch
        {
            // non-fatal — abort a partially-opened section so Finish() doesn't throw.
            try { containerWriter.AbortSection(); } catch { /* no section was open */ }
        }

        // Forward-reference index (§D5): flush, sort raw buckets into loose .dat/.idx scratch
        // files (Phase B) — run before the reachability walk below so the walk can read successors
        // from these files instead of a live ClrMD walk (§2, dominator-tree-phase1-integration.md).
        // Merging them into the container (Phase C) happens after the walk, once the loose files
        // are no longer needed as a successors source.
        ForwardEdgeExtractionStats? forwardIndexStats = null;
        if (forwardEdgeExtractor is not null)
        {
            MarkAlloc("satellite sections");
            (forwardIndexStats, string? forwardSortWarning) = SortForwardIndexBuckets(
                indexDir, forwardIndexBucketCount, forwardEdgeExtractor, cancellationToken, progress, stopwatch);
            if (forwardSortWarning is not null)
                satelliteWarnings.Add(forwardSortWarning);
        }

        // Reverse-reference index (Phase B + C) — flush, sort and merge the buckets extracted
        // during the heap scan above. Kept before stopwatch.Stop() so its progress reports (sort
        // can take a while on many buckets) show a growing elapsed like the satellite sections.
        if (reverseEdgeExtractor is not null)
        {
            MarkAlloc("reachability walk");

            // §7 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): the
            // reverse-edge index is now populated by a BFS walk from the GC roots instead of the
            // raw per-object field scan above. This means an object only gets a reverse-index
            // entry if it's actually reachable from a root — garbage never enters the walk, so it
            // never gets an entry. Every current consumer of this index searches *backward* from
            // an object toward a root, and a garbage object can have no such path by definition,
            // so this is not a loss of any answer the index used to give.
            progress?.Report(new(0, "walking reachable graph for reverse-edge index", Detail: null, Elapsed: stopwatch.Elapsed));
            var walkRootAddresses = new List<ulong>(4096);
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                ulong rootObjectAddress = root.Object.Address;
                if (rootObjectAddress != 0)
                    walkRootAddresses.Add(rootObjectAddress);
            }

            // §2/§8.8: ForwardEdgeLooseFileReader is the default (measured ~2x faster on a 25GB
            // real dump — see the field comment on ForceLiveClrMdWalk above). Falls back to live
            // ClrMD when forced, when the forward index was skipped, its sort failed, or the
            // loose files otherwise can't be opened — never a hard failure, same contract every
            // other optional satellite index in this codebase already has.
            ForwardEdgeLooseFileReader? looseForwardReader = null;
            SuccessorsFunc walkSuccessors;
            if (!ForceLiveClrMdWalk
                && forwardIndexStats is not null
                && ForwardEdgeLooseFileReader.TryOpen(indexDir, forwardIndexBucketCount, out looseForwardReader))
            {
                walkSuccessors = looseForwardReader!.GetChildren;
            }
            else
            {
                walkSuccessors = (ulong address, ref ulong[] buffer) =>
                {
                    ClrObject walkObj = heap.GetObject(address);
                    if (!walkObj.IsValid || walkObj.Type is null)
                        return 0;

                    int count = 0;
                    foreach (ClrObject child in walkObj.EnumerateReferences(carefully: true))
                    {
                        if (!child.IsValid || child.Address == 0)
                            continue;

                        if (count == buffer.Length)
                            Array.Resize(ref buffer, buffer.Length * 2);

                        buffer[count++] = child.Address;
                    }

                    return count;
                };
            }

            // buildCsr: buildStageB — §10.3's gating decides whether Stage B's CSR gets built
            // alongside Stage A's walk in this same pass (§10.1/§10.4).
            // captureSortedAddresses: true — DominatorReachableAddressWriter below needs the sorted
            // set regardless of whether Stage B ever runs.
            //
            // §10.8 Fix 1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): the
            // walk itself is the one place in this block with no isolation of its own — everything
            // after it (DominatorReachableAddressWriter.Write, BuildAndPersistDominatorTree,
            // WriteReverseIndexSections) already degrades gracefully on its own failure. If the walk
            // throws (a genuine OOM, or ChunkedBuffer's int-overflow guard tripping on a graph too
            // large to represent), every edge already streamed to reverseEdgeExtractor before that
            // point is now unreliable — so on failure this discards the reverse-edge index for this
            // dump entirely (same cleanup WriteReverseIndexSections's own catch already does for its
            // failures) rather than ever persisting or reading from a silently partial one, and skips
            // the rest of this block. Everything outside it (columnar sections, satellite sections,
            // forward index, TypeAggregates) is unaffected and still gets written.
            ReachableGraphWalkResult? walkResult = null;
            Stopwatch? walkStopwatch = PerfLogDominatorStageB ? Stopwatch.StartNew() : null;
            try
            {
                try
                {
                    walkResult = ReachableGraphWalker.Walk(
                        walkRootAddresses, walkSuccessors, reverseEdgeExtractor, buildCsr: buildStageB,
                        captureSortedAddresses: true, cancellationToken, progress);
                }
                finally
                {
                    looseForwardReader?.Dispose();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                satelliteWarnings.Add($"ReachableGraphWalk: {ex.GetType().Name}: {ex.Message}");

                try { reverseEdgeExtractor.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best-effort */ }
                DeleteReverseIndexScratchFiles(indexDir, reverseIndexBucketCount);

                if (buildStageB)
                {
                    DeleteScratchFiles(segAddrScratchFiles);
                    DeleteScratchFiles(segMtScratchFiles);
                    DeleteScratchFiles(segSizeScratchFiles);
                }
            }

            if (walkStopwatch is not null)
            {
                Console.Error.WriteLine($"[PERF] DominatorStageB: unified walk (buildCsr={buildStageB}) " +
                    $"took {walkStopwatch.Elapsed.TotalMilliseconds:N0} ms, " +
                    $"{(walkResult is null ? "failed" : $"{walkResult.NodeCount:N0} nodes")}");
            }

            if (walkResult is not null)
            {
                // §5 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): persist
                // the walk's reachable-address set so "is this object reachable?" is answerable from
                // disk without re-running the walk. DominatorReachableInDegree (also reserved in that
                // section) is deliberately not persisted — it would duplicate the exact fan-in counts
                // the reverse-edge index above already exposes via EnumerateChildCounts.
                DominatorReachableAddressWriter.Write(containerWriter, walkResult.ReachableAddresses);

                // §10.4 Batch 2a: Stage B's fold + LT + idom persistence, using the CSR the walk
                // above just built. The deferred Address/MethodTable/Size scratch files (kept on
                // disk by the deleteAfterCopy: !buildStageB calls above) are deleted here regardless
                // of outcome — this is the only place that still needs them.
                if (buildStageB)
                {
                    try
                    {
                        BuildAndPersistDominatorTree(
                            containerWriter, heap, walkResult, scratchSegmentSources!, cancellationToken, progress);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        satelliteWarnings.Add($"DominatorTree: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        DeleteScratchFiles(segAddrScratchFiles);
                        DeleteScratchFiles(segMtScratchFiles);
                        DeleteScratchFiles(segSizeScratchFiles);
                    }
                }

                MarkAlloc("reverse index (sort + write)");
                string? reverseIndexWarning = WriteReverseIndexSections(
                    containerWriter, indexDir, reverseIndexBucketCount, reverseEdgeExtractor,
                    cancellationToken, progress, stopwatch);
                if (reverseIndexWarning is not null)
                    satelliteWarnings.Add(reverseIndexWarning);
            }
        }

        // Forward-reference index Phase C: merge the loose files Phase B already sorted into the
        // container, then delete them. Only runs if Phase B above actually succeeded.
        if (forwardIndexStats is not null)
        {
            string? forwardIndexWarning = WriteForwardIndexSections(
                containerWriter, indexDir, forwardIndexBucketCount, forwardIndexStats, progress);
            if (forwardIndexWarning is not null)
                satelliteWarnings.Add(forwardIndexWarning);
        }

        // Extract aggregates once so they can be passed both to HeapIndexBuildResult and to
        // TypeAggregateIndexWriter without calling masterBuilder.Build() twice.
        var typeAggregates = masterBuilder.Build();
        var globalSizeBuckets = masterBuilder.BuildSizeBuckets();

        // Write the TypeAggregates section LAST so its presence confirms a complete build.
        // A future call to Build() will detect it and skip the full heap scan entirely.
        try
        {
            containerWriter.BeginSection(CacheSectionId.TypeAggregates);
            TypeAggregateIndexWriter.Write(containerWriter.Stream, typeAggregates,
                moduleRegistry.Modules, globalSizeBuckets, shapeCache, objectCount);
            containerWriter.EndSection(typeAggregates.Count);
        }
        catch
        {
            // Non-fatal: analysis proceeds without the cache. The section will be written on
            // the next successful full build (e.g. after a disk-full condition clears).
            try { containerWriter.AbortSection(); } catch { /* no section was open */ }
        }

        containerWriter.Finish();

        MarkAlloc("forward index (sort + write) + TypeAggregates");

        if (Environment.GetEnvironmentVariable("DD_PERF_INDEX_MEMORY") == "1")
        {
            long checkpointSum = 0;
            foreach ((string phase, long bytes) in allocCheckpoints)
                checkpointSum += bytes;

            Console.Error.WriteLine($"[PERF] IndexBuild allocation by phase (total {checkpointSum / (1024.0 * 1024 * 1024):N2} GB, " +
                $"{(objectCount == 0 ? 0 : checkpointSum / objectCount):N0} B/object over {objectCount:N0} objects):");
            foreach ((string phase, long bytes) in allocCheckpoints)
            {
                Console.Error.WriteLine($"[PERF]   {phase,-46} {bytes / (1024.0 * 1024):N1} MB" +
                    $"  ({(checkpointSum == 0 ? 0 : 100.0 * bytes / checkpointSum):N1}%)");
            }
            Console.Error.WriteLine($"[PERF]   gen0={GC.CollectionCount(0):N0} gen1={GC.CollectionCount(1):N0} gen2={GC.CollectionCount(2):N0}" +
                $"  managed-heap-now={GC.GetTotalMemory(false) / (1024.0 * 1024):N1} MB");
        }

        // Stopped only now — HeapIndexBuildResult.Elapsed (what the CLI's "Scan + Index heap"
        // checkmark displays) must cover the whole build, not just the core columnar scan
        // captured earlier in scanElapsed; satellite sections, the reverse-index build, and
        // TypeAggregates all run after that point and previously went uncounted.
        stopwatch.Stop();
        TimeSpan totalElapsed = stopwatch.Elapsed;

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            containerPath,
            objectCount,
            totalElapsed,
            typeAggregates,
            InMemoryEntries: null,
            Modules: moduleRegistry.Modules,
            GlobalSizeBuckets: globalSizeBuckets,
            TypeShapeCache: shapeCache,
            SatelliteWarnings: satelliteWarnings.Count > 0 ? satelliteWarnings : null,
            StringDedupIndex: masterStringDedup.Count > 0 ? masterStringDedup : null,
            StringFieldIndicesByMethodTable: stringFieldIndexCache.Count > 0 ? stringFieldIndexCache : null);
    }

    // ── Satellite section writing ────────────────────────────────────────────────

    private static List<string> WriteSatelliteSections(
        CacheContainerWriter containerWriter,
        ClrHeap heap,
        ConcurrentBag<(ulong Addr, ulong Mt, int StateFlags)> taskCandidates,
        ConcurrentBag<(ulong Addr, ulong Mt, ulong Size)> largeCandidates,
        ConcurrentBag<(ulong SegStart, ulong Offset, ulong Size)> lohFreeBlockCandidates,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress,
        Stopwatch stopwatch)
    {
        List<string> warnings = [];

        // Handles — GC handle enumeration
        try
        {
            progress?.Report(new(0, "enumerating GC handles", Detail: null, Elapsed: stopwatch.Elapsed));
            containerWriter.BeginSection(CacheSectionId.Handles);
            long recordCount = HandleSnapshotWriter.Write(containerWriter.Stream, heap.Runtime, cancellationToken, progress, stopwatch);
            containerWriter.EndSection(recordCount);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            warnings.Add($"Handles: {ex.GetType().Name}: {ex.Message}");
        }

        // Roots — GC root enumeration (can be slow on large dumps; progress reported every 50k roots)
        try
        {
            if (SkipRootIndexBuild)
            {
                // Section intentionally omitted; RootIndexReader treats a missing Roots
                // section as "no candidates", which triggers RootSetCache's live-heap fallback.
                progress?.Report(new(0, "skipping GC root index (DD_SKIP_ROOT_INDEX_BUILD=1)", Detail: null, Elapsed: stopwatch.Elapsed));
            }
            else
            {
                progress?.Report(new(0, "enumerating GC roots", Detail: null, Elapsed: stopwatch.Elapsed));
                containerWriter.BeginSection(CacheSectionId.Roots);
                long recordCount = RootIndexWriter.Write(containerWriter.Stream, heap, cancellationToken, progress, stopwatch);
                containerWriter.EndSection(recordCount);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            warnings.Add($"Roots: {ex.GetType().Name}: {ex.Message}");
        }

        // RootStackThreadAttribution — §12.2 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
        // which thread owns each Stack-kind root. Same gate as Roots (a ClrRoot alone carries no
        // thread identity, so this is only useful alongside the Roots section it cross-references
        // against at read time) — cheap relative to the rest of Phase 1, unconditional whenever
        // Roots itself builds, no separate opt-in.
        try
        {
            if (SkipRootIndexBuild)
            {
                progress?.Report(new(0, "skipping stack-root thread attribution (DD_SKIP_ROOT_INDEX_BUILD=1)", Detail: null, Elapsed: stopwatch.Elapsed));
            }
            else
            {
                progress?.Report(new(0, "enumerating stack root thread ownership", Detail: null, Elapsed: stopwatch.Elapsed));
                containerWriter.BeginSection(CacheSectionId.RootStackThreadAttribution);
                long recordCount = RootStackThreadIndexWriter.Write(containerWriter.Stream, heap, cancellationToken, progress, stopwatch);
                containerWriter.EndSection(recordCount);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            warnings.Add($"RootStackThreadAttribution: {ex.GetType().Name}: {ex.Message}");
        }

        // Tasks — Task objects collected during heap scan
        try
        {
            progress?.Report(new(0, "writing Tasks section", Detail: null, Elapsed: stopwatch.Elapsed));
            containerWriter.BeginSection(CacheSectionId.Tasks);
            using (TaskIndexWriter tw = new(containerWriter.Stream))
            {
                foreach ((ulong addr, ulong mt, int stateFlags) in taskCandidates)
                    tw.Add(addr, mt, stateFlags); // read during Phase 1 scan; 0 falls back to Phase 2 re-read
                tw.Flush();
            }
            containerWriter.EndSection(taskCandidates.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            warnings.Add($"Tasks: {ex.GetType().Name}: {ex.Message}");
        }

        // LargeObjects — top-100 LOH objects by size
        try
        {
            progress?.Report(new(0, "writing LargeObjects section", Detail: null, Elapsed: stopwatch.Elapsed));
            var tracker = new LargeObjectTracker();
            foreach ((ulong addr, ulong mt, ulong size) in largeCandidates)
                tracker.Consider(addr, mt, size);
            containerWriter.BeginSection(CacheSectionId.LargeObjects);
            tracker.Write(containerWriter.Stream);
            containerWriter.EndSection(largeCandidates.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            warnings.Add($"LargeObjects: {ex.GetType().Name}: {ex.Message}");
        }

        // LohFreeBlocks — free block gaps already collected during the main scan;
        // no second segment walk required.
        try
        {
            progress?.Report(new(0, "writing LohFreeBlocks section", Detail: null, Elapsed: stopwatch.Elapsed));
            containerWriter.BeginSection(CacheSectionId.LohFreeBlocks);
            long recordCount = LohFreeBlockWriter.WriteFromCandidates(
                containerWriter.Stream, lohFreeBlockCandidates, cancellationToken);
            containerWriter.EndSection(recordCount);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            warnings.Add($"LohFreeBlocks: {ex.GetType().Name}: {ex.Message}");
        }

        return warnings;
    }

    /// <summary>
    /// §10.4 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — Batch 2a: Stage
    /// B's fold + LT + idom persistence, run entirely inside Phase 1 using the CSR
    /// <paramref name="walkResult"/> just built. Per-node <c>MethodTable</c>/<c>Size</c> resolution
    /// goes through <see cref="ScratchFileObjectMetadataLookup"/> (§10.1) rather than
    /// <c>cache.TryGetObjectMetadata</c>, which is unusable before <see cref="CacheContainerWriter.Finish"/>
    /// writes a complete TOC; falls back to live ClrMD if the scratch files can't be opened, the same
    /// graceful-degradation contract as everywhere else in this pipeline.
    ///
    /// Persists <c>DominatorImmediateDominatorAddresses</c> only — the dominator child index and
    /// <c>DominatorTreeMetadata</c> rollup (§10.4's other two sections) are Batch 2b, not yet wired in.
    ///
    /// This method is only ever reached once <see cref="ReachableGraphWalker.Walk"/> has already
    /// returned successfully (its caller checks that first) — so by the time this runs,
    /// <c>reverseEdgeExtractor</c>'s data is already complete and correct regardless of anything that
    /// happens in here. A failure in this method (including
    /// <see cref="Traversal.Dominator.ChunkedBuffer{T}"/>'s <c>int</c>-overflow guard, or any other
    /// exception) is caught by the caller and only skips Stage B's persistence — it can no longer
    /// touch Stage A's already-good data (§10.8's "review the budget" fix).
    /// </summary>
    private static void BuildAndPersistDominatorTree(
        CacheContainerWriter containerWriter,
        ClrHeap heap,
        ReachableGraphWalkResult walkResult,
        IReadOnlyList<ScratchSegmentSource> scratchSegmentSources,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress)
    {
        var methodTables = new ulong[walkResult.NodeCount];
        var shallowSizes = new ulong[walkResult.NodeCount];
        var generationTags = new GenerationTag[walkResult.NodeCount];

        Stopwatch? phaseStopwatch = PerfLogDominatorStageB ? Stopwatch.StartNew() : null;
        void LogPhase(string phase)
        {
            if (phaseStopwatch is null)
                return;
            Console.Error.WriteLine($"[PERF] DominatorStageB: {phase} took {phaseStopwatch.Elapsed.TotalMilliseconds:N0} ms");
            phaseStopwatch.Restart();
        }

        var scanCounter = new ObjectScanCounter("computing exact dominator tree (resolving node metadata)", progress);
        if (ScratchFileObjectMetadataLookup.TryOpen(scratchSegmentSources, out ScratchFileObjectMetadataLookup? metadataLookup))
        {
            using (metadataLookup)
            {
                for (int id = 0; id < walkResult.NodeCount; id++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanCounter.Tick();

                    ulong address = walkResult.Addresses[id];
                    if (metadataLookup!.TryGetEntry(address, out ulong methodTable, out ulong size))
                    {
                        methodTables[id] = methodTable;
                        shallowSizes[id] = size;
                    }

                    generationTags[id] = GenerationTagResolver.Resolve(heap, address);
                }
            }
        }
        else
        {
            // Scratch files unopenable (already deleted, I/O error) — fall back to live ClrMD
            // (§10.1's documented fallback contract).
            for (int id = 0; id < walkResult.NodeCount; id++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanCounter.Tick();

                ulong address = walkResult.Addresses[id];
                ClrObject obj = heap.GetObject(address);
                if (obj.IsValid && obj.Type is not null)
                {
                    methodTables[id] = obj.Type.MethodTable;
                    shallowSizes[id] = obj.Size;
                }

                generationTags[id] = GenerationTagResolver.Resolve(heap, address);
            }
        }
        scanCounter.Complete();
        LogPhase("metadata resolution (ScratchFileObjectMetadataLookup / live-ClrMD fallback)");

        var graph = new ReachableGraph(walkResult, methodTables, shallowSizes, generationTags);
        DominatorTreeComputeResult tree = DominatorTreeComputer.Compute(graph, cancellationToken);
        LeafFoldResult fold = tree.LeafFold;
        int n = graph.NodeCount;
        LogPhase("fold + Lengauer-Tarjan (DominatorTreeComputer.Compute)");

        // §10.4 Batch 2b: each reachable node's row in the already-written, sorted
        // DominatorReachableAddresses column — computed once here and reused for both the idom
        // section below and the dominator child index, instead of each writer re-deriving its own
        // address-sorted order (DominatorTreeIndexWriter used to sort its own tuples internally;
        // it's since been simplified to trust this row order instead).
        int[] oldIdToRow = DominatorRowMapping.Compute(graph, walkResult.ReachableAddresses);
        LogPhase("row mapping (DominatorRowMapping.Compute)");

        // Reverse map: which surviving parent (new id) did each folded-away old id fold into? Built
        // once here rather than per-lookup, since every folded leaf needs its dominator address
        // resolved below (§10.4/§10.5 — a folded leaf's immediate dominator is its one real
        // predecessor, directly, with no chained resolution needed).
        var parentNewIdOfFoldedOldId = new int[n];
        Array.Fill(parentNewIdOfFoldedOldId, -1);
        for (int parentNewId = 0; parentNewId < fold.ReducedNodeCount; parentNewId++)
        {
            for (int e = fold.FoldedLeafOffsets[parentNewId]; e < fold.FoldedLeafOffsets[parentNewId + 1]; e++)
                parentNewIdOfFoldedOldId[fold.FoldedLeafOldIds[e]] = parentNewId;
        }

        var dominatorAddressesByRow = new ulong[n];
        for (int oldId = 0; oldId < n; oldId++)
        {
            int newId = fold.OldToNewId[oldId];
            ulong dominatorAddress;
            if (newId >= 0)
            {
                int dominatorNewId = tree.Idom[newId];
                dominatorAddress = dominatorNewId == tree.VirtualRoot
                    ? 0UL
                    : graph.Addresses[fold.NewToOldId[dominatorNewId]];
            }
            else
            {
                int parentNewId = parentNewIdOfFoldedOldId[oldId];
                dominatorAddress = graph.Addresses[fold.NewToOldId[parentNewId]];
            }

            dominatorAddressesByRow[oldIdToRow[oldId]] = dominatorAddress;
        }

        DominatorTreeIndexWriter.WriteImmediateDominatorAddresses(containerWriter, dominatorAddressesByRow);

        // §10.4 Batch 3: exact retained bytes per row — same "newId >= 0 ? tree.RetainedBytes[newId]
        // : shallow size" rule DominatorRetainedBytesRollup uses, so a folded leaf's retained bytes
        // (its subtree is just itself) match what the whole-tree rollup below already assumes.
        // Persisted so IDominatorTreeProvider.TryGetRetainedBytes is a binary search, not a
        // per-query subtree walk over the child index.
        var retainedBytesByRow = new ulong[n];
        for (int oldId = 0; oldId < n; oldId++)
        {
            int newId = fold.OldToNewId[oldId];
            ulong retainedBytes = newId >= 0 ? tree.RetainedBytes[newId] : graph.ShallowSizes[oldId];
            retainedBytesByRow[oldIdToRow[oldId]] = retainedBytes;
        }

        DominatorTreeIndexWriter.WriteRetainedBytes(containerWriter, retainedBytesByRow);
        LogPhase("idom + retained-bytes persistence (per-row rewrite + write)");

        // §10.4 Batch 2b: the dominator child index — same row order as above.
        DominatorChildIndexBuildResult childIndex = DominatorChildIndexBuilder.Build(graph, tree, oldIdToRow);
        DominatorChildIndexWriter.Write(containerWriter, childIndex.ChildOffsetsByRow, childIndex.ChildAddressesByRow);
        LogPhase("child-index re-keying (DominatorChildIndexBuilder.Build + write)");

        if (PerfLogDominatorStageB)
        {
            // §10.8 hub-overflow sizing: the widest single row in the dominator child index — the
            // real-dump number needed to decide whether a dominance-tree parent can have enough
            // direct children to threaten a hub-overflow scenario analogous to §8.3's reverse-edge
            // MaxParentsPerChild measurement, without adding a second pass (the CSR is already built).
            int[] offsets = childIndex.ChildOffsetsByRow;
            int widestRow = -1;
            int widestRowChildCount = 0;
            for (int row = 0; row < n; row++)
            {
                int childCount = offsets[row + 1] - offsets[row];
                if (childCount > widestRowChildCount)
                {
                    widestRowChildCount = childCount;
                    widestRow = row;
                }
            }

            ulong widestRowAddress = widestRow >= 0 ? walkResult.ReachableAddresses[widestRow] : 0UL;
            Console.Error.WriteLine($"[PERF] DominatorStageB: widest dominator child-index row has " +
                $"{widestRowChildCount:N0} direct children (address 0x{widestRowAddress:X}), " +
                $"out of {n:N0} rows / {offsets[n]:N0} total child entries");
        }

        // §10.4 Batch 2b: whole-tree total + per-MethodTable rollup, now consumed by
        // IDominatorTreeProvider (§10.6/§10.7, Batch 3) instead of DominatorAnalyzer recomputing it
        // live in Phase 2.
        DominatorRetainedBytesRollupResult rollup = DominatorRetainedBytesRollup.Compute(graph, tree);
        DominatorTreeMetadataWriter.Write(containerWriter, rollup);
        LogPhase("retained-bytes rollup (DominatorRetainedBytesRollup.Compute + write)");
    }

    /// <summary>
    /// Phase B + C for the reverse-reference index: flushes and sorts the buckets
    /// <paramref name="extractor"/> collected during the heap scan, then merges them into
    /// <paramref name="containerWriter"/>'s <c>ReverseEdgeBuckets</c>/<c>ReverseEdgeDirectories</c>/
    /// <c>ReverseEdgeMetadata</c> sections. Non-fatal like the other satellite sections above — a
    /// failure here just means <see cref="ReverseIndex.ReverseEdgeIndexReader.TryOpen"/> reports no
    /// index available later, same as any other missing/corrupt section.
    /// </summary>
    private static string? WriteReverseIndexSections(
        CacheContainerWriter containerWriter,
        string indexDir,
        int bucketCount,
        ReverseEdgeExtractor extractor,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress,
        Stopwatch stopwatch)
    {
        try
        {
            progress?.Report(new(0, "collecting reverse-index statistics", Detail: null, Elapsed: stopwatch.Elapsed));
            ReverseEdgeExtractionStats stats = extractor.GetStatistics();

            extractor.DisposeAsync(progress).AsTask().GetAwaiter().GetResult();

            var sorter = new ReverseEdgeSorter();
            sorter.SortBucketsAsync(indexDir, bucketCount, cancellationToken, progress)
                .GetAwaiter().GetResult();

            ReverseEdgeContainerWriter.Write(containerWriter, indexDir, bucketCount, stats, progress);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DeleteReverseIndexScratchFiles(indexDir, bucketCount);
            return $"ReverseIndex: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Forward-edge index Phase A→B only: flush/dispose the extractor, then sort its raw buckets
    /// into loose, directory-indexed <c>.dat</c>/<c>.idx</c> scratch files. Split out from the old
    /// single-call <c>WriteForwardIndexSections</c> so Stage A's reachability walk can run between
    /// this and <see cref="WriteForwardIndexSections"/>'s merge, reading successors from these
    /// loose files via <see cref="ForwardIndex.ForwardEdgeLooseFileReader"/> instead of a live
    /// ClrMD walk — see
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §2.
    /// </summary>
    private static (ForwardEdgeExtractionStats? Stats, string? Error) SortForwardIndexBuckets(
        string indexDir,
        int bucketCount,
        ForwardEdgeExtractor extractor,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress,
        Stopwatch stopwatch)
    {
        try
        {
            progress?.Report(new(0, "collecting forward-index statistics", Detail: null, Elapsed: stopwatch.Elapsed));
            ForwardEdgeExtractionStats stats = extractor.GetStatistics();

            extractor.DisposeAsync(progress).AsTask().GetAwaiter().GetResult();

            var sorter = new ForwardEdgeSorter();
            sorter.SortBucketsAsync(indexDir, bucketCount, cancellationToken, progress)
                .GetAwaiter().GetResult();

            return (stats, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DeleteForwardIndexScratchFiles(indexDir, bucketCount);
            return (null, $"ForwardIndex: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Forward-edge index Phase C only: merges the loose <c>.dat</c>/<c>.idx</c> files
    /// <see cref="SortForwardIndexBuckets"/> already produced into the container, then deletes
    /// them. Callers must only invoke this after a successful <see cref="SortForwardIndexBuckets"/>
    /// call — <paramref name="stats"/> is that call's output.
    /// </summary>
    private static string? WriteForwardIndexSections(
        CacheContainerWriter containerWriter,
        string indexDir,
        int bucketCount,
        ForwardEdgeExtractionStats stats,
        IProgress<AnalyzerProgressReport>? progress)
    {
        try
        {
            ForwardEdgeContainerWriter.Write(containerWriter, indexDir, bucketCount, stats, progress);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DeleteForwardIndexScratchFiles(indexDir, bucketCount);
            return $"ForwardIndex: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Streams one segment's scanned entries straight into that segment's four columnar scratch
    /// files, buffering a single fixed-size chunk per column.
    ///
    /// <para>Replaces a whole-segment <c>HeapEntry[]</c> staging buffer that held every object in the
    /// segment purely so the serialize loop could read it back afterwards — in the same order it was
    /// written, with no sort in between. That buffer was measured at <b>512 MB peak concurrent</b> on a
    /// 3.3GB dump (8 segments, DOP 4, largest single buffer 128 MB) and scaled with
    /// objects-per-segment x DOP, so it grew with dump size. Chunk buffers are a few MB per worker
    /// regardless. It also removes the pool-doubling growth path, which copied the entire accumulated
    /// buffer on each of 8 observed doublings.</para>
    ///
    /// <para>See docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 6.</para>
    /// </summary>
    private sealed class SegmentColumnWriter : IDisposable
    {
        private readonly string _addrPath, _mtPath, _sizePath, _genPath;
        private readonly int _chunkEntries;
        private readonly int _fileBufferSize;
        private readonly Action<long>? _trackBufferBytes;
        private readonly byte[] _addrBuf, _mtBuf, _sizeBuf, _genBuf;

        private FileStream? _addrStream, _mtStream, _sizeStream, _genStream;
        private int _chunkCount;
        private bool _disposed;

        /// <summary>Entries accepted so far — feeds the SegmentIndex satellite's per-segment count.</summary>
        public long EntryCount { get; private set; }

        public SegmentColumnWriter(
            string addrPath, string mtPath, string sizePath, string genPath,
            int chunkEntries, int fileBufferSize, Action<long>? trackBufferBytes = null)
        {
            _addrPath = addrPath;
            _mtPath = mtPath;
            _sizePath = sizePath;
            _genPath = genPath;
            _chunkEntries = chunkEntries;
            _fileBufferSize = fileBufferSize;
            _trackBufferBytes = trackBufferBytes;

            _addrBuf = ArrayPool<byte>.Shared.Rent(chunkEntries * ColumnSize);
            _mtBuf = ArrayPool<byte>.Shared.Rent(chunkEntries * ColumnSize);
            _sizeBuf = ArrayPool<byte>.Shared.Rent(chunkEntries * ColumnSize);
            _genBuf = ArrayPool<byte>.Shared.Rent(chunkEntries * GenColumnSize);
            _trackBufferBytes?.Invoke(BufferBytes);
        }

        private long BufferBytes => (long)_addrBuf.Length + _mtBuf.Length + _sizeBuf.Length + _genBuf.Length;

        public void Add(in HeapEntry entry)
        {
            int off = _chunkCount * ColumnSize;
            BinaryPrimitives.WriteUInt64LittleEndian(_addrBuf.AsSpan(off), entry.Address);
            BinaryPrimitives.WriteUInt64LittleEndian(_mtBuf.AsSpan(off), entry.MethodTable);
            BinaryPrimitives.WriteUInt64LittleEndian(_sizeBuf.AsSpan(off), entry.Size);
            _genBuf[_chunkCount] = unchecked((byte)entry.Generation);

            _chunkCount++;
            EntryCount++;

            if (_chunkCount == _chunkEntries)
                Flush();
        }

        /// <summary>Writes the trailing partial chunk. Must be called before disposal.</summary>
        public void Complete() => Flush();

        private void Flush()
        {
            if (_chunkCount == 0)
                return;

            // Streams are created on first flush rather than in the constructor, so a segment that
            // yields no entries still produces no scratch files — ConcatenateScratchFiles skips
            // missing per-segment files, and creating empty ones would change that contract.
            _addrStream ??= CreateStream(_addrPath);
            _mtStream ??= CreateStream(_mtPath);
            _sizeStream ??= CreateStream(_sizePath);
            _genStream ??= CreateStream(_genPath);

            _addrStream.Write(_addrBuf, 0, _chunkCount * ColumnSize);
            _mtStream.Write(_mtBuf, 0, _chunkCount * ColumnSize);
            _sizeStream.Write(_sizeBuf, 0, _chunkCount * ColumnSize);
            _genStream.Write(_genBuf, 0, _chunkCount * GenColumnSize);

            _chunkCount = 0;
        }

        private FileStream CreateStream(string path) => new(
            path, FileMode.Create, FileAccess.Write, FileShare.None, _fileBufferSize, FileOptions.SequentialScan);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _addrStream?.Dispose();
            _mtStream?.Dispose();
            _sizeStream?.Dispose();
            _genStream?.Dispose();

            _trackBufferBytes?.Invoke(-BufferBytes);
            ArrayPool<byte>.Shared.Return(_addrBuf);
            ArrayPool<byte>.Shared.Return(_mtBuf);
            ArrayPool<byte>.Shared.Return(_sizeBuf);
            ArrayPool<byte>.Shared.Return(_genBuf);
        }
    }

    // ── Type classification helpers ────────────────────────────────────────────

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

            if (TaskTypeNamePattern.IsTaskType(name))
                flags |= TypeAggregateFlags.IsTaskType;
        }

        if (type.IsArray)
            flags |= TypeAggregateFlags.IsArrayType;

        if (type.IsFinalizable)
            flags |= TypeAggregateFlags.IsFinalizableType;

        if (IsDelegateType(type))
            flags |= TypeAggregateFlags.IsDelegateType;

        if (IsAsyncStateMachineType(type))
            flags |= TypeAggregateFlags.IsAsyncStateMachineType;

        return flags;
    }

    private static bool IsDelegateType(ClrType type)
    {
        // Walk up to 4 levels of BaseType to find MulticastDelegate or Delegate.
        ClrType? current = type.BaseType;
        for (int depth = 0; depth < 4 && current is not null; depth++)
        {
            string? baseName = current.Name;
            if (baseName is "System.MulticastDelegate" or "System.Delegate")
                return true;
            current = current.BaseType;
        }
        return false;
    }
    private static bool IsAsyncStateMachineType(ClrType type)
    {
        // Check if type name matches async state machine pattern: <MethodName>d__N
        // (optionally followed by CLR-appended generic type parameters).
        string? name = type.Name;
        if (name is null || !AsyncStateMachineNamePattern.Regex.IsMatch(name))
            return false;

        // Confirm it implements IAsyncStateMachine interface
        foreach (ClrInterface iface in type.EnumerateInterfaces())
        {
            if (iface.Name is "System.Runtime.CompilerServices.IAsyncStateMachine")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Computes a type's <see cref="TypeShapeEntry"/> (ref/value field counts) and the indices
    /// (into <c>type.Fields</c>, matching <see cref="StringAnalyzer"/>'s FieldLayoutCache ordering)
    /// of its <c>System.String</c> instance fields in a single walk of <c>type.Fields</c> — these
    /// were previously two separate loops over the same field list. Empty array (never null) when
    /// the type has no string fields, so the caller's <c>Length &gt; 0</c> check decides whether to
    /// add a sparse-dictionary entry. Both run at most once per unique MethodTable.
    /// </summary>
    /// <remarks>
    /// PERF: uses <see cref="ClrInstanceField.ElementType"/> — a tag read directly off the field's
    /// metadata signature — instead of <c>field.Type?.Name</c>. <c>field.Type</c> forces full
    /// <see cref="ClrType"/> resolution, which is far more expensive and, under this method's
    /// <see cref="Parallel"/>.For segment-worker caller, serializes on ClrMD's internal
    /// metadata-resolution locking badly enough to turn a ~20s scan into 5+ minutes (measured).
    /// Same pattern applied at every other per-field type check touched in this optimization pass:
    /// <see cref="StringAnalyzer"/>'s owner-type lazy fallback and
    /// <c>ScanForStringOwnerTypesFallback</c>, and <c>CollectionAnalyzer.GetOrBuildFieldLayout</c>'s
    /// field-name fallback loop.
    /// </remarks>
    private static (TypeShapeEntry Shape, int[] StringFieldIndices) ComputeTypeShapeAndStringFields(ClrType type)
    {
        short refFields = 0;
        short valFields = 0;
        List<int>? stringIndices = null;

        int i = 0;
        foreach (ClrInstanceField field in type.Fields)
        {
            if (field.IsObjectReference)
                refFields++;
            else
                valFields++;

            if (field.ElementType == ClrElementType.String)
            {
                stringIndices ??= new List<int>(capacity: 4);
                stringIndices.Add(i);
            }
            i++;
        }

        return (new TypeShapeEntry(refFields, valFields), stringIndices?.ToArray() ?? []);
    }

    // ── Index cache fast-path ──────────────────────────────────────────────────

    /// <summary>
    /// Attempts to skip the full heap scan by loading a previous build's <c>cache.bin</c>
    /// <c>Objects</c> + <c>TypeAggregates</c> sections. Returns <c>true</c> and populates
    /// <paramref name="result"/> on success.
    /// </summary>
    private static bool TryLoadFromCache(
        string containerPath,
        string dumpPath,
        out HeapIndexBuildResult? result)
    {
        result = null;
        if (!CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            return false;

        // Cheapest gate first: a sampled content hash mismatch means the dump was replaced,
        // so there's no point parsing any section.
        if (!reader.MatchesDumpContent(dumpPath))
            return false;

        // ObjectAddresses' RecordCount (from the TOC) is authoritative — no per-section
        // header to read, unlike the pre-columnar format.
        if (!reader.TryGetSectionInfo(CacheSectionId.ObjectAddresses, out CacheTocEntry objEntry) || objEntry.RecordCount <= 0)
            return false;

        return TypeAggregateIndexReader.TryLoad(reader, containerPath, objEntry.RecordCount, out result);
    }

    private static void DeleteScratchFiles(string[] files)
    {
        for (int i = 0; i < files.Length; i++)
        {
            try { File.Delete(files[i]); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Best-effort cleanup of reverse-index bucket scratch files (<c>.tmp</c>/<c>.dat</c>/<c>.idx</c>) after a failed or abandoned build — mirrors <see cref="DeleteScratchFiles"/> for the segment scratch files.</summary>
    private static void DeleteReverseIndexScratchFiles(string indexDir, int bucketCount)
    {
        for (int i = 0; i < bucketCount; i++)
        {
            try { File.Delete(Path.Combine(indexDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.TemporaryScratchSuffix}")); } catch { /* best-effort */ }
            try { File.Delete(Path.Combine(indexDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.SortedDataSuffix}")); } catch { /* best-effort */ }
            try { File.Delete(Path.Combine(indexDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.DirectorySuffix}")); } catch { /* best-effort */ }
        }
    }

    private static void DeleteForwardIndexScratchFiles(string indexDir, int bucketCount)
    {
        for (int i = 0; i < bucketCount; i++)
        {
            try { File.Delete(Path.Combine(indexDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.TemporaryScratchSuffix}")); } catch { /* best-effort */ }
            try { File.Delete(Path.Combine(indexDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.SortedDataSuffix}")); } catch { /* best-effort */ }
            try { File.Delete(Path.Combine(indexDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.DirectorySuffix}")); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Streams each scratch file in <paramref name="files"/> into <paramref name="stream"/> in
    /// order, deleting each as it's consumed. Used to assemble a single columnar section from
    /// per-segment scratch files without materializing the whole column in memory.
    /// </summary>
    /// <summary>
    /// Concatenates <paramref name="files"/> onto <paramref name="stream"/>, hashing the bytes as
    /// they're copied so the caller can close the section via
    /// <see cref="CacheContainerWriter.EndSection(long, uint)"/> without a separate full re-read —
    /// these columnar sections have no placeholder-header-patched-afterward step, so an inline hash
    /// is safe and, at up to a few GB per section on large dumps, avoids doubling that section's I/O.
    /// </summary>
    /// <param name="deleteAfterCopy">
    /// §10.1/§10.4 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): pass
    /// <c>false</c> for the Address/MethodTable/Size columns when Stage B wants these files kept
    /// around for <see cref="ScratchFileObjectMetadataLookup"/> — the caller becomes responsible for
    /// deleting them once Stage B's metadata resolution finishes.
    /// </param>
    private static uint ConcatenateScratchFiles(Stream stream, string[] files, int bufferSize, bool deleteAfterCopy = true)
    {
        var hasher = new XxHash32();
        byte[] copyBuf = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                string segFile = files[i];
                if (!File.Exists(segFile))
                    continue;
                using (FileStream segStream = new(segFile, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: bufferSize, FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = segStream.Read(copyBuf, 0, copyBuf.Length)) > 0)
                    {
                        stream.Write(copyBuf, 0, read);
                        hasher.Append(copyBuf.AsSpan(0, read));
                    }
                }
                if (deleteAfterCopy)
                    File.Delete(segFile);
            }
            stream.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuf);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    // ── Generation helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="GCSegmentKind"/> to a generation number (0/1/2), or -1 when the
    /// generation cannot be determined from the segment kind alone (Ephemeral segments in
    /// workstation GC contain mixed Gen0/1/2 objects). LOH/POH/FOH return -1 because
    /// they are already tracked separately by the size threshold in TypeIndexBuilder.
    /// </summary>
    private static int SegmentKindToGeneration(GCSegmentKind kind) => kind switch
    {
        GCSegmentKind.Generation0 => 0,
        GCSegmentKind.Generation1 => 1,
        GCSegmentKind.Generation2 => 2,
        _ => -1, // Ephemeral (workstation GC), LOH, POH — resolved per-object at call site
    };

    // Used when segGen < 0 (Ephemeral segment): asks ClrMD which generation the object belongs to.
    private static int ResolveObjectGeneration(ClrSegment segment, ulong address)
    {
        try { return (int)segment.GetGeneration(address); }
        catch { return -1; }
    }

}
