using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

internal sealed record DominatorDomainResult(
    int CandidateCount,
    int AnalyzedCount,
    ulong TotalEstimatedRetainedBytes,
    IReadOnlyList<TypeSnapshot> TopDominatorTypes,
    bool HeuristicOnly = true,
    int MaxBreadth = 0,
    int MaxDepth = 20,
    int HighlyReferencedObjectCount = 0,
    long SkippedReferenceAddresses = 0,
    IReadOnlyList<HighlyReferencedObjectSnapshot>? TopHighlyReferencedObjects = null,
    /// <summary>
    /// True when the incoming-reference-count pass stopped early because the number of
    /// objects traced reached <see cref="DumpDetective.Core.Options.RetentionOptions.MaxLeakScanObjects"/>.
    /// Highly-referenced-object results are partial when set.
    /// </summary>
    bool ObjectScanCapped = false,
    /// <summary>
    /// True when a disk-backed index was in use and the incoming-reference-count scan was
    /// skipped entirely to avoid O(N x fields) random reads against the dump file.
    /// Highly-referenced-object detection is unavailable in this mode; use the in-memory index mode
    /// on this dump if you need it (requires enough machine RAM).
    /// </summary>
    bool ReferenceCountingSkipped = false,
    /// <summary>
    /// Aggregated retention hotspots derived from the top highly-referenced objects.
    /// Provides type-level insight (count, bytes, incoming refs) beyond per-object rows.
    /// </summary>
    IReadOnlyList<RetentionTypeSnapshot>? TopRetentionTypes = null,
    /// <summary>
    /// Total shallow bytes represented by <see cref="TopHighlyReferencedObjects"/>.
    /// This scoped footprint metric is a headline for reporting/trending.
    /// </summary>
    ulong TopHighlyReferencedTotalBytes = 0) : AnalyzerDomainResult;

internal sealed record HighlyReferencedObjectSnapshot(ulong Address, string TypeName, ulong Size, int IncomingReferences, ulong EstimatedRetainedBytes = 0);

internal sealed record RetentionTypeSnapshot(
    string TypeName,
    int ObjectCount,
    ulong TotalBytes,
    long TotalIncomingReferences,
    int MaxIncomingReferences,
    ulong EstimatedRetainedBytes = 0);
