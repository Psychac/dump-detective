using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Classifies all managed heap segments (SOH, LOH, POH, Frozen) and produces a
/// <see cref="SegmentAnalysisDomainResult"/> with per-kind size and object count totals.
/// Operates directly on <see cref="ClrHeap.Segments"/> — no heap object enumeration required.
/// </summary>
public sealed class SegmentAnalyzer : IAnalyzer
{
    // Number of top segments to show — moved to SegmentAnalyzerOptions

    public string Name => "Segment Analysis";
    public string Category => "Memory";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.Progress).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(ClrHeap heap, IProgress<AnalyzerProgressReport>? progress)
    {
        // Materialize segments to avoid re-enumeration and to get an exact count upfront.
        var segments = heap.Segments.ToList();
        int totalSegments = segments.Count;

        progress?.Report(new(0, "classifying heap segments", $"0 / {totalSegments} segments"));

        ulong sohBytes = 0, lohBytes = 0, pohBytes = 0, frozenBytes = 0;
        int sohCount = 0, lohCount = 0, pohCount = 0, frozenCount = 0;
        int sohObjects = 0, lohObjects = 0, pohObjects = 0, frozenObjects = 0;
        long totalObjectsScanned = 0;
        int segmentsProcessed = 0;

        var snapshots = new List<HeapSegmentSnapshot>(totalSegments);

        foreach (ClrSegment segment in segments)
        {
            HeapSegmentKind kind = SegmentKindMapper.Map(segment);
            ulong committed = GetCommittedBytes(segment);
            ulong start = segment.Start;
            ulong end = segment.End;
            ulong length = end > start ? end - start : 0;
            int generation = segment.SubHeap?.Index ?? -1;

            int objCount = CountObjects(segment, kind, ref totalObjectsScanned, progress);

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

            switch (kind)
            {
                case HeapSegmentKind.SmallObjectHeap:
                    sohCount++;
                    sohBytes += committed;
                    sohObjects += objCount;
                    break;
                case HeapSegmentKind.LargeObjectHeap:
                    lohCount++;
                    lohBytes += committed;
                    lohObjects += objCount;
                    break;
                case HeapSegmentKind.PinnedObjectHeap:
                    pohCount++;
                    pohBytes += committed;
                    pohObjects += objCount;
                    break;
                case HeapSegmentKind.Frozen:
                    frozenCount++;
                    frozenBytes += committed;
                    frozenObjects += objCount;
                    break;
                default:
                    sohCount++;
                    sohBytes += committed;
                    sohObjects += objCount;
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

    private static HeapSegmentKind ClassifySegment(ClrSegment segment)
    {
        string kindName = segment.Kind.ToString();
        if (kindName.Contains("Large",  StringComparison.OrdinalIgnoreCase)) return HeapSegmentKind.LargeObjectHeap;
        if (kindName.Contains("Pinned", StringComparison.OrdinalIgnoreCase)) return HeapSegmentKind.PinnedObjectHeap;
        if (kindName.Contains("Frozen", StringComparison.OrdinalIgnoreCase)) return HeapSegmentKind.Frozen;
        return HeapSegmentKind.SmallObjectHeap;
    }

    private static ulong GetCommittedBytes(ClrSegment segment)
    {
        MemoryRange mem = segment.CommittedMemory;
        return mem.End >= mem.Start ? mem.End - mem.Start : 0;
    }

    private static int CountObjects(
        ClrSegment segment,
        HeapSegmentKind kind,
        ref long totalObjectsScanned,
        IProgress<AnalyzerProgressReport>? progress)
    {
        int count = 0;
        // Only report inner-loop progress for LOH/POH — they can hold very large object counts.
        // SOH segments are numerous and small; per-object reporting there would flood the sink.
        bool reportInner = progress is not null
            && kind is HeapSegmentKind.LargeObjectHeap or HeapSegmentKind.PinnedObjectHeap;
        long localScanned = 0;

        foreach (ClrObject obj in segment.EnumerateObjects())
        {
            if (obj.IsValid && !obj.IsFree)
                count++;

            localScanned++;

            if (reportInner && (localScanned & (SegmentAnalyzerOptions.ReportObjectScanInterval - 1)) == 0) // every ~16k objects
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
