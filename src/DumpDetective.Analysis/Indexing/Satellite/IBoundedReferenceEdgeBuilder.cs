using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Phase 1.5 service that collects reference edges for the top-N candidate types and writes
/// <c>PartialRefEdgeIndex.bin</c>. Runs after Phase 1 completes but before Phase 2 begins,
/// only when <c>DominatorAnalyzer</c> is in the analyzer set.
/// </summary>
/// <remarks>
/// Record layout (16 bytes, little-endian):
///   SourceAddress (8) | TargetAddress (8)
/// Capped at 500 K edges (max 8 MB). Enforces both an edge count cap and an optional time cap.
/// </remarks>
internal interface IBoundedReferenceEdgeBuilder
{
    /// <summary>
    /// Builds <c>PartialRefEdgeIndex.bin</c> for the candidate method tables derived from
    /// <paramref name="buildResult"/>. Returns the number of edges written.
    /// </summary>
    /// <param name="buildResult">The Phase 1 build result supplying TypeAggregates and IndexPath.</param>
    /// <param name="outputPath">Full path to write the <c>PartialRefEdgeIndex.bin</c> file.</param>
    /// <param name="maxEdges">Hard cap on the number of edges written (default 500 000).</param>
    /// <param name="timeout">Optional wall-clock cap; building stops when exceeded.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation support.</param>
    /// <returns>Number of edges written; may be less than total if capped.</returns>
    long Build(
        HeapIndexBuildResult buildResult,
        string outputPath,
        int maxEdges = 500_000,
        TimeSpan? timeout = null,
        IProgress<AnalyzerProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}
