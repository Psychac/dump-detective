using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Weak Reference

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
    bool AliveWeakTargetsRetainedBytesIsExact = false) : AnalyzerDomainResult;
