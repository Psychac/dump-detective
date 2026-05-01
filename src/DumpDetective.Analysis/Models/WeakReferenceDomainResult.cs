using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Weak Reference

internal sealed record WeakReferenceDomainResult(
    int TotalWeakHandles,
    int AliveWeakTargets,
    int DeadWeakTargets,
    double DeadTargetRatio,
    int WeakReferenceObjectCount,
    ulong WeakReferenceObjectBytes,
    int StaleWrapperCount,
    IReadOnlyList<NameCountEntry> TopWeakTargetTypes,
    IReadOnlyList<NameCountEntry> TopStaleWrapperHolderTypes,
    int DependentHandleDeadKeyCount,
    bool ScanCapped) : AnalyzerDomainResult;
