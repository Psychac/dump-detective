using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// GC Handles

internal sealed record GCHandleDomainResult(
    int TotalHandles,
    int StrongLikeHandles,
    int WeakLikeHandles,
    int PinnedHandleTargets,
    IReadOnlyList<NameCountEntry>? HandlesByKind = null,
    IReadOnlyList<NameCountEntry>? TopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? TopPinnedTargetTypes = null,
    /// <summary>Total bytes retained by all pinned GC handles (estimated from object sizes).</summary>
    ulong PinnedRetainedBytes = 0,
    /// <summary>Top pinned handle target types ranked by their total pinned bytes.</summary>
    IReadOnlyList<NameBytesEntry>? TopPinnedObjectsBySize = null) : AnalyzerDomainResult;
