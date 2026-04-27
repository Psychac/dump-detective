using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Build-time contract for constructing and managing the heap index.
/// Separates the index-build API from the read-only analyzer API (<see cref="IHeapAnalysisCache"/>).
/// Both interfaces are implemented by <see cref="HeapAnalysisCache"/>; the CLI pipeline
/// holds the same instance through each interface independently.
/// </summary>
internal interface IHeapIndexBuilder
{
    /// <summary>Scans the heap and builds the in-memory or disk-backed object index.</summary>
    HeapIndexBuildResult PrebuildHeapIndex(
        ClrHeap heap,
        string dumpPath,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        HeapIndexPrebuildMode mode = HeapIndexPrebuildMode.Auto);

    /// <summary>Updates the progress reporter used during per-analyzer scans after the index is built.</summary>
    void SetProgress(IProgress<AnalyzerProgressReport>? progress);
}
