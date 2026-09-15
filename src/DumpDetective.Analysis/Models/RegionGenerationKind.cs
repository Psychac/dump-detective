using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Models;

/// <summary>
/// GC region generation, per <see cref="ClrSegment.Kind" /> — distinct from <see cref="HeapSegmentKind"/>,
/// which collapses Gen0/Gen1/Gen2/Ephemeral into a single SmallObjectHeap bucket. Region-based stats need
/// the per-generation split since each generation is its own population of regions in regions-based GC.
/// </summary>
/// <remarks>
/// Shared across multiple analyzer domains (<c>SegmentReservationAnalyzer</c>,
/// <c>SegmentKindMapper</c>, <c>SdkSegmentKindMapper</c>) — kept in <c>Analysis.Models</c> rather than
/// moved alongside Segment Reservation's analyzer/model split into
/// <c>DumpDetective.Plugins.Memory</c> (docs/refactor/modularity/phase-3-plugin-packaging.md).
/// </remarks>
internal enum RegionGenerationKind { Generation0, Generation1, Generation2, Large, Pinned, Frozen, Ephemeral }
