using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class DbConnectionFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "DB Connection Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is DbConnectionDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not DbConnectionDomainResult r || !r.ConnectionsFound) return [];

        var findings = new List<InsightFinding>(3);
        string stateCaveat = r.StateScanCapped
            ? " ⚠️ State sampling was capped; counts may be lower than actual totals."
            : "";

        // ── Connection count finding ───────────────────────────────────────────
        if (r.TotalConnections >= 50)
        {
            FindingSeverity sev = r.TotalConnections >= 200 ? FindingSeverity.Critical : FindingSeverity.Warning;
            string typeBreakdown = BuildTypeBreakdown(r.ByType);

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.TotalConnections:N0} DB connection objects on managed heap",
                Evidence: $"Total: {r.TotalConnections:N0} connection objects. " +
                          $"Open: {r.OpenConnections:N0}, Closed: {r.ClosedConnections:N0}, " +
                          $"Broken: {r.BrokenConnections:N0}, " +
                          $"Other (connecting/executing): {r.OtherConnections:N0}, " +
                          $"Unknown state: {r.UnknownStateConnections:N0}. " +
                          $"Types: {typeBreakdown}.{stateCaveat}",
                Recommendation:
                    "Connection objects on the heap after use indicate missing Dispose(). " +
                    "Always wrap ADO.NET connections in a using statement or try/finally with Dispose(). " +
                    "Check Max Pool Size in the connection string (default 100). " +
                    "Excess open connections will exhaust the connection pool and cause timeouts.",
                Tags: ["infrastructure", "connections", "pool", "adonet"],
                MetricValue: r.TotalConnections,
                MetricUnit: "connections"));
        }

        // ── Open connections finding ───────────────────────────────────────────
        if (r.OpenConnections >= 20)
        {
            FindingSeverity sev = FindingSeverity.Warning;
            if (r.StateScanCapped)
                sev = FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.OpenConnections:N0} DB connections in Open state",
                Evidence: $"{r.OpenConnections:N0} connections are in the Open state out of {r.TotalConnections:N0} total. " +
                          $"Open connections that are not actively used indicate leaks from missing Dispose() calls.{stateCaveat}",
                Recommendation:
                    "Each open connection occupies a connection pool slot and a server-side socket. " +
                    "Use 'using var conn = new SqlConnection(...)' pattern. " +
                    "Consider connection pooling metrics: current pool size, wait time, and timeout rate.",
                Tags: ["infrastructure", "connections", "open", "leak"],
                MetricValue: r.OpenConnections,
                MetricUnit: "open connections"));
        }

        // ── Broken connections finding ─────────────────────────────────────────
        if (r.BrokenConnections >= 5)
        {
            FindingSeverity sev = FindingSeverity.Warning;
            if (r.StateScanCapped)
                sev = FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.BrokenConnections:N0} DB connections in Broken state",
                Evidence: $"{r.BrokenConnections:N0} connections are in the Broken state out of {r.TotalConnections:N0} total. " +
                          $"Broken connections indicate a failed attempt to use or communicate with the database server.{stateCaveat}",
                Recommendation:
                    "Broken connections prevent pool recycling and can accumulate over time. " +
                    "Investigate server-side failures (network issues, server restarts, timeout errors). " +
                    "Ensure proper error handling and connection retry logic. " +
                    "Monitor database server health and availability.",
                Tags: ["infrastructure", "connections", "broken", "pool"],
                MetricValue: r.BrokenConnections,
                MetricUnit: "broken connections"));
        }

        return findings;
    }

    private static string BuildTypeBreakdown(IReadOnlyList<DbConnectionTypeSummary> byType)
    {
        if (byType.Count == 0) return "(unknown)";
        var sb = new System.Text.StringBuilder();
        int shown = 0;
        for (int i = 0; i < byType.Count && shown < 3; i++)
        {
            if (shown > 0) sb.Append(", ");
            sb.Append($"{byType[i].TypeName.Split('.')[^1]} ×{byType[i].TotalCount:N0}");
            shown++;
        }
        if (byType.Count > 3) sb.Append($" (+{byType.Count - 3} more)");
        return sb.ToString();
    }
}
