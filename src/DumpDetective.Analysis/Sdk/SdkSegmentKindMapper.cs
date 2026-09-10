using DumpHeapSegmentKind = DumpDetective.Analysis.Models.HeapSegmentKind;
using DumpRegionGenerationKind = DumpDetective.Analysis.Models.RegionGenerationKind;
using SdkHeapSegmentKind = DumpDetective.Sdk.Analysis.HeapSegmentKind;
using SdkRegionGenerationKind = DumpDetective.Sdk.Analysis.RegionGenerationKind;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Two-way mapping between the dump-side <c>HeapSegmentKind</c>/<c>RegionGenerationKind</c>
/// (<c>DumpDetective.Analysis.Models</c>) and their SDK mirrors
/// (<c>DumpDetective.Sdk.Analysis</c>). <see cref="HeapSegmentQuery"/> needs the dump-to-SDK
/// direction to populate <c>HeapSegmentRef</c>; a retyped analyzer needs the reverse to build its
/// still-dump-side-typed <c>*DomainResult</c> (unchanged by retyping — see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's explicit out-of-scope list).
/// Centralized here, not duplicated per call site, mirroring how <c>Analyzers.SegmentKindMapper</c>
/// already centralizes the ClrMD-to-dump-side direction.
/// </summary>
internal static class SdkSegmentKindMapper
{
    public static SdkHeapSegmentKind ToSdk(DumpHeapSegmentKind kind) => kind switch
    {
        DumpHeapSegmentKind.SmallObjectHeap => SdkHeapSegmentKind.SmallObjectHeap,
        DumpHeapSegmentKind.LargeObjectHeap => SdkHeapSegmentKind.LargeObjectHeap,
        DumpHeapSegmentKind.PinnedObjectHeap => SdkHeapSegmentKind.PinnedObjectHeap,
        DumpHeapSegmentKind.Frozen => SdkHeapSegmentKind.Frozen,
        _ => SdkHeapSegmentKind.Unknown,
    };

    public static DumpHeapSegmentKind ToDump(SdkHeapSegmentKind kind) => kind switch
    {
        SdkHeapSegmentKind.SmallObjectHeap => DumpHeapSegmentKind.SmallObjectHeap,
        SdkHeapSegmentKind.LargeObjectHeap => DumpHeapSegmentKind.LargeObjectHeap,
        SdkHeapSegmentKind.PinnedObjectHeap => DumpHeapSegmentKind.PinnedObjectHeap,
        SdkHeapSegmentKind.Frozen => DumpHeapSegmentKind.Frozen,
        _ => DumpHeapSegmentKind.Unknown,
    };

    public static SdkRegionGenerationKind ToSdk(DumpRegionGenerationKind kind) => kind switch
    {
        DumpRegionGenerationKind.Generation0 => SdkRegionGenerationKind.Generation0,
        DumpRegionGenerationKind.Generation1 => SdkRegionGenerationKind.Generation1,
        DumpRegionGenerationKind.Generation2 => SdkRegionGenerationKind.Generation2,
        DumpRegionGenerationKind.Large => SdkRegionGenerationKind.Large,
        DumpRegionGenerationKind.Pinned => SdkRegionGenerationKind.Pinned,
        DumpRegionGenerationKind.Frozen => SdkRegionGenerationKind.Frozen,
        _ => SdkRegionGenerationKind.Ephemeral,
    };

    public static DumpRegionGenerationKind ToDump(SdkRegionGenerationKind kind) => kind switch
    {
        SdkRegionGenerationKind.Generation0 => DumpRegionGenerationKind.Generation0,
        SdkRegionGenerationKind.Generation1 => DumpRegionGenerationKind.Generation1,
        SdkRegionGenerationKind.Generation2 => DumpRegionGenerationKind.Generation2,
        SdkRegionGenerationKind.Large => DumpRegionGenerationKind.Large,
        SdkRegionGenerationKind.Pinned => DumpRegionGenerationKind.Pinned,
        SdkRegionGenerationKind.Frozen => DumpRegionGenerationKind.Frozen,
        _ => DumpRegionGenerationKind.Ephemeral,
    };

    /// <summary>Matches the pre-existing "0/1/2, higher for LOH/POH/Frozen" contract documented on
    /// <c>HeapSegmentRef.Generation</c> itself — not read by either analyzer in this batch (both
    /// work off <c>Kind</c>/<c>RegionKind</c> directly), computed only to satisfy the required
    /// field.</summary>
    public static int ToLegacyGenerationNumber(DumpRegionGenerationKind regionKind) => regionKind switch
    {
        DumpRegionGenerationKind.Generation0 => 0,
        DumpRegionGenerationKind.Generation1 => 1,
        DumpRegionGenerationKind.Generation2 => 2,
        DumpRegionGenerationKind.Ephemeral => 2,
        DumpRegionGenerationKind.Large => 3,
        DumpRegionGenerationKind.Pinned => 4,
        DumpRegionGenerationKind.Frozen => 5,
        _ => 2,
    };
}
