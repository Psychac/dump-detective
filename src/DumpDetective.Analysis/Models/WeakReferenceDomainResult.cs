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
    bool ScanCapped,
    int ScanCapUsed,
    bool PhaseBFallbackUsed,
    bool PhaseBSkipped,
    IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts = null) : AnalyzerDomainResult;
