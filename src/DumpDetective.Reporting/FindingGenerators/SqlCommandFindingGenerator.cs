using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class SqlCommandFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "SQL Command Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is SqlCommandDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not SqlCommandDomainResult r || !r.CommandsFound) return [];

        var findings = new List<InsightFinding>(1);

        // ── Outstanding (connection-wired) commands finding ────────────────────
        if (r.ActiveCount >= 100)
        {
            FindingSeverity sev = r.ActiveCount >= 1000 ? FindingSeverity.Critical : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.ActiveCount:N0} SQL command objects still reference a connection",
                Evidence: $"{r.ActiveCount:N0} of {r.TotalCommands:N0} command objects on the heap still " +
                          $"reference a connection object. Detached (no connection reference): {r.DisposedCount:N0}.",
                Recommendation:
                    "A large number of command objects wired to connections usually indicates commands are " +
                    "being cached or held onto by a long-lived owner rather than created per-call and disposed. " +
                    "Review command lifetime: prefer creating SqlCommand/IDbCommand instances scoped to a " +
                    "single execution and disposing them, or explicitly clear parameters/connection references " +
                    "when reusing a command instance across calls.",
                Tags: ["infrastructure", "connections", "command", "adonet"],
                MetricValue: r.ActiveCount,
                MetricUnit: "outstanding commands"));
        }

        return findings;
    }
}
