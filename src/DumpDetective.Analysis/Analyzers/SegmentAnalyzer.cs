using System.Reflection;
using System.Collections.Concurrent;
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
    private const int TopSegmentsCount = 10;

    // OPT: Cache reflection members per ClrSegment concrete type to avoid per-segment reflection overhead.
    private static readonly ConcurrentDictionary<Type, SegmentReflectionCache> s_reflectionCache = new();

    private sealed class SegmentReflectionCache
    {
        public PropertyInfo? Kind { get; init; }
        public PropertyInfo? IsLargeObjectSegment { get; init; }
        public PropertyInfo? IsPinned { get; init; }
        public PropertyInfo? IsFrozen { get; init; }
        public PropertyInfo? Address { get; init; }
        public PropertyInfo? Start { get; init; }
        public PropertyInfo? End { get; init; }
        public PropertyInfo? ObjectRange { get; init; }
        public PropertyInfo? CommittedMemory { get; init; }

        public static SegmentReflectionCache Build(Type type) => new()
        {
            Kind = type.GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public),
            IsLargeObjectSegment = type.GetProperty("IsLargeObjectSegment", BindingFlags.Instance | BindingFlags.Public),
            IsPinned = type.GetProperty("IsPinnedObjectHeap", BindingFlags.Instance | BindingFlags.Public)
                      ?? type.GetProperty("IsPinned", BindingFlags.Instance | BindingFlags.Public),
            IsFrozen = type.GetProperty("IsFrozenObjectHeap", BindingFlags.Instance | BindingFlags.Public)
                      ?? type.GetProperty("IsFrozen", BindingFlags.Instance | BindingFlags.Public),
            Address = type.GetProperty("Address", BindingFlags.Instance | BindingFlags.Public),
            Start = type.GetProperty("Start", BindingFlags.Instance | BindingFlags.Public),
            End = type.GetProperty("End", BindingFlags.Instance | BindingFlags.Public),
            ObjectRange = type.GetProperty("ObjectRange", BindingFlags.Instance | BindingFlags.Public),
            CommittedMemory = type.GetProperty("CommittedMemory", BindingFlags.Instance | BindingFlags.Public),
        };
    }

    public string Name => "Segment Analysis";
    public string Category => "Memory";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.Progress).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(ClrHeap heap, IProgress<AnalyzerProgressReport>? progress)
    {
        // Count total segments upfront for accurate progress percentage.
        // heap.Segments is an IEnumerable but in practice it materialises cheaply from a fixed list.
        int totalSegments = 0;
        foreach (ClrSegment _ in heap.Segments)
            totalSegments++;

        progress?.Report(new(0, "classifying heap segments", $"0 / {totalSegments} segments"));

        ulong sohBytes = 0, lohBytes = 0, pohBytes = 0, frozenBytes = 0;
        int sohCount = 0, lohCount = 0, pohCount = 0, frozenCount = 0;
        int sohObjects = 0, lohObjects = 0, pohObjects = 0, frozenObjects = 0;
        long totalObjectsScanned = 0;
        int segmentsProcessed = 0;

        var snapshots = new List<HeapSegmentSnapshot>(totalSegments);

        foreach (ClrSegment segment in heap.Segments)
        {
            HeapSegmentKind kind = ClassifySegment(segment);
            ulong committed = GetCommittedBytes(segment);
            ulong start = GetStart(segment);
            ulong end = GetEnd(segment);
            ulong length = end > start ? end - start : 0;
            int generation = GetGeneration(segment);

            int objCount = CountObjects(segment, kind, ref totalObjectsScanned, progress);

            segmentsProcessed++;
            progress?.Report(new(
                ScannedCount: totalObjectsScanned,
                Phase: "classifying heap segments",
                Detail: $"{segmentsProcessed} / {totalSegments} segments, {totalObjectsScanned:N0} objects"));

            snapshots.Add(new HeapSegmentSnapshot(
                Address: GetAddress(segment),
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
            .Take(TopSegmentsCount)
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

    private static HeapSegmentKind ClassifySegment(ClrSegment segment)
    {
        Type type = segment.GetType();
        SegmentReflectionCache rc = s_reflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

        // Try strongly-typed Kind property first (available in newer ClrMD)
        if (rc.Kind?.GetValue(segment) is { } kindValue)
        {
            string kindName = kindValue.ToString() ?? string.Empty;
            if (kindName.Contains("Large", StringComparison.OrdinalIgnoreCase))
                return HeapSegmentKind.LargeObjectHeap;
            if (kindName.Contains("Pinned", StringComparison.OrdinalIgnoreCase))
                return HeapSegmentKind.PinnedObjectHeap;
            if (kindName.Contains("Frozen", StringComparison.OrdinalIgnoreCase))
                return HeapSegmentKind.Frozen;
            if (kindName.Contains("Small", StringComparison.OrdinalIgnoreCase)
                || kindName.Contains("Generation", StringComparison.OrdinalIgnoreCase)
                || kindName.Contains("Soh", StringComparison.OrdinalIgnoreCase))
                return HeapSegmentKind.SmallObjectHeap;
        }

        // Fallback to boolean properties
        if (rc.IsFrozen?.GetValue(segment) is true)
            return HeapSegmentKind.Frozen;
        if (rc.IsPinned?.GetValue(segment) is true)
            return HeapSegmentKind.PinnedObjectHeap;
        if (rc.IsLargeObjectSegment?.GetValue(segment) is true)
            return HeapSegmentKind.LargeObjectHeap;

        return HeapSegmentKind.SmallObjectHeap;
    }

    private static ulong GetAddress(ClrSegment segment)
    {
        Type type = segment.GetType();
        SegmentReflectionCache rc = s_reflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

        if (rc.Address?.GetValue(segment) is ulong addr)
            return addr;

        return GetStart(segment);
    }

    private static ulong GetStart(ClrSegment segment)
    {
        Type type = segment.GetType();
        SegmentReflectionCache rc = s_reflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

        if (rc.Start?.GetValue(segment) is ulong start)
            return start;

        if (rc.ObjectRange?.GetValue(segment) is { } range)
        {
            var startProp = range.GetType().GetProperty("Start", BindingFlags.Instance | BindingFlags.Public);
            if (startProp?.GetValue(range) is ulong s)
                return s;
        }

        return 0;
    }

    private static ulong GetEnd(ClrSegment segment)
    {
        Type type = segment.GetType();
        SegmentReflectionCache rc = s_reflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

        if (rc.End?.GetValue(segment) is ulong end)
            return end;

        if (rc.ObjectRange?.GetValue(segment) is { } range)
        {
            var endProp = range.GetType().GetProperty("End", BindingFlags.Instance | BindingFlags.Public);
            if (endProp?.GetValue(range) is ulong e)
                return e;
        }

        return 0;
    }

    private static ulong GetCommittedBytes(ClrSegment segment)
    {
        Type type = segment.GetType();
        SegmentReflectionCache rc = s_reflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

        if (rc.CommittedMemory?.GetValue(segment) is { } mem)
        {
            var lengthProp = mem.GetType().GetProperty("Length", BindingFlags.Instance | BindingFlags.Public);
            if (lengthProp?.GetValue(mem) is ulong len)
                return len;
        }

        // Fallback: derive from start/end
        ulong start = GetStart(segment);
        ulong end = GetEnd(segment);
        return end > start ? end - start : 0;
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

            if (reportInner && (localScanned & 0x3FFF) == 0) // every ~16k objects
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

    private static int GetGeneration(ClrSegment segment)
    {
        var genProp = segment.GetType().GetProperty("LogicalHeap", BindingFlags.Instance | BindingFlags.Public)
                  ?? segment.GetType().GetProperty("Generation", BindingFlags.Instance | BindingFlags.Public);
        if (genProp?.GetValue(segment) is int gen)
            return gen;
        return -1;
    }
}
