using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class MemoryFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Memory Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is MemoryDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not MemoryDomainResult r) return [];

        FindingSeverity severity = r.LohPercent >= 40 ? FindingSeverity.Warning : FindingSeverity.Info;
        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: severity,
                Title: "Managed heap memory snapshot",
                Evidence: $"Total heap: {FormatBytes(r.TotalBytes)}; LOH share: {r.LohPercent:F1}% ({FormatBytes(r.LohBytes)}); unique types: {r.UniqueTypes:N0}.",
                Recommendation: severity == FindingSeverity.Warning
                    ? "LOH share is elevated. Review large-object allocations and retention patterns."
                    : "Heap composition appears within expected range for this snapshot.",
                Tags: ["memory", "heap", "loh"],
                MetricValue: r.LohPercent,
                MetricUnit: "% loh")
        ];
    }

    private static string FormatBytes(ulong bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F2} GB"
        : bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB"
        : bytes >= 1_024 ? $"{bytes / 1_024.0:F1} KB"
        : $"{bytes} B";
}
