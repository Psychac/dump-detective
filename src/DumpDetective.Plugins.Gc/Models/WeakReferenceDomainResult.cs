using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Weak Reference

/// <summary>Alive/dead handle counts for a single weak handle kind (WeakShort, WeakLong,
/// WeakWinRT) — §24.1 P2-1 per-kind breakdown, distinct from the aggregate
/// <see cref="WeakReferenceDomainResult.AliveWeakTargets"/>/<see cref="WeakReferenceDomainResult.DeadWeakTargets"/>
/// totals across all kinds.</summary>
internal readonly record struct HandleKindLivenessEntry(string Kind, int Alive, int Dead)
{
    public int Total => Alive + Dead;
}

internal sealed record WeakReferenceDomainResult(
    int TotalWeakHandles,
    int AliveWeakTargets,
    int DeadWeakTargets,
    double DeadTargetRatio,
    IReadOnlyList<NameCountEntry> WeakHandleKinds,
    int WeakReferenceObjectCount,
    ulong WeakReferenceObjectBytes,
    int StaleWrapperCount,
    IReadOnlyList<NameCountEntry> TopWeakTargetTypes,
    IReadOnlyList<NameCountEntry> TopStaleWrapperHolderTypes,
    int DependentHandleDeadKeyCount,
    bool PhaseBFallbackUsed,
    bool PhaseBSkipped,
    IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts = null,
    /// <summary>Sum of exact dominator-tree retained bytes over every alive weak-handle target
    /// seen (§9, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — what each
    /// target transitively holds, distinct from <see cref="WeakReferenceObjectBytes"/>'s wrapper
    /// shallow size. 0 when the exact tree wasn't available.</summary>
    ulong AliveWeakTargetsRetainedBytes = 0,
    bool AliveWeakTargetsRetainedBytesIsExact = false,
    IReadOnlyList<HandleKindLivenessEntry>? WeakHandleKindLiveness = null,
    /// <summary>§24.3 P3-1: value (secondary) object types for dead-key dependent handles —
    /// reveals what data ConditionalWeakTable is orphaning when the primary key has been
    /// collected. Scoped to dead keys only; distinct from GCHandleAnalyzer's dependent-handle
    /// type breakdown, which covers all dependent handles regardless of key liveness.</summary>
    IReadOnlyList<NameCountEntry>? DependentDeadKeyValueTypes = null,
    int DependentDeadKeyValueTypesUnresolvedCount = 0,
    /// <summary>§24.1 P3-3: Gen0/Gen1/Gen2/LOH distribution of currently-alive weak targets,
    /// resolved from the target object's current segment. Scoped to alive targets only — a dead
    /// weak handle's address is either already cleared (0) or may point at memory since reused
    /// by an unrelated object, so its "generation" can't be attributed reliably.</summary>
    IReadOnlyList<NameCountEntry>? AliveWeakTargetGenerationDistribution = null,
    int AliveWeakTargetGenerationUnresolvedCount = 0,
    /// <summary>§24.1 P3-2: count of alive weak targets unreachable from any GC root via the Stage
    /// A reachability walk (<see cref="Core.Abstractions.IReachableAddressProvider"/>) — the
    /// object is currently alive purely because the GC hasn't swept it yet, and the only known
    /// reference to it is the weak handle itself (dotMemory calls this "held only via weak
    /// reference"). Prefers this over a raw reverse-edge-index parent check because that index
    /// only records object-to-object edges: an object rooted directly by a static field, with no
    /// other object pointing at it, would show zero parents there too — a false positive this
    /// reachability-based check avoids.</summary>
    int HeldOnlyViaWeakReferenceCount = 0,
    IReadOnlyList<NameCountEntry>? HeldOnlyViaWeakReferenceTopTypes = null,
    /// <summary>False when <see cref="Core.Abstractions.IReachableAddressProvider"/> was
    /// unavailable (in-memory mode, Stage A's walk skipped, legacy cache) — distinguishes "zero
    /// found" from "not computed" for <see cref="HeldOnlyViaWeakReferenceCount"/>.</summary>
    bool HeldOnlyViaWeakReferenceDetectionAvailable = false,
    /// <summary>§24.2 P3-4: whether <see cref="StaleWrapperCount"/> (and
    /// <see cref="TopStaleWrapperHolderTypes"/>) reflect a full per-instance scan rather than a
    /// group-sample extrapolation. <c>true</c> in both the normal cases — the index-path streams
    /// every WeakReference-shaped object off the disk-backed object index, and the no-index
    /// fallback path scans the live heap directly. <c>false</c> only in the rare degraded case
    /// where <c>TypeAggregates</c> is available but the disk object index itself is not (e.g. a
    /// hand-built test fixture, or a partial/aborted index write) — there, one sample address per
    /// type is extrapolated to that type's full count, which can be up to 100% wrong per type
    /// group (see weak-reference-analyzer-audit.md Bug 4).</summary>
    bool StaleWrapperCountIsExact = false) : AnalyzerDomainResult;
