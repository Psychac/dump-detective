using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Platform;
using DumpDetective.Platform.Storage.Columns;
using DumpDetective.Platform.Storage.Container;
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

    // CacheContainerWriter lives in DumpDetective.Platform, which cannot reference
    // DumpDetective.Core (zero deps beyond Sdk — see docs/refactor/modularity/phase-1-contracts-sdk.md),
    // so it reports through the source-agnostic IndexProgress shape instead of
    // Core.Abstractions.AnalyzerProgressReport. This adapts the one direction this file needs.
    private static IProgress<IndexProgress>? WrapForContainerProgress(IProgress<AnalyzerProgressReport>? progress) =>
        progress is null
            ? null
            : new Progress<IndexProgress>(p => progress.Report(new AnalyzerProgressReport(p.ScannedCount, p.Phase, p.Detail, p.Elapsed)));

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
        // String dedup index built while dump pages are hot — zero extra I/O cost. Unbounded:
        // measured (§11.4 M5) not to bind at 321K unique strings on a 3.35GB real dump; the
        // per-analyzer StringAnalysisOptions.MaxUniqueStringTracking is the surviving guard for
        // the multi-million-unique-string extreme this hasn't been validated against yet.
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
        // Stage A is unconditional now: the row-keyed walk emits the reverse CSR itself, so there is
        // no ReverseEdgeExtractor and no hash-partitioned bucket set to flush, sort and read back.
        const bool buildStageA = true;

        // Forward-reference index (§D5): extracted in the per-object foreach below that enumerates
        // obj.EnumerateReferences(carefully: true), keyed by parent. Reuses the reverse index's
        // bucket-count formula (dump-size-based, not edge-count-based, so it applies equally well
        // here) even though the two indices are no longer built from the same pass.
        int forwardIndexBucketCount = ForwardIndexConstants.CalculateBucketCount(new FileInfo(dumpPath).Length);
        var forwardEdgeExtractor = new ForwardEdgeExtractor(forwardIndexBucketCount, indexDir);

        // §10.3 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): Stage B only
        // ever runs on top of Stage A actually running (reverseEdgeExtractor is not null is Stage A's
        // own existing gate, §7) — a narrower, already-shipped-code-grounded version of §3's
        // `canBuildReachableGraph` term. Deliberately does NOT gate Stage A's own construction above
        // on `IRequiresReachableGraphIndex` — that would change already-shipped Stage A's behavior,
        // which is out of scope here (see §10.3's note on this).
        bool buildStageB =
            buildStageA
            && enableExactDominatorTree
            && (activeAnalyzers?.Any(a => a is IRequiresDominatorTreeIndex) ?? false);

        // heap.Segments is lazily resolved by ClrMD on first access — reported separately from
        // "preparing heap scan" above so a profiling run can attribute time to this specific
        // DAC-side segment-list resolution rather than lumping it in with the setup around it.
        progress?.Report(new(0, "enumerating heap segments", Detail: null, Elapsed: stopwatch.Elapsed));
        ClrSegment[] segments = heap.Segments.ToArray();

        // Ascending Start order makes the persisted ObjectAddresses column globally monotonic by
        // construction rather than by observation. Every downstream structure iterates this array
        // in the same order — scratch-file assignment, the parallel scan, column concatenation,
        // SegmentIndex's cumulative record offsets and ScratchSegmentSource — so sorting once here
        // keeps all of them coherent.
        //
        // Combined with SegmentAddressContiguityDiscrepancyTests' two invariants (each segment
        // yields objects in strictly increasing address order, and segments do not overlap), this
        // makes `row -> address` monotonic, which is what turns `address -> row` into a rank query
        // over ObjectAddressBlockBases — see docs/cache/cache-ideal-design.md §3.1 R1.
        //
        // Measured to change nothing on either reference dump: ClrMD already returns segments in
        // ascending Start order there, and the decoded column was verified strictly ascending
        // element-by-element on both (§7.2). This removes the dependency on that happening to hold.
        Array.Sort(segments, static (a, b) => a.Start.CompareTo(b.Start));
        string[] segAddrScratchFiles = new string[segments.Length];
        string[] segMtScratchFiles = new string[segments.Length];
        string[] segSizeScratchFiles = new string[segments.Length];
        string[] segGenScratchFiles = new string[segments.Length];
        // SegmentIndex satellite (docs/cache/cache-architecture.md): each worker writes its
        // own segIdx slot exactly once below, so no lock is needed despite the parallel scan.
        long[] segRecordCounts = new long[segments.Length];
        // Same per-slot ownership: the ObjectSizes width can only be chosen once the whole heap's
        // size distribution is known, and the container write that applies it is a streaming pass.
        long[] segSizeEscapesAtTwoBytes = new long[segments.Length];
        long[] segSizeEscapesAtFourBytes = new long[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            segAddrScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.addr.tmp");
            segMtScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.mt.tmp");
            segSizeScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.size.tmp");
            segGenScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.gen.tmp");
        }

        using var containerWriter = new CacheContainerWriter(containerPath, dumpPath, WrapForContainerProgress(progress));
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
                    if ((flags & TypeAggregateFlags.IsStringType) != 0)
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
                segSizeEscapesAtTwoBytes[segIdx] = columnWriter.SizeEscapesAtTwoBytes;
                segSizeEscapesAtFourBytes[segIdx] = columnWriter.SizeEscapesAtFourBytes;

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
                        else
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

        // Built here rather than at the TypeAggregates write below, because the MethodTable column
        // needs the distinct-type set to assign TypeIds. masterBuilder has been fully merged since
        // the parallel scan joined above, and the result is reused for TypeAggregates and
        // HeapIndexBuildResult, so Build() still runs exactly once.
        var typeAggregates = masterBuilder.Build();

        // Concatenate the per-segment scratch files into the three columnar sections, one
        // column at a time, in segment order — this is what makes disk-mode entry order
        // deterministic and match memory-mode's segment-ordered output, and keeps each
        // column contiguous in the container so a reader that only needs MethodTable (type
        // aggregation) or Size (histograms) doesn't pay to read Address too.
        // §10.1/§10.4: when Stage B wants them, the Address/MethodTable/Size scratch files are kept
        // on disk past this point for ScratchFileObjectMetadataLookup — deleted explicitly once
        // Stage B's metadata resolution finishes (see the reverseEdgeExtractor block below), instead
        // of here.
        // §10.3: addresses are stored as a 4-byte delta from a per-block base. Unlike the size
        // column below there is no width to choose — anything that doesn't fit escapes, which was
        // 16 records of 14.6M on the reference dump and none of 87.1M on the 27.5 GB one.
        List<ulong> addressBlockBases = new(BlockDeltaColumn.BlockCountFor(objectCount));
        List<(uint RecordIndex, ulong Value)> addressOverflow = [];

        containerWriter.BeginSection(CacheSectionId.ObjectAddresses);
        // deleteAfterCopy: false for all three columns since R2/R3, on both the Stage-B and
        // Stage-A-only paths. ScratchFileObjectMetadataLookup opens the address/MethodTable/Size
        // triple together and is what resolves address -> object row for the walk, so all three have
        // to outlive the walk, not just Stage B's metadata resolution. Deleted after the walk block.
        uint addrChecksum = BlockDeltaScratchFiles(
            stream, segAddrScratchFiles, addressBlockBases, addressOverflow, writeBuffer, deleteAfterCopy: false);
        containerWriter.EndSection(objectCount, addrChecksum);

        containerWriter.BeginSection(CacheSectionId.ObjectAddressBlockBases);
        uint addressBasesChecksum = BlockDeltaColumn.WriteBlockBases(stream, addressBlockBases, writeBuffer);
        containerWriter.EndSection(addressBlockBases.Count, addressBasesChecksum);

        containerWriter.BeginSection(CacheSectionId.ObjectAddressOverflow);
        uint addressOverflowChecksum = ColumnOverflowTable.Write(stream, addressOverflow, writeBuffer);
        containerWriter.EndSection(addressOverflow.Count, addressOverflowChecksum);

        // §3: MethodTable is stored as a narrow TypeId indexing ObjectTypeDictionary, not as the
        // full 8-byte pointer. 14,003 distinct types cover 14.6M objects on the reference dump, so
        // this column drops 111.5 MiB -> 27.9 MiB.
        var methodTableDictionary = new ulong[typeAggregates.Count];
        int dictCursor = 0;
        foreach (ulong methodTable in typeAggregates.Keys)
            methodTableDictionary[dictCursor++] = methodTable;
        Array.Sort(methodTableDictionary);

        var typeIdByMethodTable = new Dictionary<ulong, int>(methodTableDictionary.Length);
        for (int i = 0; i < methodTableDictionary.Length; i++)
            typeIdByMethodTable[methodTableDictionary[i]] = i;

        int typeIdWidth = methodTableDictionary.Length <= ushort.MaxValue ? sizeof(ushort) : sizeof(uint);

        containerWriter.BeginSection(CacheSectionId.ObjectTypeDictionary);
        uint dictChecksum = WriteMethodTableDictionary(stream, methodTableDictionary, writeBuffer);
        containerWriter.EndSection(methodTableDictionary.Length, dictChecksum);

        containerWriter.BeginSection(CacheSectionId.ObjectMethodTables);
        uint mtChecksum = ConvertMethodTablesToTypeIds(
            stream, segMtScratchFiles, typeIdByMethodTable, typeIdWidth, writeBuffer,
            deleteAfterCopy: false);
        containerWriter.EndSection(objectCount, mtChecksum);

        // §10.2: an object size never came close to needing 8 bytes on any real dump measured
        // (23.3 MB maximum across a 3.5 GB and a 27.5 GB dump), so the column is narrowed to the
        // width the distribution allows and the few values that don't fit move to a side table.
        long sizeEscapesAtTwoBytes = 0;
        long sizeEscapesAtFourBytes = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            sizeEscapesAtTwoBytes += segSizeEscapesAtTwoBytes[i];
            sizeEscapesAtFourBytes += segSizeEscapesAtFourBytes[i];
        }

        int sizeWidth = NarrowColumnWidth.Choose(objectCount, sizeEscapesAtTwoBytes, sizeEscapesAtFourBytes);
        List<(uint RecordIndex, ulong Value)> sizeOverflow = [];

        containerWriter.BeginSection(CacheSectionId.ObjectSizes);
        uint sizeChecksum = sizeWidth == NarrowColumnWidth.Full
            ? ConcatenateScratchFiles(stream, segSizeScratchFiles, writeBuffer, deleteAfterCopy: false)
            : NarrowScratchFiles(stream, segSizeScratchFiles, sizeWidth, sizeOverflow, writeBuffer, deleteAfterCopy: false);
        containerWriter.EndSection(objectCount, sizeChecksum);

        if (sizeWidth != NarrowColumnWidth.Full)
        {
            containerWriter.BeginSection(CacheSectionId.ObjectSizeOverflow);
            uint sizeOverflowChecksum = ColumnOverflowTable.Write(stream, sizeOverflow, writeBuffer);
            containerWriter.EndSection(sizeOverflow.Count, sizeOverflowChecksum);
        }

        // Generation is run-length encoded instead of stored per object: it is piecewise-constant
        // over the concatenated table, measuring 13 runs over 14.6M objects and 50 over 87.1M, so
        // the byte column was 83.07 MiB carrying ~600 bytes of information (O2, §3.2 of
        // docs/cache/cache-ideal-design.md). The runs come from the same per-object bytes the column
        // used to hold, read back in the same concatenation order — not re-derived from segment
        // metadata, which would mean reimplementing ClrMD's own Ephemeral/LOH generation rules.
        List<(long FirstRecordIndex, sbyte Generation)> generationRuns =
            BuildGenerationRuns(segGenScratchFiles, writeBuffer);
        containerWriter.BeginSection(CacheSectionId.ObjectGenerationRuns);
        uint genChecksum = ObjectGenerationRunTable.Write(stream, generationRuns);
        MarkAlloc("columnar scratch concatenation");
        containerWriter.EndSection(generationRuns.Count, genChecksum);

        // §10.1/§10.4: build the (SegmentIndexEntry, scratch-file-paths) triples
        // ScratchFileObjectMetadataLookup needs, mirroring the SegmentIndex satellite's own
        // Start/End/FirstRecordIndex/RecordCount loop below — built here, before that satellite
        // write, since Stage B needs it whether or not the SegmentIndex write below succeeds.
        // Built unconditionally since R2/R3: the row-keyed walk resolves address -> object row
        // through these on both the Stage-B and Stage-A-only paths, not just Stage B's.
        List<ScratchSegmentSource> scratchSegmentSources;
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
        if (buildStageA)
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
            // Only real heap objects may seed the walk. Conservative stack scanning yields a
            // handful of tagged or garbage pointers as root targets — 5 of 6,686,490 on the 3.3 GB
            // dump and 4 of 58,339,936 on the 27.5 GB one, always outside the heap's address range
            // (0xffffff, 0x1000007ffa899b53, ...). They used to enter the reachable set and occupy
            // real rows in DominatorReachableAddresses, which could hold any address at all.
            //
            // Since format v10 the reachable row space is a bitmap over *object rows*
            // (docs/cache/cache-ideal-design.md §3.1, R1), which structurally cannot represent an
            // address that is not a live object. Admitting them would leave the bitmap 5 bits short
            // of the row count every row-aligned column downstream was written with — idom rows,
            // retained bytes and the reverse CSR — and those readers correctly refuse to open on a
            // length mismatch, silently dropping every analyzer back to a live heap walk.
            //
            // Filtering here rather than after the walk keeps one row space for everything, and is
            // the correct behaviour independently: an address that is not an object is not reachable.
            var walkRootAddresses = new List<ulong>(4096);
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                ulong rootObjectAddress = root.Object.Address;
                if (rootObjectAddress == 0)
                    continue;

                ClrObject rootObject = heap.GetObject(rootObjectAddress);
                if (rootObject.IsValid && rootObject.Type is not null)
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
            RowKeyedWalkResult? rowWalk = null;
            Stopwatch? walkStopwatch = PerfLogDominatorStageB ? Stopwatch.StartNew() : null;
            try
            {
                try
                {
                    // R2/R3: object rows are the walk's identity, so there is no
                    // Dictionary<ulong,int> (2,325.9 MB at 87.1M objects), no id->address array and
                    // no per-edge ChunkedBuffer. buildCsr is unconditional because the reverse index
                    // now comes from this CSR rather than from a separately extracted bucket set —
                    // which is what removes ReverseEdgeExtractor's 2.19 GB scratch round-trip and
                    // resolves Part F §F.2's "the no-Stage-B path still needs the reverse index".
                    if (!ScratchFileObjectMetadataLookup.TryOpen(scratchSegmentSources, out ScratchFileObjectMetadataLookup? rowResolver)
                        || rowResolver is null)
                    {
                        throw new InvalidOperationException(
                            "object row resolver unavailable: the per-segment address scratch files could not be opened");
                    }

                    using (rowResolver)
                    {
                        rowWalk = RowKeyedGraphWalker.Walk(
                            walkRootAddresses, walkSuccessors, rowResolver, objectCount,
                            buildCsr: true, cancellationToken, progress);
                    }

                    // Adapted at the boundary so Stage B is untouched. Addresses already ascend, so
                    // they *are* the sorted set the previous result exposed separately.
                    walkResult = new ReachableGraphWalkResult(
                        nodeCount: rowWalk.NodeCount,
                        edgeCount: rowWalk.EdgeCount,
                        addresses: rowWalk.Addresses,
                        reachableAddresses: rowWalk.Addresses,
                        outDegree: rowWalk.OutDegree,
                        inDegree: rowWalk.InDegree,
                        isRoot: rowWalk.IsRoot,
                        fwdOffsets: rowWalk.FwdOffsets,
                        fwdTargets: rowWalk.FwdTargets,
                        revOffsets: rowWalk.RevOffsets,
                        revTargets: rowWalk.RevTargets);

                    // rowWalk stays reachable as this method's local for the rest of the build (its
                    // RevOffsets/RevTargets/VisitedBitmap are read again below), so its own copy of
                    // the forward-CSR reference must be dropped explicitly — walkResult now owns the
                    // arrays going forward. See RowKeyedWalkResult.ReleaseForwardEdgeArrays.
                    rowWalk.ReleaseForwardEdgeArrays();
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


                // All three columns are kept until after the walk now, so all three are cleaned up
                // here whether or not Stage B was gated on.
                DeleteScratchFiles(segAddrScratchFiles);
                DeleteScratchFiles(segMtScratchFiles);
                DeleteScratchFiles(segSizeScratchFiles);
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
                // which objects the walk reached, so "is this object reachable?" is answerable from
                // disk without re-running it. Since format v10 that is one bit per object row rather
                // than a second sorted copy of every reachable address — 222.99 MiB to 10.70 MiB on
                // the 27.5 GB dump (docs/cache/cache-ideal-design.md §3.1, R1). Rows come from a
                // sequential merge-join against the address scratch, since both sides ascend.
                try
                {
                    // Straight from the walk: the bitmap *is* its visited set, so there is nothing
                    // to merge-join and no chance of the population disagreeing with NodeCount —
                    // the mismatch that made R1's first attempt drop every reader to a live walk.
                    containerWriter.BeginSection(CacheSectionId.ReachableRowBitmap);
                    uint bitmapChecksum = ReachableRowBitmap.Write(
                        containerWriter.Stream, rowWalk!.VisitedBitmap, rowWalk.VisitedBitmap.LongLength);
                    containerWriter.EndSection(objectCount, bitmapChecksum);

                    long matchedRows = rowWalk.NodeCount;

                    // Every reachable address must map to an object row, or the bitmap's population
                    // disagrees with the row count that idom rows, retained bytes and the reverse
                    // CSR are all written with — and those readers refuse to open on a length
                    // mismatch, dropping every analyzer to a live heap walk without saying why.
                    // Root seeding above filters the only known source of unmatched addresses; if
                    // one still appears, fail this section loudly rather than emit a container whose
                    // readers silently all decline.
                    if (matchedRows != walkResult.ReachableAddresses.Length)
                    {
                        throw new InvalidOperationException(
                            $"bitmap population {matchedRows:N0} disagrees with the {walkResult.ReachableAddresses.Length:N0} " +
                            "row-aligned entries the dominator and reverse-edge columns are written with");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    satelliteWarnings.Add($"ReachableRowBitmap: {ex.GetType().Name}: {ex.Message}");
                }

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
                }

                // Cleaned up here rather than in Stage B's finally: since R2/R3 all three columns
                // are read by the walk's row resolver on both paths, so Stage B is no longer the
                // last reader of any of them.
                DeleteScratchFiles(segAddrScratchFiles);
                DeleteScratchFiles(segMtScratchFiles);
                DeleteScratchFiles(segSizeScratchFiles);

                MarkAlloc("reverse index (CSR build + write)");
                string? reverseIndexWarning = WriteReverseIndexSections(containerWriter, rowWalk!, progress);
                if (reverseIndexWarning is not null)
                    satelliteWarnings.Add(reverseIndexWarning);
            }
        }

        // Belt and braces: if the walk block above was skipped or bailed before its own cleanup,
        // these would otherwise leak into the index directory. DeleteScratchFiles is idempotent.
        DeleteScratchFiles(segAddrScratchFiles);
        DeleteScratchFiles(segMtScratchFiles);
        DeleteScratchFiles(segSizeScratchFiles);

        // Forward-reference index: the loose files Phase B sorted have now served their only
        // consumer — Stage A's reachability walk above — so they are deleted here.
        //
        // They are deliberately NOT merged into the container. That merge used to run as "Phase C"
        // and wrote ForwardEdgeBuckets/ForwardEdgeDirectories/ForwardEdgeMetadata, which measured
        // 462.4 MiB (33.1% of cache.bin) on a 3.3 GB dump and 3,087.8 MiB (32.8%) on a 27.5 GB one
        // — and which nothing ever read: `IHeapAnalysisCache.TryGetForwardIndexProvider()` has no
        // production callers, confirmed both by whole-repo search and by a run-time section-touch
        // trace (docs/cache/cache-redesign-measurements.md § 9).
        //
        // The reader, provider and interface method are intentionally left in place. If a
        // cache-hit-time forward-reference consumer is ever added it will need this section (a cache
        // hit skips Phase 1 entirely, so no loose files exist then) — at which point restoring the
        // merge is one `ForwardEdgeContainerWriter.Write` call here, plus a rebuild, which such a
        // consumer would require regardless. ForwardEdgeContainerWriter itself stays in place
        // and stays covered by ForwardEdgeIndexTests, so the capability is intact.
        if (forwardIndexStats is not null)
            DeleteForwardIndexScratchFiles(indexDir, forwardIndexBucketCount);

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
        containerWriter.TryWriteSection(CacheSectionId.Handles, "enumerating GC handles",
            stream => HandleSnapshotWriter.Write(stream, heap.Runtime, cancellationToken, progress, stopwatch),
            warnings, WrapForContainerProgress(progress), stopwatch);

        // Roots — GC root enumeration (can be slow on large dumps; progress reported every 50k roots)
        containerWriter.TryWriteSection(CacheSectionId.Roots, "enumerating GC roots",
            stream => RootIndexWriter.Write(stream, heap, cancellationToken, progress, stopwatch),
            warnings, WrapForContainerProgress(progress), stopwatch);

        // RootStackThreadAttribution — §12.2 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
        // which thread owns each Stack-kind root. Same gate as Roots (a ClrRoot alone carries no
        // thread identity, so this is only useful alongside the Roots section it cross-references
        // against at read time) — cheap relative to the rest of Phase 1, unconditional whenever
        // Roots itself builds, no separate opt-in.
        containerWriter.TryWriteSection(CacheSectionId.RootStackThreadAttribution,
            "enumerating stack root thread ownership",
            stream => RootStackThreadIndexWriter.Write(stream, heap, cancellationToken, progress, stopwatch),
            warnings, WrapForContainerProgress(progress), stopwatch);

        // Tasks — Task objects collected during heap scan
        containerWriter.TryWriteSection(CacheSectionId.Tasks, "writing Tasks section",
            stream =>
            {
                using (TaskIndexWriter tw = new(stream))
                {
                    foreach ((ulong addr, ulong mt, int stateFlags) in taskCandidates)
                        tw.Add(addr, mt, stateFlags); // read during Phase 1 scan; 0 falls back to Phase 2 re-read
                    tw.Flush();
                }
                return taskCandidates.Count;
            },
            warnings, WrapForContainerProgress(progress), stopwatch);

        // LargeObjects — top-100 LOH objects by size
        var tracker = new LargeObjectTracker();
        foreach ((ulong addr, ulong mt, ulong size) in largeCandidates)
            tracker.Consider(addr, mt, size);
        containerWriter.TryWriteSection(CacheSectionId.LargeObjects, "writing LargeObjects section",
            stream => { tracker.Write(stream); return largeCandidates.Count; },
            warnings, WrapForContainerProgress(progress), stopwatch);

        // LohFreeBlocks — free block gaps already collected during the main scan;
        // no second segment walk required.
        containerWriter.TryWriteSection(CacheSectionId.LohFreeBlocks, "writing LohFreeBlocks section",
            stream => LohFreeBlockWriter.WriteFromCandidates(stream, lohFreeBlockCandidates, cancellationToken),
            warnings, WrapForContainerProgress(progress), stopwatch);

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
                // §10.8: sequential merge across all segments instead of one random-access binary
                // search per node — see ResolveBatch's doc comment for the measured rationale.
                // walkResult.Addresses is RowKeyedGraphWalker.BuildCsr's row-ordered (hence
                // address-ordered) node ids, so the sorted-input fast path applies: no O(N log N)
                // sort, no ~700 MB sort/order-tracking scratch at N=58.3M.
                metadataLookup!.ResolveBatch(
                    walkResult.Addresses, methodTables, shallowSizes, cancellationToken,
                    assumeAscendingAddresses: true);

                for (int id = 0; id < walkResult.NodeCount; id++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanCounter.Tick();

                    generationTags[id] = GenerationTagResolver.Resolve(heap, walkResult.Addresses[id]);
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

        // walkResult (this method's parameter) aliases the same forward-CSR arrays graph just took
        // — it stays reachable for the rest of this method's stack frame regardless of what graph's
        // own release later clears, so without this that release frees nothing. See
        // ReachableGraphWalkResult.ReleaseForwardEdgeArrays.
        walkResult.ReleaseForwardEdgeArrays();

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

        // §4's aggressive option (docs/cache/cache-format-clean-slate-redesign.md): a row index into
        // this same DominatorReachableAddresses ordering, not an address — what makes
        // DominatorChildIndexReader's in-memory inversion possible without a search per row. A
        // node's own row is already known (oldIdToRow[oldId]); its *dominator's* row is the same
        // lookup applied to the dominator's old id, so no address round-trip is needed either
        // direction.
        var dominatorRowByRow = new uint[n];
        for (int oldId = 0; oldId < n; oldId++)
        {
            int newId = fold.OldToNewId[oldId];
            uint dominatorRow;
            if (newId >= 0)
            {
                int dominatorNewId = tree.Idom[newId];
                dominatorRow = dominatorNewId == tree.VirtualRoot
                    ? DominatorRowIndex.NoParentRow
                    : (uint)oldIdToRow[fold.NewToOldId[dominatorNewId]];
            }
            else
            {
                int parentNewId = parentNewIdOfFoldedOldId[oldId];
                dominatorRow = (uint)oldIdToRow[fold.NewToOldId[parentNewId]];
            }

            dominatorRowByRow[oldIdToRow[oldId]] = dominatorRow;
        }

        DominatorTreeIndexWriter.WriteImmediateDominatorRows(containerWriter, dominatorRowByRow);

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

        // §4's aggressive option: no dominator child index is written any more.
        // DominatorChildIndexReader derives the child direction on demand by inverting the idom rows
        // written above. The hub-overflow sizing this write-time diagnostic block used to produce is
        // already recorded (docs/cache/cache-redesign-measurements.md, no capping needed) rather than
        // re-instrumented against a structure this writer no longer builds.

        // §10.4 Batch 2b: whole-tree total + per-MethodTable rollup, now consumed by
        // IDominatorTreeProvider (§10.6/§10.7, Batch 3) instead of DominatorAnalyzer recomputing it
        // live in Phase 2.
        DominatorRetainedBytesRollupResult rollup = DominatorRetainedBytesRollup.Compute(graph, tree);
        DominatorTreeMetadataWriter.Write(containerWriter, rollup);
        LogPhase("retained-bytes rollup (DominatorRetainedBytesRollup.Compute + write)");
    }

    /// <summary>
    /// Phase B + C for the reverse-reference index: resolves the buckets <paramref name="extractor"/>
    /// collected during the heap scan into true CSR (docs/cache/cache-format-clean-slate-redesign.md
    /// §2) against <paramref name="sortedReachableAddresses"/> — the same reachable-address set
    /// <see cref="Dominator.DominatorReachableAddressWriter"/> just persisted — then writes the
    /// resulting <c>ReverseEdgeOffsets</c>/<c>ReverseEdgeChildren</c> sections. Non-fatal like the
    /// other satellite sections above — a failure here just means
    /// <see cref="ReverseIndex.ReverseEdgeIndexReader.TryOpen"/> reports no index available later,
    /// same as any other missing/corrupt section.
    /// </summary>
    /// <summary>
    /// Writes the reverse CSR the row-keyed walk already produced.
    /// </summary>
    /// <remarks>
    /// Until R2/R3 this flushed <c>ReverseEdgeExtractor</c>'s hash-partitioned buckets to disk
    /// (2.19 GB on the 27.5 GB dump), read them back, and re-resolved both endpoints of every edge
    /// with two binary searches per edge to rebuild a structure the walk had already built in
    /// memory — 39.6 s and 2.62 GB resident (§2.2, Part B.1/B.2/B.3). The walk now emits the CSR
    /// directly in reachable-row space, so this is a buffered write of two arrays.
    ///
    /// It also serves the no-Stage-B path, which is what Part F §F.2 said made the earlier C.2
    /// framing impossible: the CSR no longer depends on Stage B being gated on.
    /// </remarks>
    private static string? WriteReverseIndexSections(
        CacheContainerWriter containerWriter,
        RowKeyedWalkResult rowWalk,
        IProgress<AnalyzerProgressReport>? progress)
    {
        try
        {
            var csr = new ReverseEdgeCsrResult(rowWalk.RevOffsets, rowWalk.RevTargets, rowWalk.EdgeCount);
            ReverseEdgeContainerWriter.Write(containerWriter, csr, progress);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"ReverseIndex: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Forward-edge index: flush/dispose the extractor, then sort its raw buckets into loose,
    /// directory-indexed <c>.dat</c>/<c>.idx</c> scratch files.
    ///
    /// <para>Stage A's reachability walk reads successors from these loose files via
    /// <see cref="ForwardIndex.ForwardEdgeLooseFileReader"/> instead of doing a live ClrMD walk — see
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §2. That walk is now
    /// their <i>only</i> consumer: the container merge that used to follow it wrote sections nothing
    /// read, and was removed (docs/cache/cache-redesign-measurements.md §9), so the caller deletes
    /// these files once the walk is done.</para>
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

        /// <summary>
        /// How many of this segment's sizes would need the escape table at each candidate narrow
        /// width, which is what <see cref="NarrowColumnWidth.Choose"/> needs to pick one. Counted
        /// here because the container write is a streaming pass and cannot look ahead — see
        /// docs/cache/cache-format-clean-slate-redesign.md §10.2.
        /// </summary>
        public long SizeEscapesAtTwoBytes { get; private set; }

        public long SizeEscapesAtFourBytes { get; private set; }

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

            if (entry.Size >= ushort.MaxValue)
            {
                SizeEscapesAtTwoBytes++;
                if (entry.Size >= uint.MaxValue)
                    SizeEscapesAtFourBytes++;
            }

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

        // §6.2/§3: every section a successful build always writes must be present, not just the two
        // this check used to look at. A satellite write that failed and was downgraded to a warning
        // previously left a container that passed here forever, silently degrading every future
        // analysis of the dump until someone deleted .dumpindex/ by hand.
        //
        // Presence only — deliberately not checksum validity. Verifying every section here would
        // hash the whole file (~1.4 GB on the reference dump) on every cache hit, which is the exact
        // cost CacheContainerReader's per-session memoization exists to remove. Integrity stays
        // lazy, on first actual use. See cache-implementation-clean-slate-redesign.md § 6.5(a).
        foreach (CacheSectionDescriptor descriptor in CacheSectionCatalog.Required)
        {
            if (!reader.ContainsSection(descriptor.Id))
                return false;
        }

        // §10.5: the remainder of the same gap, now closable. A *conditional* section lost to the
        // same disk-full or AV blip leaves the Required check above green, because absence there is
        // otherwise indistinguishable from "this build wasn't asked to produce it". The manifest
        // records what the build opened, so the two can finally be told apart.
        if (reader.LostSections().Count > 0)
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
    // §2 (docs/cache/cache-format-clean-slate-redesign.md): format v8's Phase B resolves each
    // bucket's raw .tmp file directly into in-memory CSR arrays and deletes the .tmp itself once
    // resolved (ReverseEdgeCsrBuilder) — there is no intermediate sorted .dat/.idx pair to clean up
    // any more, so this only needs to catch whatever a failure left behind before that per-bucket
    // deletion ran.
    private static void DeleteReverseIndexScratchFiles(string indexDir, int bucketCount)
    {
        for (int i = 0; i < bucketCount; i++)
        {
            try { File.Delete(Path.Combine(indexDir, $"reverse_edges_bucket_{i}{ReverseIndexConstants.TemporaryScratchSuffix}")); } catch { /* best-effort */ }
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
    /// <summary>
    /// Writes the distinct-<c>MethodTable</c> dictionary as a dense ascending <c>ulong[]</c>; a
    /// value's position is the <c>TypeId</c> that <see cref="CacheSectionId.ObjectMethodTables"/>
    /// stores. Tiny — 112 KB for 14,003 types — so it is built in one buffer rather than streamed.
    /// </summary>
    private static uint WriteMethodTableDictionary(Stream stream, ulong[] methodTables, int bufferSize)
    {
        var hasher = new XxHash32();
        byte[] buf = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            int perChunk = buf.Length / sizeof(ulong);
            for (int start = 0; start < methodTables.Length; start += perChunk)
            {
                int count = Math.Min(perChunk, methodTables.Length - start);
                for (int i = 0; i < count; i++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        buf.AsSpan(i * sizeof(ulong)), methodTables[start + i]);
                }

                int bytes = count * sizeof(ulong);
                stream.Write(buf, 0, bytes);
                hasher.Append(buf.AsSpan(0, bytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Streams the per-segment 8-byte <c>MethodTable</c> scratch files into the container as narrow
    /// <c>TypeId</c> values — the <see cref="CacheSectionId.ObjectMethodTables"/> counterpart of
    /// <see cref="ConcatenateScratchFiles"/>, which stays a straight byte copy for the columns whose
    /// width doesn't change.
    /// </summary>
    /// <remarks>
    /// The scratch files deliberately keep the full 8-byte pointer. Narrowing during the parallel
    /// scan would need the final distinct-type count before the scan has finished, and
    /// <see cref="ScratchFileObjectMetadataLookup"/> reads those same files during Stage B and
    /// resolves real <c>MethodTable</c> values from them. Converting here costs no extra pass: it
    /// replaces a copy that already read every one of these bytes, and writes a quarter as many.
    /// </remarks>
    private static uint ConvertMethodTablesToTypeIds(
        Stream stream,
        string[] files,
        Dictionary<ulong, int> typeIdByMethodTable,
        int typeIdWidth,
        int bufferSize,
        bool deleteAfterCopy)
    {
        var hasher = new XxHash32();
        byte[] readBuf = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] writeBuf = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            int recordsPerRead = readBuf.Length / sizeof(ulong);
            int usableReadBytes = recordsPerRead * sizeof(ulong);

            for (int i = 0; i < files.Length; i++)
            {
                string segFile = files[i];
                if (!File.Exists(segFile))
                    continue;

                using (FileStream segStream = new(segFile, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: bufferSize, FileOptions.SequentialScan))
                {
                    int carried = 0;
                    int read;
                    while ((read = segStream.Read(readBuf, carried, usableReadBytes - carried)) > 0)
                    {
                        int available = carried + read;
                        int whole = available / sizeof(ulong);

                        for (int r = 0; r < whole; r++)
                        {
                            ulong methodTable = BinaryPrimitives.ReadUInt64LittleEndian(
                                readBuf.AsSpan(r * sizeof(ulong)));

                            // A MethodTable the master type builder never saw would mean the scan and
                            // the aggregate disagree, which is a bug rather than bad heap data — fail
                            // loudly instead of silently writing a wrong TypeId.
                            if (!typeIdByMethodTable.TryGetValue(methodTable, out int typeId))
                            {
                                throw new InvalidOperationException(
                                    $"MethodTable 0x{methodTable:X} is present in the object column but absent " +
                                    "from the type-aggregate dictionary.");
                            }

                            if (typeIdWidth == sizeof(ushort))
                                BinaryPrimitives.WriteUInt16LittleEndian(writeBuf.AsSpan(r * sizeof(ushort)), (ushort)typeId);
                            else
                                BinaryPrimitives.WriteUInt32LittleEndian(writeBuf.AsSpan(r * sizeof(uint)), (uint)typeId);
                        }

                        int written = whole * typeIdWidth;
                        stream.Write(writeBuf, 0, written);
                        hasher.Append(writeBuf.AsSpan(0, written));

                        // A read can stop mid-record; keep the tail for the next iteration.
                        carried = available - whole * sizeof(ulong);
                        if (carried > 0)
                            readBuf.AsSpan(whole * sizeof(ulong), carried).CopyTo(readBuf);
                    }
                }

                if (deleteAfterCopy)
                {
                    try { File.Delete(segFile); } catch { /* best-effort cleanup */ }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
            ArrayPool<byte>.Shared.Return(writeBuf);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Streams the per-segment 8-byte scratch files into the container at <paramref name="width"/>
    /// bytes per record, diverting the values that don't fit into <paramref name="overflow"/> and
    /// storing the escape sentinel in their place — the narrowed-column counterpart of
    /// <see cref="ConcatenateScratchFiles"/> (docs/cache/cache-format-clean-slate-redesign.md §10.1).
    /// </summary>
    /// <remarks>
    /// Like <see cref="ConvertMethodTablesToTypeIds"/>, this costs no extra pass: it replaces a copy
    /// that already read every one of these bytes. The scratch files themselves stay 8 bytes wide,
    /// because <see cref="ScratchFileObjectMetadataLookup"/> reads them during Stage B and wants the
    /// real values.
    /// </remarks>
    private static uint NarrowScratchFiles(
        Stream stream,
        string[] files,
        int width,
        List<(uint RecordIndex, ulong Value)> overflow,
        int bufferSize,
        bool deleteAfterCopy)
    {
        ulong sentinel = NarrowColumnWidth.Sentinel(width);
        var hasher = new XxHash32();
        byte[] readBuf = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] writeBuf = ArrayPool<byte>.Shared.Rent(bufferSize);
        long recordIndex = 0;

        try
        {
            int recordsPerRead = readBuf.Length / ColumnSize;
            int usableReadBytes = recordsPerRead * ColumnSize;

            foreach (string segFile in files)
            {
                if (!File.Exists(segFile))
                    continue;

                using (FileStream segStream = new(segFile, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: bufferSize, FileOptions.SequentialScan))
                {
                    int carried = 0;
                    int read;
                    while ((read = segStream.Read(readBuf, carried, usableReadBytes - carried)) > 0)
                    {
                        int available = carried + read;
                        int whole = available / ColumnSize;

                        for (int r = 0; r < whole; r++)
                        {
                            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(readBuf.AsSpan(r * ColumnSize));
                            ulong stored = value;
                            if (value >= sentinel)
                            {
                                overflow.Add(((uint)(recordIndex + r), value));
                                stored = sentinel;
                            }

                            if (width == sizeof(ushort))
                                BinaryPrimitives.WriteUInt16LittleEndian(writeBuf.AsSpan(r * sizeof(ushort)), (ushort)stored);
                            else
                                BinaryPrimitives.WriteUInt32LittleEndian(writeBuf.AsSpan(r * sizeof(uint)), (uint)stored);
                        }

                        int written = whole * width;
                        stream.Write(writeBuf, 0, written);
                        hasher.Append(writeBuf.AsSpan(0, written));
                        recordIndex += whole;

                        // A read can stop mid-record; keep the tail for the next iteration.
                        carried = available - whole * ColumnSize;
                        if (carried > 0)
                            readBuf.AsSpan(whole * ColumnSize, carried).CopyTo(readBuf);
                    }
                }

                if (deleteAfterCopy)
                {
                    try { File.Delete(segFile); } catch { /* best-effort cleanup */ }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
            ArrayPool<byte>.Shared.Return(writeBuf);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Streams the per-segment 8-byte address scratch files into the container as 4-byte deltas from
    /// a per-block base, collecting the bases and the escaped records as it goes — see
    /// <see cref="BlockDeltaColumn"/>. Same shape and same no-extra-pass property as
    /// <see cref="NarrowScratchFiles"/>.
    /// </summary>
    private static uint BlockDeltaScratchFiles(
        Stream stream,
        string[] files,
        List<ulong> blockBases,
        List<(uint RecordIndex, ulong Value)> overflow,
        int bufferSize,
        bool deleteAfterCopy)
    {
        var hasher = new XxHash32();
        byte[] readBuf = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] writeBuf = ArrayPool<byte>.Shared.Rent(bufferSize);
        long recordIndex = 0;

        try
        {
            int recordsPerRead = readBuf.Length / ColumnSize;
            int usableReadBytes = recordsPerRead * ColumnSize;

            foreach (string segFile in files)
            {
                if (!File.Exists(segFile))
                    continue;

                using (FileStream segStream = new(segFile, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: bufferSize, FileOptions.SequentialScan))
                {
                    int carried = 0;
                    int read;
                    while ((read = segStream.Read(readBuf, carried, usableReadBytes - carried)) > 0)
                    {
                        int available = carried + read;
                        int whole = available / ColumnSize;

                        for (int r = 0; r < whole; r++)
                        {
                            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(readBuf.AsSpan(r * ColumnSize));
                            uint delta = BlockDeltaColumn.Encode(address, recordIndex + r, blockBases, overflow);
                            BinaryPrimitives.WriteUInt32LittleEndian(writeBuf.AsSpan(r * sizeof(uint)), delta);
                        }

                        int written = whole * BlockDeltaColumn.DeltaWidth;
                        stream.Write(writeBuf, 0, written);
                        hasher.Append(writeBuf.AsSpan(0, written));
                        recordIndex += whole;

                        // A read can stop mid-record; keep the tail for the next iteration.
                        carried = available - whole * ColumnSize;
                        if (carried > 0)
                            readBuf.AsSpan(whole * ColumnSize, carried).CopyTo(readBuf);
                    }
                }

                if (deleteAfterCopy)
                {
                    try { File.Delete(segFile); } catch { /* best-effort cleanup */ }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
            ArrayPool<byte>.Shared.Return(writeBuf);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Reads the per-segment generation scratch files in concatenation order and collapses them to
    /// one record per change. Deletes each file as it goes, matching
    /// <see cref="ConcatenateScratchFiles"/>' default.
    /// </summary>
    private static List<(long FirstRecordIndex, sbyte Generation)> BuildGenerationRuns(string[] files, int bufferSize)
    {
        var runs = new List<(long FirstRecordIndex, sbyte Generation)>(capacity: 64);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            long recordIndex = 0;
            sbyte current = 0;
            bool started = false;

            for (int i = 0; i < files.Length; i++)
            {
                if (!File.Exists(files[i]))
                    continue;

                using (var fs = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.None, bufferSize, FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        for (int b = 0; b < read; b++)
                        {
                            sbyte generation = unchecked((sbyte)buffer[b]);
                            if (!started || generation != current)
                            {
                                runs.Add((recordIndex, generation));
                                current = generation;
                                started = true;
                            }

                            recordIndex++;
                        }
                    }
                }

                File.Delete(files[i]);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return runs;
    }

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
