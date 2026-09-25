namespace DumpDetective.Analysis.Models;

/// <summary>Generation distribution for a single type, built from Phase-1 TypeAggregates.</summary>
/// <remarks>
/// Shared across multiple analyzer domains (<c>GCGenerationAnalyzer</c>, <c>FinalizableObjectAnalyzer</c>)
/// — kept in <c>Analysis.Models</c> rather than moved alongside any single
/// package's <c>DomainResult</c> when GC Generation's analyzer/model split into
/// <c>DumpDetective.Plugins.Memory</c> (docs/refactor/modularity/phase-3-plugin-packaging.md).
/// </remarks>
public sealed record TypeGenerationProfile(
    string TypeName,
    long Gen0Count,
    long Gen1Count,
    long Gen2Count,
    long LohCount,
    ulong TotalBytes = 0,
    /// <summary>
    /// Exact sum of object sizes for this type's non-LOH Gen2 instances, sourced from
    /// <see cref="DumpDetective.Analysis.Indexing.TypeAggregateIndexEntry.Gen2TotalSize"/>.
    /// Zero when the heap index predates that field (schema v3 and older).
    /// </summary>
    ulong Gen2Bytes = 0,
    bool IsFinalizable = false);
