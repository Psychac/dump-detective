using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using DumpHeapSegmentKind = DumpDetective.Analysis.Models.HeapSegmentKind;
using SdkHeapObjectRef = DumpDetective.Sdk.Analysis.HeapObjectRef;
using SdkHeapSegmentRef = DumpDetective.Sdk.Analysis.HeapSegmentRef;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing segment
/// data through <see cref="IHeapSegmentQuery"/> (the <c>heap.segments</c> capability, including its
/// per-segment <see cref="IHeapSegmentQuery.EnumerateObjects"/>) and exact-index facts through
/// <see cref="IHeapTypeStatisticsQuery"/>. Runs through the existing pipeline via
/// <see cref="HeapTopologyAnalyzerLegacyAdapter"/>.
///
/// Classifies all managed heap segments (SOH, LOH, POH, Frozen) and produces a
/// <see cref="HeapTopologyDomainResult"/> with per-kind size and object count totals. SOH is never
/// walked per-object (it dominates object count — 87 M+ objects on large dumps — and is the main
/// cost driver); its exact object count is instead derived as
/// <c>Phase1TotalObjectCount - LohCount - PohCount - FrozenCount</c>, free once Phase 1's
/// already-exact total is available.
/// </summary>
public sealed class HeapTopologyAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "Heap Topology";
    public string Category => "Memory";

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapSegmentQuery segmentQuery = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapTypeStatisticsQuery statsQuery = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");

        LastResult = Analyze(segmentQuery, statsQuery, context.Progress, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static AnalyzerDomainResult Analyze(
        IHeapSegmentQuery segmentQuery,
        IHeapTypeStatisticsQuery statsQuery,
        IProgress<AnalyzerProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        List<SdkHeapSegmentRef> segments = segmentQuery.EnumerateSegments().ToList();
        int totalSegments = segments.Count;

        progress?.Report(new(0, "classifying heap segments", $"0 / {totalSegments} segments"));

        ulong sohBytes = 0, lohBytes = 0, pohBytes = 0, frozenBytes = 0, unknownBytes = 0;
        ulong sohUsedBytes = 0, lohUsedBytes = 0, pohUsedBytes = 0, frozenUsedBytes = 0, unknownUsedBytes = 0;
        ulong sohReserved = 0, lohReserved = 0, pohReserved = 0, frozenReserved = 0, unknownReserved = 0;
        ulong gen0Bytes = 0, gen1Bytes = 0, gen2Bytes = 0;
        ulong sohFragmented = 0, lohFragmented = 0, pohFragmented = 0, frozenFragmented = 0, unknownFragmented = 0;
        int sohCount = 0, lohCount = 0, pohCount = 0, frozenCount = 0, unknownCount = 0;
        long sohObjects = 0, lohObjects = 0, pohObjects = 0, frozenObjects = 0, unknownObjects = 0;
        long totalObjectsScanned = 0;
        int segmentsProcessed = 0;

        var snapshots = new List<HeapSegmentSnapshot>(totalSegments);
        var bytesByLogicalHeap = new Dictionary<int, ulong>();
        var objectsByLogicalHeap = new Dictionary<int, long>();
        var segmentCountByLogicalHeap = new Dictionary<int, int>();
        var pohTypes = new Dictionary<string, SegmentTypeAccumulator>(StringComparer.Ordinal);
        var frozenTypes = new Dictionary<string, SegmentTypeAccumulator>(StringComparer.Ordinal);

        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            SdkHeapSegmentRef segment = segments[segmentIndex];
            DumpHeapSegmentKind kind = SdkSegmentKindMapper.ToDump(segment.Kind);
            ulong committed = segment.CommittedBytes;
            ulong reserved = segment.ReservedBytes;
            ulong used = 0;
            ulong start = segment.Start;
            ulong end = segment.End;
            ulong length = end > start ? end - start : 0;
            int logicalHeapIndex = segment.LogicalHeapIndex;

            ulong segGen0Bytes = segment.Gen0Bytes, segGen1Bytes = segment.Gen1Bytes, segGen2Bytes = segment.Gen2Bytes;
            if (kind == DumpHeapSegmentKind.SmallObjectHeap)
            {
                gen0Bytes += segGen0Bytes;
                gen1Bytes += segGen1Bytes;
                gen2Bytes += segGen2Bytes;
            }

            Dictionary<string, SegmentTypeAccumulator>? typeStats = kind switch
            {
                DumpHeapSegmentKind.PinnedObjectHeap => pohTypes,
                DumpHeapSegmentKind.Frozen => frozenTypes,
                _ => null
            };

            long objCount = CountObjects(segmentQuery, segment, kind, ref totalObjectsScanned, ref used, progress, typeStats, cancellationToken);
            if (logicalHeapIndex >= 0)
            {
                if (bytesByLogicalHeap.TryGetValue(logicalHeapIndex, out ulong existingBytes))
                    bytesByLogicalHeap[logicalHeapIndex] = existingBytes + committed;
                else
                    bytesByLogicalHeap[logicalHeapIndex] = committed;

                if (segmentCountByLogicalHeap.TryGetValue(logicalHeapIndex, out int existingSegments))
                    segmentCountByLogicalHeap[logicalHeapIndex] = existingSegments + 1;
                else
                    segmentCountByLogicalHeap[logicalHeapIndex] = 1;

                if (objCount < 0)
                {
                    objectsByLogicalHeap[logicalHeapIndex] = -1;
                }
                else if (objectsByLogicalHeap.TryGetValue(logicalHeapIndex, out long existingObjects) && existingObjects >= 0)
                {
                    objectsByLogicalHeap[logicalHeapIndex] = existingObjects + objCount;
                }
                else if (!objectsByLogicalHeap.ContainsKey(logicalHeapIndex))
                {
                    objectsByLogicalHeap[logicalHeapIndex] = objCount;
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
                Generation: logicalHeapIndex,
                ObjectCount: objCount,
                Gen0Bytes: segGen0Bytes,
                Gen1Bytes: segGen1Bytes,
                Gen2Bytes: segGen2Bytes));

            switch (kind)
            {
                case DumpHeapSegmentKind.SmallObjectHeap:
                    sohUsedBytes += used;
                    sohFragmented += fragmented;
                    break;
                case DumpHeapSegmentKind.LargeObjectHeap:
                    lohUsedBytes += used;
                    lohFragmented += fragmented;
                    break;
                case DumpHeapSegmentKind.PinnedObjectHeap:
                    pohUsedBytes += used;
                    pohFragmented += fragmented;
                    break;
                case DumpHeapSegmentKind.Frozen:
                    frozenUsedBytes += used;
                    frozenFragmented += fragmented;
                    break;
                case DumpHeapSegmentKind.Unknown:
                    unknownUsedBytes += used;
                    unknownFragmented += fragmented;
                    break;
            }

            // objCount == -1 is the sentinel for "SOH not counted" — do not add to totals.
            long countedObj = objCount >= 0 ? objCount : 0;
            switch (kind)
            {
                case DumpHeapSegmentKind.SmallObjectHeap:
                    sohCount++;
                    sohBytes += committed;
                    sohReserved += reserved;
                    if (objCount >= 0) sohObjects += countedObj; else sohObjects = -1;
                    break;
                case DumpHeapSegmentKind.LargeObjectHeap:
                    lohCount++;
                    lohBytes += committed;
                    lohReserved += reserved;
                    lohObjects += countedObj;
                    break;
                case DumpHeapSegmentKind.PinnedObjectHeap:
                    pohCount++;
                    pohBytes += committed;
                    pohReserved += reserved;
                    pohObjects += countedObj;
                    break;
                case DumpHeapSegmentKind.Frozen:
                    frozenCount++;
                    frozenBytes += committed;
                    frozenReserved += reserved;
                    frozenObjects += countedObj;
                    break;
                case DumpHeapSegmentKind.Unknown:
                default:
                    // Genuinely unrecognized segment kind (corrupted dump or a newer ClrMD enum
                    // member SegmentKindMapper hasn't been updated for). Tracked separately rather
                    // than silently folded into SOH, so a corrupted dump is visible in the report
                    // instead of quietly skewing SOH totals.
                    unknownCount++;
                    unknownBytes += committed;
                    unknownReserved += reserved;
                    unknownObjects += countedObj;
                    break;
            }
        }

        // SOH was never walked, so sohUsedBytes is still 0 and sohFragmented (accumulated as
        // committed - 0 in the loop above) equals sohBytes — i.e. "100% fragmented", which is
        // wrong. Reset it; it is only meaningful once derived below from Phase 1's exact totals.
        sohFragmented = 0;

        // Exact SOH object count and used bytes, free: Phase 1's already-exact heap-wide totals
        // minus the already-cheap LOH/POH/Frozen/Unknown walks above — zero additional heap traversal.
        if (statsQuery.ExactObjectCount is long exactObjectCount)
        {
            long derivedSohObjects = exactObjectCount - lohObjects - pohObjects - frozenObjects - unknownObjects;
            sohObjects = Math.Max(derivedSohObjects, 0);

            ulong totalIndexedBytes = statsQuery.GetTotalIndexedBytes();

            ulong nonSohUsedBytes = lohUsedBytes + pohUsedBytes + frozenUsedBytes + unknownUsedBytes;
            sohUsedBytes = totalIndexedBytes > nonSohUsedBytes ? totalIndexedBytes - nonSohUsedBytes : 0;
            sohFragmented = sohBytes > sohUsedBytes ? sohBytes - sohUsedBytes : 0;
        }

        ulong totalCommitted = sohBytes + lohBytes + pohBytes + frozenBytes + unknownBytes;
        ulong totalUsed = sohUsedBytes + lohUsedBytes + pohUsedBytes + frozenUsedBytes + unknownUsedBytes;
        ulong totalReserved = sohReserved + lohReserved + pohReserved + frozenReserved + unknownReserved;
        ulong reservationGap = totalReserved > totalCommitted ? totalReserved - totalCommitted : 0;
        double frozenPercent = totalCommitted == 0 ? 0.0 : frozenBytes * 100.0 / totalCommitted;
        double lohPercent = totalCommitted == 0 ? 0.0 : lohBytes * 100.0 / totalCommitted;
        double pohPercent = totalCommitted == 0 ? 0.0 : pohBytes * 100.0 / totalCommitted;

        var kindSummaries = new List<SegmentKindSummary>
        {
                new(DumpHeapSegmentKind.SmallObjectHeap, sohCount, sohObjects, sohBytes, sohReserved),
                new(DumpHeapSegmentKind.LargeObjectHeap, lohCount, lohObjects, lohBytes, lohReserved),
                new(DumpHeapSegmentKind.PinnedObjectHeap, pohCount, pohObjects, pohBytes, pohReserved),
                new(DumpHeapSegmentKind.Frozen, frozenCount, frozenObjects, frozenBytes, frozenReserved),
                new(DumpHeapSegmentKind.Unknown, unknownCount, unknownObjects, unknownBytes, unknownReserved),
        };

        var logicalHeapSummaries = new List<PerLogicalHeapSummary>(bytesByLogicalHeap.Count);
        foreach (int heapIndex in bytesByLogicalHeap.Keys.OrderBy(index => index))
        {
            bytesByLogicalHeap.TryGetValue(heapIndex, out ulong heapBytes);
            segmentCountByLogicalHeap.TryGetValue(heapIndex, out int heapSegments);
            objectsByLogicalHeap.TryGetValue(heapIndex, out long heapObjects);
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
            IsServerGc: segmentQuery.IsServerGc,
            LogicalHeapCount: segmentQuery.LogicalHeapCount,
            KindSummaries: kindSummaries,
            PerLogicalHeapSummaries: logicalHeapSummaries,
            TopPohTypes: topPohTypes,
            TopFrozenTypes: topFrozenTypes,
            TopSegmentsBySize: topBySize);
    }

    public void Dispose() { }

    /// <summary>
    /// Progress-cadence note: unlike the pre-retyping version (which counted every enumerated
    /// <c>ClrObject</c>, including invalid/free ones, toward the report-interval bitmask before
    /// filtering), <see cref="IHeapSegmentQuery.EnumerateObjects"/> already excludes invalid/free/
    /// untyped objects at the capability boundary — so this loop's <c>localScanned</c> only ever
    /// counts objects that would have passed the old filter too. Progress messages may fire at
    /// slightly different points on a segment with free blocks; the returned count, bytes, and
    /// per-type accumulators — the only pieces that reach <see cref="AnalyzerDomainResult"/> — are
    /// unaffected, since progress is a side channel, not part of the domain result.
    /// </summary>
    private static long CountObjects(
        IHeapSegmentQuery segmentQuery,
        SdkHeapSegmentRef segment,
        DumpHeapSegmentKind kind,
        ref long totalObjectsScanned,
        ref ulong usedBytes,
        IProgress<AnalyzerProgressReport>? progress,
        Dictionary<string, SegmentTypeAccumulator>? typeStats,
        CancellationToken cancellationToken)
    {
        // SOH holds the vast majority of objects on large dumps (O(87M) on large dumps) — never
        // walked per-object here. Its exact total is derived arithmetically after the segment loop
        // (see Analyze) from Phase 1's already-exact heap-wide object count.
        if (kind == DumpHeapSegmentKind.SmallObjectHeap)
            return -1; // sentinel: "not walked here" — caller overwrites the SOH total via arithmetic

        long count = 0;
        // Only flood-report progress for LOH/POH; SOH has too many segments to flood (never reached
        // here, but Unknown/Frozen stay quiet the same way the pre-retyping code kept them).
        bool reportInner = progress is not null
            && kind is DumpHeapSegmentKind.LargeObjectHeap or DumpHeapSegmentKind.PinnedObjectHeap;
        long localScanned = 0;

        foreach (SdkHeapObjectRef obj in segmentQuery.EnumerateObjects(segment))
        {
            count++;
            usedBytes += obj.Size;

            if (typeStats is not null)
            {
                string typeName = obj.TypeDisplayName;
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    if (!typeStats.TryGetValue(typeName, out SegmentTypeAccumulator? acc))
                    {
                        acc = new SegmentTypeAccumulator();
                        typeStats[typeName] = acc;
                    }

                    acc.Count++;
                    acc.TotalBytes += obj.Size;
                }
            }

            localScanned++;

            if (reportInner && (localScanned & (HeapTopologyAnalyzerOptions.ReportObjectScanInterval - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class SegmentTypeAccumulator
    {
        public int Count { get; set; }
        public ulong TotalBytes { get; set; }
    }
}
