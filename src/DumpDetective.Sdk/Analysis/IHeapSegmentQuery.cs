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

/// <summary>Mirrors dump-side <c>DumpDetective.Core.Enums.GenerationTag</c> 1:1 — a per-*object*
/// (not per-segment) generation classification. Distinct from <see cref="HeapSegmentRef.Generation"/>:
/// an ephemeral (Workstation GC) segment holds Gen0/Gen1/Gen2 objects together in one range, so two
/// objects on the very same segment can carry different <see cref="HeapGenerationTag"/> values.
/// Added 2026-09-11 for <c>GCRootAnalyzer</c>'s retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), which needs per-root-target
/// generation for its by-kind Gen0/Gen1/Gen2/LOH fraction breakdown.</summary>
public enum HeapGenerationTag
{
    Gen0,
    Gen1,
    Gen2,
    Loh,
    Poh,
    Frozen,
    Unknown,
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

    /// <summary>Number of logical (sub-)heaps — 1 for Workstation GC, one per logical CPU for
    /// Server GC. Added 2026-09-11 for <c>HeapTopologyAnalyzer</c>'s retyping.</summary>
    int LogicalHeapCount { get; }

    /// <summary>Whether this heap can be walked at all — <c>false</c> for a heap ClrMD judged too
    /// corrupted/incomplete to enumerate safely. Added 2026-09-11 for <c>MemoryAnalyzer</c>'s
    /// retyping, which skips its retained-size enrichment entirely (rather than attempting a walk
    /// that would fail) when this is <c>false</c>. Heap-wide, not segment-specific, but bundled here
    /// alongside <see cref="DumpPointerSize"/>/<see cref="IsServerGc"/> for the same reason those
    /// are: no consumer outside segment-adjacent analysis needs it yet.</summary>
    bool CanWalkHeap { get; }

    /// <summary>
    /// Live, non-free objects on exactly this segment — added 2026-09-11 for
    /// <c>HeapTopologyAnalyzer</c>'s retyping, which walks LOH/POH/Frozen/Unknown segments
    /// individually (never the whole heap; SOH is deliberately never walked per-object at all —
    /// see that analyzer's own remarks) rather than filtering <c>heap.objects</c>' whole-heap
    /// stream. <paramref name="segment"/> must have come from this same query's
    /// <see cref="EnumerateSegments"/>/<see cref="TryGetSegment"/>.
    /// </summary>
    /// <remarks>
    /// Yields <see cref="HeapObjectRef"/> — carrying a full <c>TypeRef</c>, not a raw type name,
    /// for SDK-wide consistency with <see cref="IHeapObjectStream"/>. That means
    /// every yielded object pays <c>EntityCanonicalizer</c>'s canonicalization cost even though
    /// today's only consumer (`HeapTopologyAnalyzer`) only reads the raw display name back off it —
    /// accepted because LOH/POH/Frozen/Unknown populations are, by construction, far smaller than
    /// SOH's (which this method is never called for), not because the cost is free.
    ///
    /// <paramref name="includeFree"/> (default <c>false</c>, added 2026-09-11 for
    /// <c>MemoryAnalyzer</c>'s retyping) includes GC free-space pseudo-objects — excluded by default
    /// since <c>HeapTopologyAnalyzer</c>'s per-type breakdown has no use for them, but
    /// <c>MemoryAnalyzer</c>'s LOH fragmentation ratio needs the full committed span (live +
    /// free) to compute a free-byte delta against committed bytes, matching its pre-retyping
    /// behavior exactly.
    /// </remarks>
    IEnumerable<HeapObjectRef> EnumerateObjects(HeapSegmentRef segment, bool includeFree = false);

    /// <summary>Per-object generation classification for <paramref name="address"/> — resolves an
    /// ephemeral segment's internal Gen0/Gen1/Gen2 sub-range when the segment's own
    /// <see cref="HeapSegmentKind"/> doesn't already determine it uniquely (LOH/POH/Frozen segments
    /// always resolve directly from their kind). Returns <see cref="HeapGenerationTag.Unknown"/> for
    /// an address not on any live segment.</summary>
    HeapGenerationTag GetGeneration(ulong address);

    /// <summary>
    /// Every free (unallocated) block on this heap's LOH/POH segments. Added 2026-09-11 for
    /// <c>LohFragmentationAnalyzer</c>'s retyping
    /// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — hidden fast/fallback
    /// fork (the Phase-1 disk-backed free-block index when available, a live per-object scan of just
    /// LOH/POH segments otherwise) with identical semantics either way: every free block found, no
    /// cap, no size filter — unlike <see cref="EnumerateCapturedLargeObjects"/>, whose two modes are
    /// genuinely different features, this one's two modes answer the exact same question. Callers
    /// aggregate (per-segment totals, a size histogram, ...) themselves; this only streams.
    /// </summary>
    IEnumerable<HeapFreeBlockRef> EnumerateLohFreeBlocks();

    /// <summary>
    /// The up-to-100 largest objects captured during the Phase-1 scan (see
    /// <c>LargeObjectTracker</c>), sorted descending by size — a capped sample admitted by
    /// observation order during the single-pass scan, not a guaranteed top-100-by-final-size (an
    /// object larger than everything admitted so far but seen after the sample filled can still lose
    /// to a smaller earlier entry — see that type's own admission logic). Added 2026-09-11 for
    /// <c>LohFragmentationAnalyzer</c>'s retyping. Disk-index-only: empty when
    /// <see cref="HasLohSatelliteIndex"/> is <c>false</c> — unlike <see cref="EnumerateLohFreeBlocks"/>,
    /// there is no live-scan equivalent that reproduces this exact capped-sample set, so callers
    /// needing an exhaustive large-object list in that case derive one themselves from
    /// <see cref="EnumerateObjects"/> over LOH/POH segments instead.
    /// </summary>
    IEnumerable<HeapObjectRef> EnumerateCapturedLargeObjects();

    /// <summary>
    /// Whether a real disk-backed Phase-1 index is available to back
    /// <see cref="EnumerateCapturedLargeObjects"/> (and the fast path of
    /// <see cref="EnumerateLohFreeBlocks"/>). Added 2026-09-11 for <c>LohFragmentationAnalyzer</c>'s
    /// retyping — deliberately <em>not</em> the same signal as
    /// <see cref="IHeapTypeStatisticsQuery.HasExactGenerationData"/>: that one is <c>true</c> whenever
    /// any <c>HeapIndexBuildResult</c> exists, including an in-memory-mode one with no backing
    /// directory for these LOH-specific satellite files, which this property correctly reports as
    /// <c>false</c>.
    /// </summary>
    bool HasLohSatelliteIndex { get; }
}

/// <summary>One free (unallocated) block on a LOH/POH segment. <see cref="SegmentAddress"/> matches
/// the owning <see cref="HeapSegmentRef.Start"/>.</summary>
public readonly record struct HeapFreeBlockRef(ulong SegmentAddress, ulong Address, ulong Size);
