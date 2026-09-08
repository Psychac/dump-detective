using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class SqlTransactionFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "SQL Transaction Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is SqlTransactionDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not SqlTransactionDomainResult r || !r.TransactionsFound) return [];

        var findings = new List<InsightFinding>(1);

        // ── Active (long-held) transactions finding ────────────────────────────
        if (r.ActiveCount >= 5)
        {
            FindingSeverity sev = r.ActiveCount >= 25 ? FindingSeverity.Critical : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.ActiveCount:N0} SQL transaction objects still hold an open connection",
                Evidence: $"{r.ActiveCount:N0} of {r.TotalTransactions:N0} transaction objects on the heap still " +
                          $"reference an owning connection (not yet Committed/Rolled back/Disposed). " +
                          $"Disposed: {r.DisposedCount:N0}, Other: {r.OtherCount:N0}.",
                Recommendation:
                    "A transaction object that is still on the heap and references its connection prevents that " +
                    "connection from returning to the pool, even if the connection object itself looks idle. " +
                    "Always wrap SqlTransaction/IDbTransaction in a using statement, and Commit or Rollback as " +
                    "soon as the unit of work completes — do not hold transactions open across unrelated calls " +
                    "or await points.",
                Tags: ["infrastructure", "connections", "transaction", "pool"],
                MetricValue: r.ActiveCount,
                MetricUnit: "active transactions"));
        }

        return findings;
    }
}
