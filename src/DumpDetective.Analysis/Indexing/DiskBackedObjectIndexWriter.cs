using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Indexing.Satellite;

namespace DumpDetective.Analysis.Indexing;

internal sealed class DiskBackedObjectIndexWriter : IObjectIndexWriter
{
    // ObjectIndex.bin header constants (separate from IndexHeader to preserve existing format)
    private const int ObjIndexMagic      = 0x58494444; // DDIX
    private const int ObjIndexVersion    = 1;
    private const int ObjIndexHeaderSize = 24;
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

        long objectCount = 0;

        int writeBuffer = sizeTier switch
        {
            DumpDetective.Core.Models.DumpSizeTier.Large => 4 * 1024 * 1024,
            DumpDetective.Core.Models.DumpSizeTier.Medium => 1 * 1024 * 1024,
            _ => 128 * 1024,
        };
        // Each segment gets its own entry list sized from its own byte length (see below).

        // Cap DOP so ClrMD's minidump page cache never holds more than this many segments'
        // pages resident simultaneously. Uncapped (default -1) causes ProcessorCount threads
        // to each hold a different segment's pages in cache concurrently, which multiplied the
        // working-set footprint proportional to core count after the parallel rearchitecture.
        // 4 concurrent segments gives ~4x speedup over sequential while bounding peak page pressure.
        const int MaxSegmentParallelism = 4;

        var masterBuilder  = new TypeIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        // Satellite data collected during parallel scan, written serially afterwards.
        var shapeCache      = new ConcurrentDictionary<ulong, TypeShapeEntry>();
        // OPT: global flags cache eliminates redundant ComputeTypeFlags calls across segments,
        // reducing IsFinalizable string allocations from (uniqueTypes × segmentCount) to uniqueTypes.
        var globalFlagsCache = new ConcurrentDictionary<ulong, TypeAggregateFlags>();
        var taskCandidates  = new ConcurrentBag<(ulong Addr, ulong Mt)>();
        var eventCandidates = new ConcurrentBag<(ulong Addr, ulong Mt)>();
        var largeCandidates = new ConcurrentBag<(ulong Addr, ulong Mt, ulong Size)>();

        // OPT: open the index file before the parallel scan so each segment writes directly to
        // disk as it completes — eliminating the post-scan bag + flat-array accumulation
        // (~1.2 GB of pool buffers + flat array that were previously simultaneously live).
        // Serialization (CPU) runs outside the lock; only stream.Write is serialized.
        int serialChunkEntries = Math.Max(writeBuffer / RecordSize, 1);
        int serialChunkBytes   = serialChunkEntries * RecordSize;
        object streamWriteLock = new();
        using FileStream stream = new(indexPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: writeBuffer, FileOptions.SequentialScan);
        WriteObjIndexHeader(stream, recordCount: 0); // placeholder — overwritten after scan

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
                // Determine generation from segment kind — avoids per-object GetGeneration call
                // for server GC where each segment is dedicated to a single generation.
                // For Ephemeral segments (workstation GC) segGen = -1; generation is resolved
                // per-object below via segment.GetGeneration(address).
                int segGen = SegmentKindToGeneration(segment.Kind);
                bool isEphemeral = segGen < 0;

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

                    var entry    = new HeapEntry(obj.Address, mt, obj.Size);
                    int moduleId = moduleRegistry.GetOrAdd(obj.Type.Module);
                    int objGen   = isEphemeral ? ResolveObjectGeneration(segment, obj.Address) : segGen;
                    segBuf[segCount++] = entry;
                    state.Builder.Add(entry, moduleId, flags, objGen);

                    // Collect satellite candidates (written serially after the parallel loop).
                    if ((flags & TypeAggregateFlags.IsTaskType) != 0)
                        taskCandidates.Add((obj.Address, mt));
                    if ((flags & TypeAggregateFlags.IsDelegateType) != 0)
                        eventCandidates.Add((obj.Address, mt));
                    if (entry.Size >= 85_000)
                        largeCandidates.Add((obj.Address, mt, entry.Size));

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
                                BinaryPrimitives.WriteUInt64LittleEndian(serialBuf.AsSpan(off),      e.Address);
                                BinaryPrimitives.WriteUInt64LittleEndian(serialBuf.AsSpan(off + 8),  e.MethodTable);
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
                    masterBuilder.Merge(state.Builder);
            });

        // Seal the index: flush buffered data, rewind, and overwrite the placeholder header
        // with the actual record count now that all segment writes have completed.
        stream.Flush();
        stream.Position = 0;
        WriteObjIndexHeader(stream, objectCount);
        stream.Flush();

        stopwatch.Stop();
        progress?.Report(new(objectCount, "index complete", Detail: null, Elapsed: stopwatch.Elapsed));

        // Write satellite index files serially after the parallel heap scan.
        WriteSatelliteFiles(dumpPath, heap, taskCandidates, eventCandidates, largeCandidates,
            cancellationToken, progress, stopwatch);

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            indexPath,
            objectCount,
            stopwatch.Elapsed,
            masterBuilder.Build(),
            InMemoryEntries: null,
            Modules: moduleRegistry.Modules,
            GlobalSizeBuckets: masterBuilder.BuildSizeBuckets(),
            TypeShapeCache: shapeCache);
    }

    // ── Satellite file writing ─────────────────────────────────────────────────

    private static void WriteSatelliteFiles(
        string dumpPath,
        ClrHeap heap,
        ConcurrentBag<(ulong Addr, ulong Mt)> taskCandidates,
        ConcurrentBag<(ulong Addr, ulong Mt)> eventCandidates,
        ConcurrentBag<(ulong Addr, ulong Mt, ulong Size)> largeCandidates,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress,
        Stopwatch stopwatch)
    {
        // HandleSnapshot.bin — GC handle enumeration
        try
        {
            progress?.Report(new(0, "writing HandleSnapshot.bin", Detail: null, Elapsed: stopwatch.Elapsed));
            HandleSnapshotWriter.Write(DumpIndexPaths.HandleSnapshot(dumpPath), heap.Runtime, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* non-critical satellite — continue on failure */ }

        // RootIndex.bin — GC root enumeration
        try
        {
            progress?.Report(new(0, "writing RootIndex.bin", Detail: null, Elapsed: stopwatch.Elapsed));
            RootIndexWriter.Write(DumpIndexPaths.RootIndex(dumpPath), heap, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

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
        catch { }

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
        catch { }

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
        catch { }

        // LohFreeBlockIndex.bin — free block gaps inside LOH/POH segments
        try
        {
            progress?.Report(new(0, "writing LohFreeBlockIndex.bin", Detail: null, Elapsed: stopwatch.Elapsed));
            LohFreeBlockWriter.Write(DumpIndexPaths.LohFreeBlockIndex(dumpPath), heap, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // ── Type classification helpers ────────────────────────────────────────────

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
        try   { return (int)segment.GetGeneration(address); }
        catch { return -1; }
    }

    // ── ObjectIndex.bin header ─────────────────────────────────────────────────
    // Uses the existing format (not IndexHeader) to preserve backward compatibility
    // with any existing ObjectIndex.bin files.

    private static void WriteObjIndexHeader(Stream stream, long recordCount)
    {
        Span<byte> buf = stackalloc byte[ObjIndexHeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(buf,       ObjIndexMagic);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..],  ObjIndexVersion);
        BinaryPrimitives.WriteInt64LittleEndian(buf[8..],  DateTime.UtcNow.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(buf[16..], recordCount);
        stream.Write(buf);
    }
}
