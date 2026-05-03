using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Classifies all managed heap segments (SOH, LOH, POH, Frozen) and produces a
/// <see cref="SegmentAnalysisDomainResult"/> with per-kind size and object count totals.
/// Operates directly on <see cref="ClrHeap.Segments"/>.
/// Per-object counting is skipped for SOH by default (see <see cref="SegmentAnalysisOptions.CountSohObjects"/>)
/// since SOH dominates object count (87 M+ objects on large dumps) and is the main cost driver.
/// </summary>
public sealed class SegmentAnalyzer : IAnalyzer
{
    public string Name => "Segment Analysis";
    public string Category => "Memory";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opts = context.GetOption<SegmentAnalysisOptions>();
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
        int sohCount = 0, lohCount = 0, pohCount = 0, frozenCount = 0;
        int sohObjects = 0, lohObjects = 0, pohObjects = 0, frozenObjects = 0;
        long totalObjectsScanned = 0;
        int segmentsProcessed = 0;

        var snapshots = new List<HeapSegmentSnapshot>(totalSegments);

        foreach (ClrSegment segment in heap.Segments)
        {
            HeapSegmentKind kind = SegmentKindMapper.Map(segment);
            ulong committed = GetCommittedBytes(segment);
            ulong start = segment.Start;
            ulong end = segment.End;
            ulong length = end > start ? end - start : 0;
            int generation = segment.SubHeap?.Index ?? -1;

            int objCount = CountObjects(segment, kind, countSoh, ref totalObjectsScanned, progress);

            segmentsProcessed++;
            progress?.Report(new(
                ScannedCount: totalObjectsScanned,
                Phase: "classifying heap segments",
                Detail: $"{segmentsProcessed} / {totalSegments} segments, {totalObjectsScanned:N0} objects"));

            snapshots.Add(new HeapSegmentSnapshot(
                Address: segment.Address,
                Start: start,
                End: end,
                Length: length,
                CommittedBytes: committed,
                Kind: kind,
                Generation: generation,
                ObjectCount: objCount));

            // objCount == -1 is the sentinel for "SOH not counted" — do not add to totals.
            int countedObj = objCount >= 0 ? objCount : 0;
            switch (kind)
            {
                case HeapSegmentKind.SmallObjectHeap:
                    sohCount++;
                    sohBytes += committed;
                    if (objCount >= 0) sohObjects += countedObj; else sohObjects = -1;
                    break;
                case HeapSegmentKind.LargeObjectHeap:
                    lohCount++;
                    lohBytes += committed;
                    lohObjects += countedObj;
                    break;
                case HeapSegmentKind.PinnedObjectHeap:
                    pohCount++;
                    pohBytes += committed;
                    pohObjects += countedObj;
                    break;
                case HeapSegmentKind.Frozen:
                    frozenCount++;
                    frozenBytes += committed;
                    frozenObjects += countedObj;
                    break;
                default:
                    sohCount++;
                    sohBytes += committed;
                    if (objCount >= 0) sohObjects += countedObj; else sohObjects = -1;
                    break;
            }
        }

        ulong totalCommitted = sohBytes + lohBytes + pohBytes + frozenBytes;
        double lohPercent = totalCommitted == 0 ? 0.0 : lohBytes * 100.0 / totalCommitted;
        double pohPercent = totalCommitted == 0 ? 0.0 : pohBytes * 100.0 / totalCommitted;

        var kindSummaries = new List<SegmentKindSummary>
        {
            new(HeapSegmentKind.SmallObjectHeap, sohCount, sohObjects, sohBytes),
            new(HeapSegmentKind.LargeObjectHeap, lohCount, lohObjects, lohBytes),
            new(HeapSegmentKind.PinnedObjectHeap, pohCount, pohObjects, pohBytes),
            new(HeapSegmentKind.Frozen, frozenCount, frozenObjects, frozenBytes),
        };

        var topBySize = snapshots
            .OrderByDescending(s => s.CommittedBytes)
            .Take(SegmentAnalyzerOptions.TopSegmentsCount)
            .ToList();

        progress?.Report(new(
            ScannedCount: totalObjectsScanned,
            Phase: "aggregating results",
            Detail: $"{snapshots.Count} segments, {totalObjectsScanned:N0} objects total"));

        return new SegmentAnalysisDomainResult(
            TotalSegments: snapshots.Count,
            TotalCommittedBytes: totalCommitted,
            SohSegmentCount: sohCount,
            SohBytes: sohBytes,
            LohSegmentCount: lohCount,
            LohBytes: lohBytes,
            PohSegmentCount: pohCount,
            PohBytes: pohBytes,
            FrozenSegmentCount: frozenCount,
            FrozenBytes: frozenBytes,
            LohPercent: lohPercent,
            PohPercent: pohPercent,
            KindSummaries: kindSummaries,
            TopSegmentsBySize: topBySize);
    }

    public void Dispose() { }

    private static ulong GetCommittedBytes(ClrSegment segment)
    {
        MemoryRange mem = segment.CommittedMemory;
        return mem.End >= mem.Start ? mem.End - mem.Start : 0;
    }

    private static int CountObjects(
        ClrSegment segment,
        HeapSegmentKind kind,
        bool countSoh,
        ref long totalObjectsScanned,
        IProgress<AnalyzerProgressReport>? progress)
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
                count++;

            localScanned++;

            if (reportInner && (localScanned & (SegmentAnalyzerOptions.ReportObjectScanInterval - 1)) == 0)
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
}
