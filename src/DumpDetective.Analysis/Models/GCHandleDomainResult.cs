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
    IReadOnlyList<NameCountEntry>? DependentTopSourceTargetEdges = null) : AnalyzerDomainResult;
