using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Classifies all managed heap segments (SOH, LOH, POH, Frozen) and produces a
/// <see cref="HeapTopologyDomainResult"/> with per-kind size and object count totals.
/// Operates directly on <see cref="ClrHeap.Segments"/>.
/// Per-object counting is skipped for SOH by default (see <see cref="HeapTopologyAnalysisOptions.CountSohObjects"/>)
/// since SOH dominates object count (87 M+ objects on large dumps) and is the main cost driver.
/// </summary>
public sealed class HeapTopologyAnalyzer : IAnalyzer
{
    public string Name => "Heap Topology";
    public string Category => "Memory";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opts = context.AnalysisOptions.HeapTopology;
        return ValueTask.FromResult(Analyze(context.Heap, context.Progress, opts.CountSohObjects).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(ClrHeap heap, IProgress<AnalyzerProgressReport>? progress, bool countSoh)
    {
        // heap.Segments is backed by a fixed list in ClrMD — enumerate twice rather than ToList(),
        // keeping one extra List<T> allocation off the heap for large dumps.
        int totalSegments = 0;
        foreach (ClrSegment _ in heap.Segments) totalSegments++;

        progress?.Report(new(0, "classifying heap segments", $"0 / {totalSegments} segments"));

        ulong sohBytes = 0, lohBytes = 0, pohBytes = 0, frozenBytes = 0;
        ulong sohUsedBytes = 0, lohUsedBytes = 0, pohUsedBytes = 0, frozenUsedBytes = 0;
        ulong sohReserved = 0, lohReserved = 0, pohReserved = 0, frozenReserved = 0;
        ulong gen0Bytes = 0, gen1Bytes = 0, gen2Bytes = 0;
        ulong sohFragmented = 0, lohFragmented = 0, pohFragmented = 0, frozenFragmented = 0;
        int sohCount = 0, lohCount = 0, pohCount = 0, frozenCount = 0;
        int sohObjects = 0, lohObjects = 0, pohObjects = 0, frozenObjects = 0;
        long totalObjectsScanned = 0;
        int segmentsProcessed = 0;

        var snapshots = new List<HeapSegmentSnapshot>(totalSegments);
        var bytesByLogicalHeap = new Dictionary<int, ulong>();
        var objectsByLogicalHeap = new Dictionary<int, int>();
        var segmentCountByLogicalHeap = new Dictionary<int, int>();
        var pohTypes = new Dictionary<string, SegmentTypeAccumulator>(StringComparer.Ordinal);
        var frozenTypes = new Dictionary<string, SegmentTypeAccumulator>(StringComparer.Ordinal);

        foreach (ClrSegment segment in heap.Segments)
        {
            HeapSegmentKind kind = SegmentKindMapper.Map(segment);
            ulong committed = SegmentKindMapper.GetCommittedBytes(segment);
            ulong reserved = SegmentKindMapper.GetReservedBytes(segment);
            ulong used = 0;
            ulong start = segment.Start;
            ulong end = segment.End;
            ulong length = end > start ? end - start : 0;
            int generation = segment.SubHeap?.Index ?? -1;

            // Extract generation range sizes for SOH segments
            ulong segGen0Bytes = 0, segGen1Bytes = 0, segGen2Bytes = 0;
            if (kind == HeapSegmentKind.SmallObjectHeap)
            {
                segGen0Bytes = SegmentKindMapper.GetRangeLength(segment.Generation0);
                segGen1Bytes = SegmentKindMapper.GetRangeLength(segment.Generation1);
                segGen2Bytes = SegmentKindMapper.GetRangeLength(segment.Generation2);
                gen0Bytes += segGen0Bytes;
                gen1Bytes += segGen1Bytes;
                gen2Bytes += segGen2Bytes;
            }

            Dictionary<string, SegmentTypeAccumulator>? typeStats = kind switch
            {
                HeapSegmentKind.PinnedObjectHeap => pohTypes,
                HeapSegmentKind.Frozen => frozenTypes,
                _ => null
            };

            int objCount = CountObjects(segment, kind, countSoh, ref totalObjectsScanned, ref used, progress, typeStats);
            if (generation >= 0)
            {
                if (bytesByLogicalHeap.TryGetValue(generation, out ulong existingBytes))
                    bytesByLogicalHeap[generation] = existingBytes + committed;
                else
                    bytesByLogicalHeap[generation] = committed;

                if (segmentCountByLogicalHeap.TryGetValue(generation, out int existingSegments))
                    segmentCountByLogicalHeap[generation] = existingSegments + 1;
                else
                    segmentCountByLogicalHeap[generation] = 1;

                if (objCount < 0)
                {
                    objectsByLogicalHeap[generation] = -1;
                }
                else if (objectsByLogicalHeap.TryGetValue(generation, out int existingObjects) && existingObjects >= 0)
                {
                    objectsByLogicalHeap[generation] = existingObjects + objCount;
                }
                else if (!objectsByLogicalHeap.ContainsKey(generation))
                {
                    objectsByLogicalHeap[generation] = objCount;
                }
            }

            segmentsProcessed++;
            progress?.Report(new(
                ScannedCount: totalObjectsScanned,
                Phase: "classifying heap segments",
                Detail: $"{segmentsProcessed} / {totalSegments} segments, {totalObjectsScanned:N0} objects"));

            ulong fragmented = committed > used ? committed - used : 0;

            snapshots.Add(new HeapSegmentSnapshot(
                Address: segment.Address,
                Start: start,
                End: end,
                Length: length,
                CommittedBytes: committed,
                UsedBytes: used,
                ReservedBytes: reserved,
                Kind: kind,
                Generation: generation,
                ObjectCount: objCount,
                Gen0Bytes: segGen0Bytes,
                Gen1Bytes: segGen1Bytes,
                Gen2Bytes: segGen2Bytes));

            switch (kind)
            {
                case HeapSegmentKind.SmallObjectHeap:
                    sohUsedBytes += used;
                    sohFragmented += fragmented;
                    break;
                case HeapSegmentKind.LargeObjectHeap:
                    lohUsedBytes += used;
                    lohFragmented += fragmented;
                    break;
                case HeapSegmentKind.PinnedObjectHeap:
                    pohUsedBytes += used;
                    pohFragmented += fragmented;
                    break;
                case HeapSegmentKind.Frozen:
                    frozenUsedBytes += used;
                    frozenFragmented += fragmented;
                    break;
            }

            // objCount == -1 is the sentinel for "SOH not counted" — do not add to totals.
            int countedObj = objCount >= 0 ? objCount : 0;
            switch (kind)
            {
                case HeapSegmentKind.SmallObjectHeap:
                    sohCount++;
                    sohBytes += committed;
                    sohReserved += reserved;
                    if (objCount >= 0) sohObjects += countedObj; else sohObjects = -1;
                    break;
                case HeapSegmentKind.LargeObjectHeap:
                    lohCount++;
                    lohBytes += committed;
                    lohReserved += reserved;
                    lohObjects += countedObj;
                    break;
                case HeapSegmentKind.PinnedObjectHeap:
                    pohCount++;
                    pohBytes += committed;
                    pohReserved += reserved;
                    pohObjects += countedObj;
                    break;
                case HeapSegmentKind.Frozen:
                    frozenCount++;
                    frozenBytes += committed;
                    frozenReserved += reserved;
                    frozenObjects += countedObj;
                    break;
                default:
                    sohCount++;
                    sohBytes += committed;
                    sohReserved += reserved;
                    if (objCount >= 0) sohObjects += countedObj; else sohObjects = -1;
                    break;
            }
        }

        ulong totalCommitted = sohBytes + lohBytes + pohBytes + frozenBytes;
        ulong totalUsed = sohUsedBytes + lohUsedBytes + pohUsedBytes + frozenUsedBytes;
        ulong totalReserved = sohReserved + lohReserved + pohReserved + frozenReserved;
        ulong reservationGap = totalReserved > totalCommitted ? totalReserved - totalCommitted : 0;
        double frozenPercent = totalCommitted == 0 ? 0.0 : frozenBytes * 100.0 / totalCommitted;
        double lohPercent = totalCommitted == 0 ? 0.0 : lohBytes * 100.0 / totalCommitted;
        double pohPercent = totalCommitted == 0 ? 0.0 : pohBytes * 100.0 / totalCommitted;

        var kindSummaries = new List<SegmentKindSummary>
        {
                new(HeapSegmentKind.SmallObjectHeap, sohCount, sohObjects, sohBytes, sohReserved),
                new(HeapSegmentKind.LargeObjectHeap, lohCount, lohObjects, lohBytes, lohReserved),
                new(HeapSegmentKind.PinnedObjectHeap, pohCount, pohObjects, pohBytes, pohReserved),
                new(HeapSegmentKind.Frozen, frozenCount, frozenObjects, frozenBytes, frozenReserved),
        };

        var logicalHeapSummaries = new List<PerLogicalHeapSummary>(bytesByLogicalHeap.Count);
        foreach (int heapIndex in bytesByLogicalHeap.Keys.OrderBy(index => index))
        {
            bytesByLogicalHeap.TryGetValue(heapIndex, out ulong heapBytes);
            segmentCountByLogicalHeap.TryGetValue(heapIndex, out int heapSegments);
            objectsByLogicalHeap.TryGetValue(heapIndex, out int heapObjects);
            logicalHeapSummaries.Add(new PerLogicalHeapSummary(heapIndex, heapBytes, heapObjects, heapSegments));
        }

        var topBySize = snapshots
            .OrderByDescending(s => s.CommittedBytes)
            .Take(HeapTopologyAnalyzerOptions.TopSegmentsCount)
            .ToArray();

        var topPohTypes = BuildTopTypeSnapshots(pohTypes, HeapTopologyAnalyzerOptions.TopSegmentsCount);
        var topFrozenTypes = BuildTopTypeSnapshots(frozenTypes, HeapTopologyAnalyzerOptions.TopSegmentsCount);

        progress?.Report(new(
            ScannedCount: totalObjectsScanned,
            Phase: "aggregating results",
            Detail: $"{snapshots.Count} segments, {totalObjectsScanned:N0} objects total"));

        return new HeapTopologyDomainResult(
            TotalSegments: snapshots.Count,
            TotalCommittedBytes: totalCommitted,
            TotalUsedBytes: totalUsed,
            TotalReservedBytes: totalReserved,
            ReservationGapBytes: reservationGap,
            SohSegmentCount: sohCount,
            SohBytes: sohBytes,
            LohSegmentCount: lohCount,
            LohBytes: lohBytes,
            PohSegmentCount: pohCount,
            PohBytes: pohBytes,
            FrozenSegmentCount: frozenCount,
            FrozenBytes: frozenBytes,
            FrozenPercent: frozenPercent,
            LohPercent: lohPercent,
            PohPercent: pohPercent,
            Gen0Bytes: gen0Bytes,
            Gen1Bytes: gen1Bytes,
            Gen2Bytes: gen2Bytes,
            SohFragmentedBytes: sohFragmented,
            LohFragmentedBytes: lohFragmented,
            PohFragmentedBytes: pohFragmented,
            FrozenFragmentedBytes: frozenFragmented,
            CountSohObjects: countSoh,
            KindSummaries: kindSummaries,
            PerLogicalHeapSummaries: logicalHeapSummaries,
            TopPohTypes: topPohTypes,
            TopFrozenTypes: topFrozenTypes,
            TopSegmentsBySize: topBySize);
    }

    public void Dispose() { }

    private static int CountObjects(
        ClrSegment segment,
        HeapSegmentKind kind,
        bool countSoh,
        ref long totalObjectsScanned,
        ref ulong usedBytes,
        IProgress<AnalyzerProgressReport>? progress,
        Dictionary<string, SegmentTypeAccumulator>? typeStats = null)
    {
        // SOH holds the vast majority of objects on large dumps.
        // Skip enumeration unless explicitly requested to avoid O(87M) scans.
        if (kind == HeapSegmentKind.SmallObjectHeap && !countSoh)
            return -1; // sentinel: "not counted" — distinguished from a genuine zero in the report

        int count = 0;
        // Only flood-report progress for LOH/POH; SOH has too many segments to flood.
        bool reportInner = progress is not null
            && kind is HeapSegmentKind.LargeObjectHeap or HeapSegmentKind.PinnedObjectHeap;
        long localScanned = 0;

        foreach (ClrObject obj in segment.EnumerateObjects())
        {
            if (obj.IsValid && !obj.IsFree)
            {
                count++;
                usedBytes += obj.Size;

                if (typeStats is not null && obj.Type is not null)
                {
                    string typeName = obj.Type.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(typeName))
                    {
                        if (!typeStats.TryGetValue(typeName, out SegmentTypeAccumulator acc))
                            acc = new SegmentTypeAccumulator();

                        acc.Count++;
                        acc.TotalBytes += obj.Size;
                        typeStats[typeName] = acc;
                    }
                }
            }

            localScanned++;

            if (reportInner && (localScanned & (HeapTopologyAnalyzerOptions.ReportObjectScanInterval - 1)) == 0)
            {
                totalObjectsScanned += localScanned;
                localScanned = 0;
                progress!.Report(new(
                    ScannedCount: totalObjectsScanned,
                    Phase: "scanning segment objects",
                    Detail: $"{totalObjectsScanned:N0} objects"));
            }
        }

        totalObjectsScanned += localScanned;
        return count;
    }

    private static IReadOnlyList<TypeSnapshot> BuildTopTypeSnapshots(Dictionary<string, SegmentTypeAccumulator> typeStats, int limit)
    {
        if (typeStats.Count == 0)
            return [];

        var snapshots = new List<TypeSnapshot>(typeStats.Count);
        foreach (KeyValuePair<string, SegmentTypeAccumulator> kvp in typeStats)
        {
            int count = kvp.Value.Count;
            ulong totalBytes = kvp.Value.TotalBytes;
            ulong averageSize = count > 0 ? totalBytes / (ulong)count : 0;
            snapshots.Add(new TypeSnapshot(kvp.Key, count, totalBytes, 0, averageSize));
        }

        snapshots.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        if (snapshots.Count > limit)
            snapshots.RemoveRange(limit, snapshots.Count - limit);

        return snapshots;
    }

    private struct SegmentTypeAccumulator
    {
        public int Count { get; set; }
        public ulong TotalBytes { get; set; }
    }
}
