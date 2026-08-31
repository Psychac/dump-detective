using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class SqlConnectionPoolFindingGenerator : IFindingGenerator
{
    private const double CriticalUtilizationPct = 95.0;

    public string AnalyzerName => "SQL Connection Pool Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is SqlConnectionPoolDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not SqlConnectionPoolDomainResult r || !r.PoolsFound) return [];
        if (r.PoolsNearCapacity == 0) return [];

        SqlConnectionPoolSnapshot? worst = null;
        double worstPct = -1;
        foreach (SqlConnectionPoolSnapshot pool in r.Pools)
        {
            double pct = SqlConnectionPoolAnalyzer.UtilizationPercent(pool);
            if (pct > worstPct)
            {
                worstPct = pct;
                worst = pool;
            }
        }

        if (worst is null || worstPct < 0) return [];

        FindingSeverity sev = worstPct >= CriticalUtilizationPct ? FindingSeverity.Critical : FindingSeverity.Warning;
        string poolLabel = worst.AnonymisedConnectionString ?? "(unknown server/database)";

        var findings = new List<InsightFinding>(1)
        {
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.PoolsNearCapacity:N0} SQL connection pool(s) near capacity",
                Evidence: $"Highest utilisation pool is at {worstPct:F1}% ({worst.CurrentSize:N0}/{worst.MaxPoolSize:N0} " +
                          $"connections) — {poolLabel}. {r.PoolsNearCapacity:N0} of {r.TotalPools:N0} discovered " +
                          "pools are at or above 80% of Max Pool Size.",
                Recommendation:
                    "This is read directly from the connection pool manager's own counters, not estimated. " +
                    "Increase Max Pool Size in the connection string if legitimate concurrent demand requires " +
                    "it, or investigate connection leaks (missing Dispose()) feeding this specific pool.",
                Tags: ["infrastructure", "connections", "pool", "pool-exhaustion"],
                MetricValue: worstPct,
                MetricUnit: "% utilised")
        };

        return findings;
    }
}
