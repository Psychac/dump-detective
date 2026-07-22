using DumpDetective.Core.Models;

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
    int DependentHandleCount = 0,
    int DependentResolvedEdgeCount = 0,
    int DependentUnresolvedTargetCount = 0,
    double DependentUnresolvedPercent = 0,
    IReadOnlyList<NameCountEntry>? DependentTopSourceTypes = null,
    IReadOnlyList<NameCountEntry>? DependentTopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? DependentTopSourceTargetEdges = null) : AnalyzerDomainResult;
