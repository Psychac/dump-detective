using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Per-segment classification computed once via <see cref="SegmentKindMapper"/> and shared between
/// <c>HeapTopologyAnalyzer</c> and <c>SegmentReservationAnalyzer</c> (see
/// docs/refactor/heap-segment-shared-pass-plan.md) instead of each analyzer re-deriving the same
/// fields from its own <c>ClrHeap.Segments</c> loop. Gen0/Gen1/Gen2 byte lengths are only populated
/// for <see cref="HeapSegmentKind.SmallObjectHeap"/> segments (zero otherwise), matching the
/// generation-range semantics ClrMD only exposes meaningfully for SOH.
/// </summary>
internal readonly record struct SegmentSummary(
    ClrSegment Segment,
    HeapSegmentKind Kind,
    RegionGenerationKind RegionKind,
    ulong CommittedBytes,
    ulong ReservedBytes,
    int LogicalHeapIndex,
    bool IsEphemeral,
    ulong Gen0Bytes,
    ulong Gen1Bytes,
    ulong Gen2Bytes);
