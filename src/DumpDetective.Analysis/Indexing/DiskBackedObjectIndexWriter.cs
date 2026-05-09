using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Indexing.Satellite;

namespace DumpDetective.Analysis.Indexing;

internal sealed class DiskBackedObjectIndexWriter : IObjectIndexWriter
{
    // ObjectIndex.bin header constants (separate from IndexHeader to preserve existing format)
    internal const int ObjIndexMagic = 0x58494444; // DDIX
    private const int ObjIndexVersion = 1;
    internal const int ObjIndexHeaderSize = 24;
    private const int RecordSize = sizeof(ulong) * 3;
    private const int ProgressReportEveryObjects = 100_000;

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        string? dumpPath = null,
        DumpDetective.Core.Models.DumpSizeTier sizeTier = DumpDetective.Core.Models.DumpSizeTier.Medium)
    {
        ArgumentNullException.ThrowIfNull(dumpPath, nameof(dumpPath));
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Use canonical per-dump .dumpindex/ directory for all index files.
        DumpIndexPaths.EnsureDirectory(dumpPath);
        string indexPath = DumpIndexPaths.ObjectIndex(dumpPath);
        string typeAggPath = DumpIndexPaths.TypeAggregateIndex(dumpPath);

        // ── Fast-path: skip full heap scan if a valid TypeAggregateIndex.bin exists ──
        // TypeAggregateIndex.bin is written LAST, after all satellite files, so its
        // presence guarantees the previous build completed successfully.
        if (TryLoadFromCache(indexPath, typeAggPath, dumpPath, out var cachedResult))
        {
            progress?.Report(new(cachedResult!.ObjectCount, "index cache hit",
                Detail: "loaded TypeAggregateIndex.bin — skipping heap scan",
                Elapsed: stopwatch.Elapsed));
            stopwatch.Stop();
            return cachedResult!;
        }

        long objectCount = 0;

        int writeBuffer = sizeTier switch
        {
            DumpDetective.Core.Models.DumpSizeTier.Large => 4 * 1024 * 1024,
            DumpDetective.Core.Models.DumpSizeTier.Medium => 1 * 1024 * 1024,
            _ => 128 * 1024,
        };
        // Each segment gets its own entry list sized from its own byte length (see below).

        // Cap DOP so ClrMD's minidump page cache never holds more than this many segments'
        // pages resident simultaneously. For Large dumps on SSDs, up to 8 concurrent segments
        // give additional throughput; smaller tiers use fewer to bound page-cache pressure.
        int maxSegmentParallelism = sizeTier switch
        {
            DumpDetective.Core.Models.DumpSizeTier.Large => Math.Min(Environment.ProcessorCount, 8),
            DumpDetective.Core.Models.DumpSizeTier.Medium => Math.Min(Environment.ProcessorCount, 4),
            _ => 2,
        };

        var masterBuilder = new TypeIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        // Satellite data collected during parallel scan, written serially afterwards.
        var shapeCache = new ConcurrentDictionary<ulong, TypeShapeEntry>();
        // OPT: global flags cache eliminates redundant ComputeTypeFlags calls across segments,
        // reducing IsFinalizable string allocations from (uniqueTypes × segmentCount) to uniqueTypes.
        var globalFlagsCache = new ConcurrentDictionary<ulong, TypeAggregateFlags>();
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

        // OPT: open the index file before the parallel scan so each segment writes directly to
        // disk as it completes — eliminating the post-scan bag + flat-array accumulation
        // (~1.2 GB of pool buffers + flat array that were previously simultaneously live).
        // Serialization (CPU) runs outside the lock; only stream.Write is serialized.
        int serialChunkEntries = Math.Max(writeBuffer / RecordSize, 1);
        int serialChunkBytes = serialChunkEntries * RecordSize;
        object streamWriteLock = new();
        using FileStream stream = new(indexPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: writeBuffer, FileOptions.SequentialScan);
        WriteObjIndexHeader(stream, recordCount: 0); // placeholder — overwritten after scan

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxSegmentParallelism
        };

        Parallel.ForEach(
            heap.Segments,
            parallelOptions,
            () => (Builder: new TypeIndexBuilder(), FlagsCache: new Dictionary<ulong, TypeAggregateFlags>(capacity: 64), StringDedup: new Dictionary<ulong, StringDedupEntry>(capacity: 64), LengthSamples: new List<int>(), LengthBuckets: new Dictionary<string, int>(StringComparer.Ordinal)),
            (segment, _, state) =>
            {
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

                    // Compute type flags + shape once per unique MT.
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
                    segBuf[segCount++] = entry;
                    state.Builder.Add(entry, moduleId, flags, objGen);

                    // Collect satellite candidates (written serially after the parallel loop).
                    if ((flags & TypeAggregateFlags.IsTaskType) != 0)
                        taskCandidates.Add((obj.Address, mt));
                    if ((flags & TypeAggregateFlags.IsDelegateType) != 0)
                        eventCandidates.Add((obj.Address, mt));
                    if (entry.Size >= 85_000)
                        largeCandidates.Add((obj.Address, mt, entry.Size));
                    // Collect LOH/POH free blocks during the scan — avoids a second segment walk
                    // that LohFreeBlockWriter.Write(heap,...) would otherwise require.
                    if (isLohOrPoh && (flags & TypeAggregateFlags.IsFreeBlobType) != 0)
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

                // Serialize segment entries to disk in fixed-size chunks.
                // Each chunk is serialized to a pooled byte[] outside the lock, then flushed
                // to the stream under the write lock.  The HeapEntry pool buffer is returned
                // immediately after — at most MaxSegmentParallelism buffers are live at any instant.
                {
                    byte[] serialBuf = ArrayPool<byte>.Shared.Rent(serialChunkBytes);
                    try
                    {
                        int srcIdx = 0;
                        while (srcIdx < segCount)
                        {
                            int chunkEntries = Math.Min(serialChunkEntries, segCount - srcIdx);
                            int chunkBytes = chunkEntries * RecordSize;
                            for (int ci = 0; ci < chunkEntries; ci++)
                            {
                                int off = ci * RecordSize;
                                ref HeapEntry e = ref segBuf[srcIdx + ci];
                                BinaryPrimitives.WriteUInt64LittleEndian(serialBuf.AsSpan(off), e.Address);
                                BinaryPrimitives.WriteUInt64LittleEndian(serialBuf.AsSpan(off + 8), e.MethodTable);
                                BinaryPrimitives.WriteUInt64LittleEndian(serialBuf.AsSpan(off + 16), e.Size);
                            }
                            lock (streamWriteLock)
                                stream.Write(serialBuf, 0, chunkBytes);
                            srcIdx += chunkEntries;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(serialBuf);
                        ArrayPool<HeapEntry>.Shared.Return(segBuf, clearArray: false);
                    }
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

        // Seal the index: flush buffered data, rewind, and overwrite the placeholder header
        // with the actual record count now that all segment writes have completed.
        stream.Flush();
        stream.Position = 0;
        WriteObjIndexHeader(stream, objectCount);
        stream.Flush();

        // Capture the main heap scan elapsed time for HeapIndexBuildResult before satellite writes.
        // We keep the stopwatch running during satellite file writes so their progress reports
        // show a growing elapsed rather than a frozen timestamp.
        TimeSpan scanElapsed = stopwatch.Elapsed;
        progress?.Report(new(objectCount, "index complete", Detail: null, Elapsed: scanElapsed));

        // Write satellite index files serially after the parallel heap scan.
        IReadOnlyList<string> satelliteWarnings = WriteSatelliteFiles(dumpPath, heap,
            taskCandidates, eventCandidates, largeCandidates, lohFreeBlockCandidates,
            cancellationToken, progress, stopwatch);

        // Write StringDedupIndex satellite file (compact binary) so subsequent analyses
        // can read prebuilt dedup data without re-scanning the heap.
        try
        {
            string dedupPath = DumpIndexPaths.StringDedupIndex(dumpPath);
            using var ds = new FileStream(dedupPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
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
        }
        catch (Exception ex)
        {
            ((List<string>)satelliteWarnings).Add($"StringDedupIndex.bin: {ex.GetType().Name}: {ex.Message}");
        }

        // Persist lightweight distribution metadata to a small JSON sidecar so readers
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

                string metaPath = DumpIndexPaths.StringDedupIndexMetadata(dumpPath);
                var jsOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                string json = System.Text.Json.JsonSerializer.Serialize(distribution, jsOpts);
                File.WriteAllText(metaPath, json);
            }
        }
        catch { /* non-fatal */ }

        stopwatch.Stop();

        // Extract aggregates once so they can be passed both to HeapIndexBuildResult and to
        // TypeAggregateIndexWriter without calling masterBuilder.Build() twice.
        var typeAggregates = masterBuilder.Build();
        var globalSizeBuckets = masterBuilder.BuildSizeBuckets();

        // Write TypeAggregateIndex.bin LAST so its presence confirms a complete build.
        // A future call to Build() will detect it and skip the full heap scan entirely.
        try
        {
            TypeAggregateIndexWriter.Write(typeAggPath, dumpPath, typeAggregates,
                moduleRegistry.Modules, globalSizeBuckets, shapeCache, objectCount);
        }
        catch
        {
            // Non-fatal: analysis proceeds without the cache. The file will be written on
            // the next successful full build (e.g. after a disk-full condition clears).
        }

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            indexPath,
            objectCount,
            scanElapsed,
            typeAggregates,
            InMemoryEntries: null,
            Modules: moduleRegistry.Modules,
            GlobalSizeBuckets: globalSizeBuckets,
            TypeShapeCache: shapeCache,
            SatelliteWarnings: satelliteWarnings.Count > 0 ? satelliteWarnings : null,
            StringDedupIndex: masterStringDedup.Count > 0 ? masterStringDedup : null);
    }

    // ── Satellite file writing ─────────────────────────────────────────────────

    private static IReadOnlyList<string> WriteSatelliteFiles(
        string dumpPath,
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

        // HandleSnapshot.bin — GC handle enumeration
        try
        {
            progress?.Report(new(0, "enumerating GC handles", Detail: null, Elapsed: stopwatch.Elapsed));
            HandleSnapshotWriter.Write(DumpIndexPaths.HandleSnapshot(dumpPath), heap.Runtime, cancellationToken, progress, stopwatch);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"HandleSnapshot.bin: {ex.GetType().Name}: {ex.Message}"); }

        // RootIndex.bin — GC root enumeration (can be slow on large dumps; progress reported every 50k roots)
        try
        {
            progress?.Report(new(0, "enumerating GC roots", Detail: null, Elapsed: stopwatch.Elapsed));
            RootIndexWriter.Write(DumpIndexPaths.RootIndex(dumpPath), heap, cancellationToken, progress, stopwatch);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"RootIndex.bin: {ex.GetType().Name}: {ex.Message}"); }

        // TaskIndex.bin — Task objects collected during heap scan
        try
        {
            progress?.Report(new(0, "writing TaskIndex.bin", Detail: null, Elapsed: stopwatch.Elapsed));
            using TaskIndexWriter tw = new(DumpIndexPaths.TaskIndex(dumpPath));
            foreach ((ulong addr, ulong mt) in taskCandidates)
                tw.Add(addr, mt, stateFlags: 0); // stateFlags resolved in Phase 2 by AsyncTaskAnalyzer
            tw.Flush();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"TaskIndex.bin: {ex.GetType().Name}: {ex.Message}"); }

        // EventCandidateIndex.bin — delegate/event objects collected during heap scan
        try
        {
            progress?.Report(new(0, "writing EventCandidateIndex.bin", Detail: null, Elapsed: stopwatch.Elapsed));
            using EventCandidateIndexWriter ew = new(DumpIndexPaths.EventCandidateIndex(dumpPath));
            foreach ((ulong addr, ulong mt) in eventCandidates)
                ew.Add(addr, mt);
            ew.Flush();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"EventCandidateIndex.bin: {ex.GetType().Name}: {ex.Message}"); }

        // LargeObjectIndex.bin — top-100 LOH objects by size
        try
        {
            progress?.Report(new(0, "writing LargeObjectIndex.bin", Detail: null, Elapsed: stopwatch.Elapsed));
            var tracker = new LargeObjectTracker();
            foreach ((ulong addr, ulong mt, ulong size) in largeCandidates)
                tracker.Consider(addr, mt, size);
            tracker.Write(DumpIndexPaths.LargeObjectIndex(dumpPath));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"LargeObjectIndex.bin: {ex.GetType().Name}: {ex.Message}"); }

        // LohFreeBlockIndex.bin — free block gaps already collected during the main scan;
        // no second segment walk required.
        try
        {
            progress?.Report(new(0, "writing LohFreeBlockIndex.bin", Detail: null, Elapsed: stopwatch.Elapsed));
            LohFreeBlockWriter.WriteFromCandidates(
                DumpIndexPaths.LohFreeBlockIndex(dumpPath), lohFreeBlockCandidates, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"LohFreeBlockIndex.bin: {ex.GetType().Name}: {ex.Message}"); }

        return warnings;
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

            if (name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
                flags |= TypeAggregateFlags.IsTaskType;

            if (name == "Free")
                flags |= TypeAggregateFlags.IsFreeBlobType;
        }

        if (type.IsArray)
            flags |= TypeAggregateFlags.IsArrayType;

        if (type.IsFinalizable)
            flags |= TypeAggregateFlags.IsFinalizableType;

        if (IsDelegateType(type))
            flags |= TypeAggregateFlags.IsDelegateType;

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

    // ── Index cache fast-path ──────────────────────────────────────────────────

    /// <summary>
    /// Attempts to skip the full heap scan by loading a previous build's
    /// <see cref="TypeAggregateIndex.bin"/> and <c>ObjectIndex.bin</c>.
    /// Returns <c>true</c> and populates <paramref name="result"/> on success.
    /// </summary>
    private static bool TryLoadFromCache(
        string indexPath,
        string typeAggPath,
        string dumpPath,
        out HeapIndexBuildResult? result)
    {
        result = null;
        if (!File.Exists(indexPath) || !File.Exists(typeAggPath))
            return false;

        if (!TryReadObjectCount(indexPath, out long objectCount))
            return false;

        return TypeAggregateIndexReader.TryLoad(typeAggPath, indexPath, dumpPath, objectCount, out result);
    }

    /// <summary>
    /// Reads the <c>recordCount</c> field from an <c>ObjectIndex.bin</c> header.
    /// Returns <c>false</c> if the file is missing, too short, or has a wrong magic number.
    /// </summary>
    private static bool TryReadObjectCount(string indexPath, out long objectCount)
    {
        objectCount = 0;
        try
        {
            using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 64, FileOptions.SequentialScan);
            Span<byte> hdr = stackalloc byte[ObjIndexHeaderSize];
            if (fs.ReadAtLeast(hdr, ObjIndexHeaderSize, throwOnEndOfStream: false) < ObjIndexHeaderSize)
                return false;
            if (BinaryPrimitives.ReadInt32LittleEndian(hdr) != ObjIndexMagic)
                return false;
            objectCount = BinaryPrimitives.ReadInt64LittleEndian(hdr[16..]);
            return objectCount > 0;
        }
        catch { return false; }
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

    // ── ObjectIndex.bin header ─────────────────────────────────────────────────
    // Uses the existing format (not IndexHeader) to preserve backward compatibility
    // with any existing ObjectIndex.bin files.

    private static void WriteObjIndexHeader(Stream stream, long recordCount)
    {
        Span<byte> buf = stackalloc byte[ObjIndexHeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(buf, ObjIndexMagic);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..], ObjIndexVersion);
        BinaryPrimitives.WriteInt64LittleEndian(buf[8..], DateTime.UtcNow.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(buf[16..], recordCount);
        stream.Write(buf);
    }
}
