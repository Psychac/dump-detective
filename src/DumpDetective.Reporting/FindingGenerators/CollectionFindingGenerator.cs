using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
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

        // Per-kind breakdown, ordered by wasted bytes so the dominant kind reads first.
        string perKindBreakdown = string.Empty;
        CollectionKind? dominantWasteKind = null;
        if (r.WasteCountsByKind is { Count: > 0 })
        {
            var wasteBytesByKind = r.WasteBytesByKind ?? new Dictionary<CollectionKind, ulong>();
            var kindsByWaste = r.WasteCountsByKind.Keys
                .OrderByDescending(k => wasteBytesByKind.TryGetValue(k, out ulong bytes) ? bytes : 0UL)
                .ThenBy(k => k.ToString(), StringComparer.Ordinal)
                .ToList();

            perKindBreakdown = " (" + string.Join(", ", kindsByWaste.Select(k =>
            {
                wasteBytesByKind.TryGetValue(k, out ulong bytes);
                return $"{k}: {r.WasteCountsByKind[k]:N0} / {FormatHelper.FormatBytes(bytes)}";
            })) + ")";

            if (wasteBytesByKind.Count > 0)
                dominantWasteKind = kindsByWaste[0];
        }

        string evidence = $"{r.TotalCollections:N0} collections scanned; estimated unused capacity {FormatHelper.FormatBytes(r.TotalWastedMemory)} across {r.WastefulCollectionCount:N0} wasteful collections{perKindBreakdown}.";

        string recommendation = severity == FindingSeverity.Warning
            ? "Trim long-lived collections and initialize with realistic capacities."
            : "Collection sizing appears acceptable in this snapshot.";

        // If a particular kind dominates wasted bytes we can add a focused recommendation.
        var topKind = dominantWasteKind ?? r.TopWastefulCollections?.FirstOrDefault()?.Kind;
        if (topKind.HasValue && topKind.Value != CollectionKind.List)
        {
            recommendation += $" Consider addressing {topKind.Value} instances specifically (e.g., TrimExcess / Resize / Use alternative collection) if they are long-lived.";
        }

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: severity,
                Title: "Collection capacity efficiency",
                Evidence: evidence,
                Recommendation: recommendation,
                Tags: ["collections", "memory-waste", "capacity"],
                MetricValue: r.TotalWastedMemory,
                MetricUnit: "wasted-bytes")
        ];
    }
}
