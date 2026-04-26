using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class CollectionFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Collection Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is CollectionDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not CollectionDomainResult r) return [];

        // Use a moderate default; this may be overridden by a reporting-level configuration in the future.
        ulong summaryWarnBytes = 50 * 1024 * 1024;
        FindingSeverity severity = r.TotalWastedMemory >= summaryWarnBytes
            ? FindingSeverity.Warning : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: severity,
                Title: "Collection capacity efficiency",
                Evidence: $"{r.TotalCollections:N0} collections scanned; estimated unused capacity {FormatHelper.FormatBytes(r.TotalWastedMemory)} across {r.WastefulCollectionCount:N0} wasteful collections.",
                Recommendation: severity == FindingSeverity.Warning
                    ? "Trim long-lived collections and initialize with realistic capacities."
                    : "Collection sizing appears acceptable in this snapshot.",
                Tags: ["collections", "memory-waste", "capacity"],
                MetricValue: r.TotalWastedMemory,
                MetricUnit: "wasted-bytes")
        ];
    }
}
