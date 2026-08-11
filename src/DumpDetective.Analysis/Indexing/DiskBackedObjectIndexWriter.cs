using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Indexing;

internal sealed class DiskBackedObjectIndexWriter : IObjectIndexWriter
{
    // Columnar Object* sections store one ulong per object per section — no per-section
    // header, since the container's TOC already carries each section's RecordCount.
    private const int ColumnSize = sizeof(ulong);
    // ObjectGenerations is a separate, narrower column (1 byte/sbyte vs. 8 bytes/ulong).
    private const int GenColumnSize = sizeof(sbyte);
    private const int ProgressReportEveryObjects = 100_000;

    // TEMPORARY perf A/B toggle (see docs/cache/17-DiskIndexBuildPhaseBreakdown.md option 2):
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

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        string? dumpPath = null,
        DumpSizeTier sizeTier = DumpSizeTier.Medium)
    {
        ArgumentNullException.ThrowIfNull(dumpPath, nameof(dumpPath));
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Use canonical per-dump .dumpindex/ directory for the container file.
        DumpIndexPaths.EnsureDirectory(dumpPath);
        string containerPath = DumpIndexPaths.CacheContainer(dumpPath);

        // ── Fast-path: skip full heap scan if cache.bin has a valid TypeAggregates section ──
        // TypeAggregates is written LAST, after all other sections, so its presence
        // guarantees the previous build completed successfully.
        if (TryLoadFromCache(containerPath, dumpPath, out var cachedResult))
        {
            progress?.Report(new(cachedResult!.ObjectCount, "index cache hit",
                Detail: "loaded cache.bin — skipping heap scan",
                Elapsed: stopwatch.Elapsed));
            stopwatch.Stop();
            return cachedResult!;
        }

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
        var taskCandidates = new ConcurrentBag<(ulong Addr, ulong Mt)>();
        var eventCandidates = new ConcurrentBag<(ulong Addr, ulong Mt)>();
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

        // Reverse-reference index (Phase A): extracted alongside the main heap scan below since a
        // second full heap pass is prohibitively expensive on large dumps — see
        // docs/analysis/phase1-redesigns/full-reverse-index-plan.md Decision 2. One extractor
        // instance for the whole scan; RecordEdge is thread-safe (per-bucket locks) so every
        // segment's parallel worker below shares it directly.
        int reverseIndexBucketCount = ReverseIndexConstants.CalculateBucketCount(new FileInfo(dumpPath).Length);
        ReverseEdgeExtractor? reverseEdgeExtractor = SkipReverseIndexBuild
            ? null
            : new ReverseEdgeExtractor(reverseIndexBucketCount, indexDir);

        ClrSegment[] segments = heap.Segments.ToArray();
        string[] segAddrScratchFiles = new string[segments.Length];
        string[] segMtScratchFiles = new string[segments.Length];
        string[] segSizeScratchFiles = new string[segments.Length];
        string[] segGenScratchFiles = new string[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            segAddrScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.addr.tmp");
            segMtScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.mt.tmp");
            segSizeScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.size.tmp");
            segGenScratchFiles[i] = Path.Combine(indexDir, $"ObjectIndex.bin.seg{i}.gen.tmp");
        }

        using var containerWriter = new CacheContainerWriter(containerPath, dumpPath, progress);
        Stream stream = containerWriter.Stream;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxSegmentParallelism
        };

        try
        {
        Parallel.For(
            0,
            segments.Length,
            parallelOptions,
            () => (Builder: new TypeIndexBuilder(), FlagsCache: new Dictionary<ulong, TypeAggregateFlags>(capacity: 64), ModuleIdCache: new Dictionary<ulong, int>(capacity: 64), StringDedup: new Dictionary<ulong, StringDedupEntry>(capacity: 64), LengthSamples: new List<int>(), LengthBuckets: new Dictionary<string, int>(StringComparer.Ordinal)),
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
                            shapeCache.TryAdd(mt, ComputeTypeShape(obj.Type));
                            int[] stringFieldIndices = ComputeStringFieldIndices(obj.Type);
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
                    segBuf[segCount++] = entry;
                    state.Builder.Add(entry, moduleId, flags, objGen);

                    // Reverse-reference index (Phase A): record every outgoing edge for this
                    // object. "carefully" matches the enumeration mode validated in Investigation 1
                    // (see pre-implementation-validation.md) and, unlike a raw field walk, also
                    // covers array elements — the dominant edge source for collection-held leaks.
                    if (reverseEdgeExtractor is not null)
                    {
                        foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                        {
                            if (reference.IsValid)
                                reverseEdgeExtractor.RecordEdge(obj.Address, reference.Address);
                        }
                    }

                    // Collect satellite candidates (written serially after the parallel loop).
                    if ((flags & TypeAggregateFlags.IsTaskType) != 0)
                        taskCandidates.Add((obj.Address, mt));
                    if ((flags & TypeAggregateFlags.IsDelegateType) != 0)
                        eventCandidates.Add((obj.Address, mt));
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

                // Serialize segment entries to this segment's own columnar scratch files in
                // fixed-size chunks — no shared-stream lock, so segments make independent
                // progress. Scratch files are concatenated in segment order after the scan
                // completes, one column at a time.
                if (segCount > 0)
                {
                    byte[] addrBuf = ArrayPool<byte>.Shared.Rent(serialChunkEntries * ColumnSize);
                    byte[] mtBuf = ArrayPool<byte>.Shared.Rent(serialChunkEntries * ColumnSize);
                    byte[] sizeBuf = ArrayPool<byte>.Shared.Rent(serialChunkEntries * ColumnSize);
                    byte[] genBuf = ArrayPool<byte>.Shared.Rent(serialChunkEntries * GenColumnSize);
                    try
                    {
                        using FileStream addrStream = new(segAddrScratchFiles[segIdx], FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize: writeBuffer, FileOptions.SequentialScan);
                        using FileStream mtStream = new(segMtScratchFiles[segIdx], FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize: writeBuffer, FileOptions.SequentialScan);
                        using FileStream sizeStream = new(segSizeScratchFiles[segIdx], FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize: writeBuffer, FileOptions.SequentialScan);
                        using FileStream genStream = new(segGenScratchFiles[segIdx], FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize: writeBuffer, FileOptions.SequentialScan);
                        int srcIdx = 0;
                        while (srcIdx < segCount)
                        {
                            int chunkEntries = Math.Min(serialChunkEntries, segCount - srcIdx);
                            int chunkBytes = chunkEntries * ColumnSize;
                            for (int ci = 0; ci < chunkEntries; ci++)
                            {
                                int off = ci * ColumnSize;
                                ref HeapEntry e = ref segBuf[srcIdx + ci];
                                BinaryPrimitives.WriteUInt64LittleEndian(addrBuf.AsSpan(off), e.Address);
                                BinaryPrimitives.WriteUInt64LittleEndian(mtBuf.AsSpan(off), e.MethodTable);
                                BinaryPrimitives.WriteUInt64LittleEndian(sizeBuf.AsSpan(off), e.Size);
                                genBuf[ci] = unchecked((byte)e.Generation);
                            }
                            addrStream.Write(addrBuf, 0, chunkBytes);
                            mtStream.Write(mtBuf, 0, chunkBytes);
                            sizeStream.Write(sizeBuf, 0, chunkBytes);
                            genStream.Write(genBuf, 0, chunkEntries * GenColumnSize);
                            srcIdx += chunkEntries;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(addrBuf);
                        ArrayPool<byte>.Shared.Return(mtBuf);
                        ArrayPool<byte>.Shared.Return(sizeBuf);
                        ArrayPool<byte>.Shared.Return(genBuf);
                        ArrayPool<HeapEntry>.Shared.Return(segBuf, clearArray: false);
                    }
                }
                else
                {
                    ArrayPool<HeapEntry>.Shared.Return(segBuf, clearArray: false);
                }

                return state;
            },
            state =>
            {
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
            throw;
        }

        // Concatenate the per-segment scratch files into the three columnar sections, one
        // column at a time, in segment order — this is what makes disk-mode entry order
        // deterministic and match memory-mode's segment-ordered output, and keeps each
        // column contiguous in the container so a reader that only needs MethodTable (type
        // aggregation) or Size (histograms) doesn't pay to read Address too.
        containerWriter.BeginSection(CacheSectionId.ObjectAddresses);
        ConcatenateScratchFiles(stream, segAddrScratchFiles, writeBuffer);
        containerWriter.EndSection(objectCount);

        containerWriter.BeginSection(CacheSectionId.ObjectMethodTables);
        ConcatenateScratchFiles(stream, segMtScratchFiles, writeBuffer);
        containerWriter.EndSection(objectCount);

        containerWriter.BeginSection(CacheSectionId.ObjectSizes);
        ConcatenateScratchFiles(stream, segSizeScratchFiles, writeBuffer);
        containerWriter.EndSection(objectCount);

        containerWriter.BeginSection(CacheSectionId.ObjectGenerations);
        ConcatenateScratchFiles(stream, segGenScratchFiles, writeBuffer);
        containerWriter.EndSection(objectCount);

        // Capture the main heap scan elapsed time for HeapIndexBuildResult before satellite writes.
        // We keep the stopwatch running during satellite file writes so their progress reports
        // show a growing elapsed rather than a frozen timestamp.
        TimeSpan scanElapsed = stopwatch.Elapsed;
        progress?.Report(new(objectCount, "index complete", Detail: null, Elapsed: scanElapsed));

        // Write satellite sections serially after the parallel heap scan.
        List<string> satelliteWarnings = WriteSatelliteSections(containerWriter, heap,
            taskCandidates, eventCandidates, largeCandidates, lohFreeBlockCandidates,
            cancellationToken, progress, stopwatch);

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

        // Reverse-reference index (Phase B + C) — flush, sort and merge the buckets extracted
        // during the heap scan above. Kept before stopwatch.Stop() so its progress reports (sort
        // can take a while on many buckets) show a growing elapsed like the satellite sections.
        if (reverseEdgeExtractor is not null)
        {
            string? reverseIndexWarning = WriteReverseIndexSections(
                containerWriter, indexDir, reverseIndexBucketCount, reverseEdgeExtractor,
                cancellationToken, progress, stopwatch);
            if (reverseIndexWarning is not null)
                satelliteWarnings.Add(reverseIndexWarning);
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
        ConcurrentBag<(ulong Addr, ulong Mt)> taskCandidates,
        ConcurrentBag<(ulong Addr, ulong Mt)> eventCandidates,
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

        // Tasks — Task objects collected during heap scan
        try
        {
            progress?.Report(new(0, "writing Tasks section", Detail: null, Elapsed: stopwatch.Elapsed));
            containerWriter.BeginSection(CacheSectionId.Tasks);
            using (TaskIndexWriter tw = new(containerWriter.Stream))
            {
                foreach ((ulong addr, ulong mt) in taskCandidates)
                    tw.Add(addr, mt, stateFlags: 0); // stateFlags resolved in Phase 2 by AsyncTaskAnalyzer
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

        // EventCandidates — delegate/event objects collected during heap scan
        try
        {
            progress?.Report(new(0, "writing EventCandidates section", Detail: null, Elapsed: stopwatch.Elapsed));
            containerWriter.BeginSection(CacheSectionId.EventCandidates);
            using (EventCandidateIndexWriter ew = new(containerWriter.Stream))
            {
                foreach ((ulong addr, ulong mt) in eventCandidates)
                    ew.Add(addr, mt);
                ew.Flush();
            }
            containerWriter.EndSection(eventCandidates.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            containerWriter.AbortSection();
            warnings.Add($"EventCandidates: {ex.GetType().Name}: {ex.Message}");
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
            var truncatedPerBucket = new IReadOnlySet<ulong>[bucketCount];
            for (int i = 0; i < bucketCount; i++)
                truncatedPerBucket[i] = extractor.GetTruncatedChildren(i);

            extractor.DisposeAsync(progress).AsTask().GetAwaiter().GetResult();

            var sorter = new ReverseEdgeSorter();
            sorter.SortBucketsAsync(indexDir, bucketCount, cancellationToken, truncatedPerBucket, progress)
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

    // ── Type classification helpers ────────────────────────────────────────────

    private static string CreatePreview(string value)
    {
        string s = value.Length > 47 ? value[..47] + "..." : value;
        return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static readonly Regex StateMachinePattern = 
        new(@"<(.+?)>d__\d+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

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
        string? name = type.Name;
        if (name is null || !StateMachinePattern.IsMatch(name))
            return false;

        // Confirm it implements IAsyncStateMachine interface
        foreach (ClrInterface iface in type.EnumerateInterfaces())
        {
            if (iface.Name is "System.Runtime.CompilerServices.IAsyncStateMachine")
                return true;
        }
        return false;
    }

    private static TypeShapeEntry ComputeTypeShape(ClrType type)
    {
        short refFields = 0;
        short valFields = 0;

        foreach (ClrInstanceField field in type.Fields)
        {
            if (field.IsObjectReference)
                refFields++;
            else
                valFields++;
        }

        return new TypeShapeEntry(refFields, valFields);
    }

    /// <summary>
    /// Indices (into <c>type.Fields</c>, matching <see cref="StringAnalyzer"/>'s FieldLayoutCache
    /// ordering) of instance fields whose type is <c>System.String</c>. Empty array (never null)
    /// when the type has none, so the caller's <c>Length &gt; 0</c> check decides whether to add a
    /// sparse-dictionary entry. Walks <c>type.Fields</c> itself rather than sharing
    /// <see cref="ComputeTypeShape"/>'s loop — both run at most once per unique MethodTable.
    /// </summary>
    /// <remarks>
    /// PERF: uses <see cref="ClrInstanceField.ElementType"/> — a tag read directly off the field's
    /// metadata signature — instead of <c>field.Type?.Name</c>. <c>field.Type</c> forces full
    /// <see cref="ClrType"/> resolution, which is far more expensive and, under this method's
    /// <see cref="Parallel"/>.For segment-worker caller, serializes on ClrMD's internal
    /// metadata-resolution locking badly enough to turn a ~20s scan into 5+ minutes (measured).
    /// Same pattern applied at every other per-field type check touched in this optimization pass:
    /// <see cref="StringAnalyzer"/>'s owner-type lazy fallback and
    /// <c>ScanForStringOwnerTypesFallback</c>, and <c>CollectionAnalyzer.FindFirstArrayField</c> /
    /// <c>CollectionAnalyzer.GetOrBuildFieldLayout</c>'s field-name fallback loop.
    /// </remarks>
    private static int[] ComputeStringFieldIndices(ClrType type)
    {
        List<int>? indices = null;
        int i = 0;
        foreach (ClrInstanceField field in type.Fields)
        {
            if (field.ElementType == ClrElementType.String)
            {
                indices ??= new List<int>(capacity: 4);
                indices.Add(i);
            }
            i++;
        }

        return indices?.ToArray() ?? [];
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

    /// <summary>
    /// Streams each scratch file in <paramref name="files"/> into <paramref name="stream"/> in
    /// order, deleting each as it's consumed. Used to assemble a single columnar section from
    /// per-segment scratch files without materializing the whole column in memory.
    /// </summary>
    private static void ConcatenateScratchFiles(Stream stream, string[] files, int bufferSize)
    {
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
                        stream.Write(copyBuf, 0, read);
                }
                File.Delete(segFile);
            }
            stream.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuf);
        }
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
