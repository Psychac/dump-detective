namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// One GC segment. <see cref="Generation"/> is ClrMD's own generation numbering (0/1/2, higher for
/// LOH/POH/Frozen) — see docs/binary-format.md's <c>ObjectGenerations</c>/<c>ObjectGenerationRuns</c>
/// section notes for exactly how that's derived today.
/// </summary>
/// <remarks>
/// Named `required` properties, not positional construction — <see cref="Start"/>/<see cref="End"/>
/// are adjacent same-typed (`ulong`) fields, silently transposable at a positional call site with
/// no compiler protection. See docs/refactor/modularity/phase-1-sdk-review-findings.md item 5.
/// </remarks>
public readonly record struct HeapSegmentRef
{
    public required ulong Start { get; init; }
    public required ulong End { get; init; }
    public required int Generation { get; init; }
}

/// <summary>The <c>heap.segments</c> capability.</summary>
public interface IHeapSegmentQuery
{
    IEnumerable<HeapSegmentRef> EnumerateSegments();

    /// <summary>Point lookup mirroring <c>heap.GetSegmentByAddress</c>/<c>SegmentKindMapper</c>.</summary>
    bool TryGetSegment(ulong address, out HeapSegmentRef segment);
}
