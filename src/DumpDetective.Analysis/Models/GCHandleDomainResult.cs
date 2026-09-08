using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Models;

// GC Handles (including dependent handles)

internal sealed record GCHandleDomainResult(
    int TotalHandles,
    int StrongLikeHandles,
    int WeakLikeHandles,
    int PinnedHandleTargets,
    IReadOnlyList<NameCountEntry>? HandlesByKind = null,
    IReadOnlyList<NameCountEntry>? TopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? TopPinnedTargetTypes = null,
    /// <summary>Total bytes retained by all pinned GC handles (estimated object sizes).</summary>
    ulong PinnedRetainedBytes = 0,
    /// <summary>Top pinned handle target types ranked by total pinned bytes.</summary>
    IReadOnlyList<NameBytesEntry>? TopPinnedObjectsBySize = null,
    /// <summary>Total bytes retained by all AsyncPinned GC handles (P1-2).</summary>
    ulong AsyncPinnedRetainedBytes = 0,
    /// <summary>Top AsyncPinned handle target types ranked by total bytes (P1-2).</summary>
    IReadOnlyList<NameBytesEntry>? TopAsyncPinnedObjectsBySize = null,
    /// <summary>Null-target handle counts per kind (P1-3).</summary>
    IReadOnlyList<NameCountEntry>? NullTargetHandlesByKind = null,
    /// <summary>Count of handles with unresolvable target types (P1-6).</summary>
    int UnknownTargetCount = 0,
    int DependentHandleCount = 0,
    int DependentResolvedEdgeCount = 0,
    int DependentUnresolvedTargetCount = 0,
    double DependentUnresolvedPercent = 0,
    IReadOnlyList<NameCountEntry>? DependentTopSourceTypes = null,
    IReadOnlyList<NameCountEntry>? DependentTopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? DependentTopSourceTargetEdges = null,
    /// <summary>True when every handle contributing to <see cref="PinnedRetainedBytes"/> was
    /// resolved via the exact dominator tree (§9, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md)
    /// rather than falling back to the target's own shallow size.</summary>
    bool PinnedRetainedBytesIsExact = false,
    /// <summary>Same as <see cref="PinnedRetainedBytesIsExact"/>, for <see cref="AsyncPinnedRetainedBytes"/>.</summary>
    bool AsyncPinnedRetainedBytesIsExact = false,
    /// <summary>Count of "Pinned" handle targets on the small object heap (P2-1). These block GC
    /// compaction while pinned; targets on LOH/POH/Frozen (<see cref="PinnedNonSohObjectCount"/>) don't,
    /// since LOH is never compacted and POH objects are already pinned by construction.</summary>
    int PinnedSohObjectCount = 0,
    /// <summary>Count of "Pinned" handle targets outside the small object heap (LOH/POH/Frozen).</summary>
    int PinnedNonSohObjectCount = 0,
    /// <summary>Same as <see cref="PinnedSohObjectCount"/>, for "AsyncPinned" handle targets.</summary>
    int AsyncPinnedSohObjectCount = 0,
    /// <summary>Same as <see cref="PinnedNonSohObjectCount"/>, for "AsyncPinned" handle targets.</summary>
    int AsyncPinnedNonSohObjectCount = 0,
    /// <summary>Count of "RefCounted" handles (P2-2) — the CLR's COM interop RCW keep-alive
    /// mechanism. Concentration by target type surfaces COM object leaks.</summary>
    int RefCountedHandleCount = 0,
    /// <summary>RefCounted handle target types ranked by count (P2-2).</summary>
    IReadOnlyList<NameCountEntry>? TopRefCountedTargetTypes = null,
    /// <summary>Individual Pinned/AsyncPinned handle target addresses, ranked by retained bytes
    /// (P2-4). Bridges to debugger follow-up (e.g. <c>!gcroot</c>) without needing WinDbg to
    /// re-enumerate handles. The full set is computed exactly; this list is truncated to
    /// <see cref="GCHandleAnalysisOptions.TopPinnedHandleAddressesToShow"/> entries for display.</summary>
    IReadOnlyList<PinnedHandleAddressEntry>? TopPinnedHandleAddresses = null,
    /// <summary>Generation breakdown of "WeakShort" handle targets (P3-2). WeakShort clears when
    /// the target becomes unreachable, even mid-finalization.</summary>
    int WeakShortGen0Count = 0,
    int WeakShortGen1Count = 0,
    int WeakShortGen2Count = 0,
    int WeakShortLohCount = 0,
    /// <summary>Generation breakdown of "WeakLong" handle targets (P3-2). WeakLong clears only
    /// after finalization completes, so a population concentrated in Gen2/LOH
    /// (<see cref="WeakLongGen2Count"/> + <see cref="WeakLongLohCount"/>) can indicate a
    /// finalization backlog — see <c>FinalizableObjectAnalyzer</c> for the finalization-queue view.</summary>
    int WeakLongGen0Count = 0,
    int WeakLongGen1Count = 0,
    int WeakLongGen2Count = 0,
    int WeakLongLohCount = 0,
    int TotalHandlesWarningThreshold = 10000,
    int PinnedHandleTargetsWarningThreshold = 1000,
    ulong PinnedRetainedBytesWarningThreshold = 100 * 1024 * 1024,
    /// <summary>Combined (Pinned + AsyncPinned) SOH target count threshold for warning-level severity.</summary>
    int PinnedSohObjectCountWarningThreshold = 500,
    /// <summary>RefCounted handle count threshold for warning-level severity (P2-2).</summary>
    int RefCountedHandleCountWarningThreshold = 100,
    /// <summary>Minimum fraction of resolved WeakLong targets in Gen2/LOH for warning-level
    /// severity (P3-2).</summary>
    double WeakLongGen2FractionWarningThreshold = 70.0,
    /// <summary>Minimum absolute Gen2/LOH WeakLong target count required before the fraction
    /// threshold is evaluated (P3-2) — avoids noise on small weak-handle populations.</summary>
    int WeakLongGen2MinimumCountThreshold = 100,
    double DependentUnresolvedPercentWarningThreshold = 50.0) : AnalyzerDomainResult;

/// <summary>One individual Pinned/AsyncPinned handle target, for the P2-4 top-N address table.</summary>
internal sealed record PinnedHandleAddressEntry(ulong Address, string TypeName, ulong Bytes, string HandleKind);
