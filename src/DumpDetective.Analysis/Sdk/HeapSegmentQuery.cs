using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Core.Abstractions;
using DumpDetective.Platform.Storage.Container;

using Microsoft.Diagnostics.Runtime;

using System.Buffers.Binary;

using SdkHeapFreeBlockRef = DumpDetective.Sdk.Analysis.HeapFreeBlockRef;
using SdkHeapObjectRef = DumpDetective.Sdk.Analysis.HeapObjectRef;
using SdkHeapSegmentRef = DumpDetective.Sdk.Analysis.HeapSegmentRef;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.segments</c> capability, built for the
/// <c>SegmentReservationAnalyzer</c>/<c>HeapTopologyAnalyzer</c> retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Lives here rather than a
/// dedicated <c>Sources.ClrDump</c> project for the same reason as
/// <see cref="HeapTypeStatisticsQuery"/> — that project doesn't exist yet.
/// </summary>
/// <remarks>
/// Delegates to <c>HeapAnalysisCache.GetOrBuildSegmentSummaries</c> — the one shared per-run pass
/// the two analyzers this batch retypes already both used directly — falling back to a fresh
/// <c>SegmentSummaryCache.Build</c> when <paramref name="cache"/> isn't the concrete
/// <c>HeapAnalysisCache</c> (e.g. a bare <c>IHeapAnalysisCache</c> test double), matching the
/// fallback both analyzers' own pre-retyping code already had.
/// </remarks>
internal sealed class HeapSegmentQuery(ClrHeap heap, ClrRuntime runtime, IHeapAnalysisCache cache) : Sdk.Analysis.IHeapSegmentQuery
{
    public int DumpPointerSize => runtime.DataTarget.DataReader.PointerSize;

    public bool IsServerGc => heap.IsServer;

    public int LogicalHeapCount => heap.SubHeaps.Length;

    public bool CanWalkHeap => heap.CanWalkHeap;

    public bool HasLohSatelliteIndex => TryGetIndexPath(out _);

    private bool TryGetIndexPath([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? indexPath)
    {
        if (cache is IHeapIndexBuilder builder && builder.TryGetHeapIndex(out HeapIndexBuildResult? idx) && idx.IndexPath.Length > 0)
        {
            indexPath = idx.IndexPath;
            return true;
        }

        indexPath = null;
        return false;
    }

    public IEnumerable<SdkHeapSegmentRef> EnumerateSegments()
    {
        IReadOnlyList<SegmentSummary> summaries = cache is HeapAnalysisCache heapCache
            ? heapCache.GetOrBuildSegmentSummaries(heap)
            : SegmentSummaryCache.Build(heap);

        foreach (SegmentSummary summary in summaries)
            yield return ToRef(summary);
    }

    public bool TryGetSegment(ulong address, out SdkHeapSegmentRef segment)
    {
        foreach (SdkHeapSegmentRef candidate in EnumerateSegments())
        {
            if (address >= candidate.Start && address < candidate.End)
            {
                segment = candidate;
                return true;
            }
        }

        segment = default;
        return false;
    }

    public IEnumerable<SdkHeapObjectRef> EnumerateObjects(SdkHeapSegmentRef segment, bool includeFree = false)
    {
        ClrSegment? clrSegment = heap.GetSegmentByAddress(segment.Start);
        if (clrSegment is null)
            yield break;

        foreach (ClrObject obj in clrSegment.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || (obj.IsFree && !includeFree))
                continue;

            string name = obj.Type.Name ?? $"MT:0x{obj.Type.MethodTable:x}";
            yield return new SdkHeapObjectRef(obj.Address, SdkTypeRefFactory.FromName(name, obj.Type.MethodTable), obj.Size, name, IsFree: obj.IsFree);
        }
    }

    public Sdk.Analysis.HeapGenerationTag GetGeneration(ulong address)
    {
        // Faithful port of Traversal.Dominator.GenerationTagResolver.Resolve, kept as its own
        // small copy rather than a shared call site — that resolver lives in Core-facing
        // Traversal code with its own ClrHeap-shaped caller (DiskBackedObjectIndexWriter), and
        // duplicating ~15 lines here avoids coupling the two for no shared benefit.
        ClrSegment? seg = heap.GetSegmentByAddress(address);
        if (seg is null)
            return Sdk.Analysis.HeapGenerationTag.Unknown;

        switch (seg.Kind)
        {
            case GCSegmentKind.Large:
                return Sdk.Analysis.HeapGenerationTag.Loh;
            case GCSegmentKind.Pinned:
                return Sdk.Analysis.HeapGenerationTag.Poh;
            case GCSegmentKind.Frozen:
                return Sdk.Analysis.HeapGenerationTag.Frozen;
            case GCSegmentKind.Generation2:
                return Sdk.Analysis.HeapGenerationTag.Gen2;
            case GCSegmentKind.Generation1:
                return Sdk.Analysis.HeapGenerationTag.Gen1;
            case GCSegmentKind.Generation0:
                return Sdk.Analysis.HeapGenerationTag.Gen0;
            default:
                // Ephemeral (workstation GC) segment holds gen0/1/2 together — resolve per-object.
                try
                {
                    return seg.GetGeneration(address) switch
                    {
                        Generation.Generation0 => Sdk.Analysis.HeapGenerationTag.Gen0,
                        Generation.Generation1 => Sdk.Analysis.HeapGenerationTag.Gen1,
                        Generation.Generation2 => Sdk.Analysis.HeapGenerationTag.Gen2,
                        _ => Sdk.Analysis.HeapGenerationTag.Unknown,
                    };
                }
                catch
                {
                    return Sdk.Analysis.HeapGenerationTag.Unknown;
                }
        }
    }

    public IEnumerable<SdkHeapFreeBlockRef> EnumerateLohFreeBlocks()
    {
        if (TryGetIndexPath(out string? indexPath))
        {
            foreach (SdkHeapFreeBlockRef block in ReadIndexedLohFreeBlocks(indexPath))
                yield return block;
            yield break;
        }

        foreach (SdkHeapSegmentRef segment in EnumerateSegments())
        {
            if (segment.Kind != Sdk.Analysis.HeapSegmentKind.LargeObjectHeap && segment.Kind != Sdk.Analysis.HeapSegmentKind.PinnedObjectHeap)
                continue;

            foreach (SdkHeapObjectRef obj in EnumerateObjects(segment, includeFree: true))
            {
                if (obj.IsFree)
                    yield return new SdkHeapFreeBlockRef(segment.Start, obj.Address, obj.Size);
            }
        }
    }

    // Not an iterator itself — a try/catch around a yield-containing block is illegal (CS1626), and
    // this needs one to match ReadFreeBlocks' "section missing or corrupted: process without free
    // blocks" fallback contract. Reads eagerly into a list (bounded — LOH/POH fragmentation is never
    // heap-object-scale), then EnumerateLohFreeBlocks streams from it.
    private static List<SdkHeapFreeBlockRef> ReadIndexedLohFreeBlocks(string containerPath)
    {
        var result = new List<SdkHeapFreeBlockRef>();
        try
        {
            if (!CacheSectionHelper.TryOpenCacheSection(containerPath, CacheSectionId.LohFreeBlocks, out Stream? stream) || stream is null)
                return result;

            using (stream)
            {
                if (!IndexHeader.TryRead(stream, out IndexHeader header))
                    return result;

                const int RecordSize = 24; // SegmentAddress(8) | Offset(8) | Size(8)
                Span<byte> rec = stackalloc byte[RecordSize];
                for (long i = 0; i < header.RecordCount; i++)
                {
                    if (stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false) < RecordSize)
                        break;

                    ulong segAddr = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                    ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                    ulong size = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);
                    result.Add(new SdkHeapFreeBlockRef(segAddr, segAddr + offset, size));
                }
            }
        }
        catch (Exception)
        {
            // Section not found or read failed; caller processes whatever was read so far.
        }

        return result;
    }

    public IEnumerable<SdkHeapObjectRef> EnumerateCapturedLargeObjects()
    {
        if (!TryGetIndexPath(out string? indexPath))
            yield break;

        foreach ((ulong address, ulong methodTable, ulong size) in ReadCapturedLargeObjectRecords(indexPath))
        {
            ClrType? type = heap.GetTypeByMethodTable(methodTable);
            if (type is null)
                continue;

            string typeName = type.Name ?? "Unknown";
            if (string.Equals(typeName, "Free", StringComparison.Ordinal))
                continue;

            yield return new SdkHeapObjectRef(address, SdkTypeRefFactory.FromName(typeName, methodTable), size, typeName);
        }
    }

    private static List<(ulong Address, ulong MethodTable, ulong Size)> ReadCapturedLargeObjectRecords(string containerPath)
    {
        var result = new List<(ulong Address, ulong MethodTable, ulong Size)>();
        LargeObjectTracker.ReadRecords(containerPath, (address, mt, size) => result.Add((address, mt, size)));
        // Rank by size descending — matches this method's own documented contract; the index file
        // is already written in this order, but re-sorting here doesn't rely on that.
        result.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return result;
    }

    private static SdkHeapSegmentRef ToRef(SegmentSummary summary) => new()
    {
        Start = summary.Segment.Start,
        End = summary.Segment.End,
        Address = summary.Segment.Address,
        Generation = SdkSegmentKindMapper.ToLegacyGenerationNumber(summary.RegionKind),
        Kind = SdkSegmentKindMapper.ToSdk(summary.Kind),
        RegionKind = SdkSegmentKindMapper.ToSdk(summary.RegionKind),
        CommittedBytes = summary.CommittedBytes,
        ReservedBytes = summary.ReservedBytes,
        LogicalHeapIndex = summary.LogicalHeapIndex,
        IsEphemeral = summary.IsEphemeral,
        Gen0Bytes = summary.Gen0Bytes,
        Gen1Bytes = summary.Gen1Bytes,
        Gen2Bytes = summary.Gen2Bytes,
    };
}
