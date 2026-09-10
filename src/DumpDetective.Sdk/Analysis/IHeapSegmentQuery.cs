namespace DumpDetective.Sdk.Analysis;

/// <summary>Mirrors dump-side <c>DumpDetective.Analysis.Models.HeapSegmentKind</c> 1:1 — SDK has
/// zero ClrMD dependency. Classic (non-regions) generations collapse into
/// <see cref="SmallObjectHeap"/>; see <see cref="RegionGenerationKind"/> for the split.</summary>
public enum HeapSegmentKind
{
    SmallObjectHeap,
    LargeObjectHeap,
    PinnedObjectHeap,
    Frozen,
    Unknown,
}

/// <summary>Mirrors dump-side <c>DumpDetective.Analysis.Models.RegionGenerationKind</c> 1:1 —
/// distinct from <see cref="HeapSegmentKind"/>, which collapses Gen0/Gen1/Gen2/Ephemeral into a
/// single <see cref="HeapSegmentKind.SmallObjectHeap"/> bucket. Only meaningfully populated on
/// regions-based (.NET 8+) heaps; a classic heap's SOH segments report <see cref="Ephemeral"/>.</summary>
public enum RegionGenerationKind
{
    Generation0,
    Generation1,
    Generation2,
    Large,
    Pinned,
    Frozen,
    Ephemeral,
}

/// <summary>
/// One GC segment. <see cref="Generation"/> is ClrMD's own generation numbering (0/1/2, higher for
/// LOH/POH/Frozen) — see docs/binary-format.md's <c>ObjectGenerations</c>/<c>ObjectGenerationRuns</c>
/// section notes for exactly how that's derived today.
/// </summary>
/// <remarks>
/// Named `required` properties, not positional construction — <see cref="Start"/>/<see cref="End"/>
/// are adjacent same-typed (`ulong`) fields, silently transposable at a positional call site with
/// no compiler protection. See docs/refactor/modularity/phase-1-sdk-review-findings.md item 5.
///
/// <see cref="Kind"/> through <see cref="Gen2Bytes"/> added 2026-09-11 for the
/// `SegmentReservationAnalyzer`/`HeapTopologyAnalyzer` retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — mirror the dump-side
/// cache's internal <c>SegmentSummary</c> one-for-one, since both analyzers (and any future one
/// touching segments) need exactly that shape, already computed once per run and shared between
/// them today via <c>HeapAnalysisCache.GetOrBuildSegmentSummaries</c>.
/// </remarks>
public readonly record struct HeapSegmentRef
{
    public required ulong Start { get; init; }
    public required ulong End { get; init; }
    public required int Generation { get; init; }

    /// <summary>The CLR segment object's own address — distinct from <see cref="Start"/>/
    /// <see cref="End"/>, which bound the object range allocated on the segment, not the segment
    /// object itself. Verified against the installed ClrMD package (v4.0.732401): "The address of
    /// the CLR segment object" vs. <c>Start</c>/<c>End</c>, which are <c>ObjectRange.Start</c>/
    /// <c>.End</c> — genuinely different values, not a naming duplicate.</summary>
    public required ulong Address { get; init; }

    public required HeapSegmentKind Kind { get; init; }
    public required RegionGenerationKind RegionKind { get; init; }
    public required ulong CommittedBytes { get; init; }
    public required ulong ReservedBytes { get; init; }

    /// <summary>Server GC sub-heap (per-CPU) index, or a negative value when not applicable
    /// (Workstation GC). Distinct from <see cref="Generation"/> — this is which logical heap the
    /// segment belongs to, not which GC generation.</summary>
    public required int LogicalHeapIndex { get; init; }

    public required bool IsEphemeral { get; init; }

    /// <summary>Non-zero only for <see cref="HeapSegmentKind.SmallObjectHeap"/> segments — ClrMD
    /// only exposes meaningful generation sub-ranges there.</summary>
    public required ulong Gen0Bytes { get; init; }
    public required ulong Gen1Bytes { get; init; }
    public required ulong Gen2Bytes { get; init; }
}

/// <summary>The <c>heap.segments</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapSegmentQuery
{
    IEnumerable<HeapSegmentRef> EnumerateSegments();

    /// <summary>Point lookup mirroring <c>heap.GetSegmentByAddress</c>/<c>SegmentKindMapper</c>.</summary>
    bool TryGetSegment(ulong address, out HeapSegmentRef segment);

    /// <summary>
    /// The dump process's own pointer size in bytes (4 or 8), read from the dump itself — not this
    /// analysis tool's own process bitness, a P0 correctness bug fixed pre-SDK (see
    /// <c>SegmentReservationAnalyzer</c>'s own history). Placed here, not on a runtime/process
    /// capability, because its only consumer today reasons about it purely in terms of address-space
    /// pressure against committed/reserved segment bytes; revisit if a non-segment consumer needs it.
    /// </summary>
    int DumpPointerSize { get; }

    /// <summary>Whether the process ran Server GC (one heap per logical CPU) vs. Workstation GC.</summary>
    bool IsServerGc { get; }
}
