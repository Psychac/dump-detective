using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Thread Stack Clusters

internal sealed record ThreadStackClusterDomainResult(
    int AliveThreadCount,
    int UniqueClusters,
    int SingletonSignatures,
    double DiversityPercent,
    IReadOnlyList<string> TopClusterSignatures,
    IReadOnlyList<ThreadClusterSnapshot>? TopClusters = null,
    IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? Artifacts = null) : AnalyzerDomainResult;

/// <summary>
/// <paramref name="SampleOsThreadIds"/>/<paramref name="SampleManagedThreadIds"/> are the complete
/// per-cluster thread ID lists (§9.24) — the "Sample" prefix is kept only for artifact-schema
/// stability (JSON/NDJSON cluster exports), not because the list is truncated. Display-width
/// truncation, if any, happens at the render layer.
/// </summary>
internal sealed record ThreadClusterSnapshot(
    int Count,
    IReadOnlyList<uint> SampleOsThreadIds,
    string Signature,
    int ThreadpoolWorkerCount = 0,
    int GcCount = 0,
    int FinalizerCount = 0,
    IReadOnlyList<int>? SampleManagedThreadIds = null);
