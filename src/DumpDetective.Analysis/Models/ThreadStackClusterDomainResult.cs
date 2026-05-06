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
    IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? RawExports = null) : AnalyzerDomainResult;

internal sealed record ThreadClusterSnapshot(
    int Count,
    IReadOnlyList<uint> SampleOsThreadIds,
    string Signature);
