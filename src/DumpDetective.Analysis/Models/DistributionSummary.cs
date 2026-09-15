namespace DumpDetective.Analysis.Models;

/// <summary>
/// Stayed in <c>Analysis.Models</c> rather than moving into <c>Plugins.Types</c> alongside
/// <see cref="StringDomainResult"/> — still-in-<c>Analysis</c> heap-index infra
/// (<c>HeapIndexBuildResult</c>, <c>DiskBackedObjectIndexWriter</c>, <c>TypeAggregateIndexReader</c>)
/// depends on it too, and moving it would create a <c>Analysis → Plugins.Types</c> reverse
/// dependency the package boundary forbids.
/// </summary>
internal sealed record DistributionSummary(
    IReadOnlyDictionary<string, double> Percentiles,
    IReadOnlyDictionary<string, int> LengthBuckets,
    IReadOnlyDictionary<string, int> FrequencyBuckets,
    int SampleCount);
