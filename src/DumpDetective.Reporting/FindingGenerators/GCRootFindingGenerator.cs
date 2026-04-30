using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class GCRootFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "GC Root Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is GCRootDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not GCRootDomainResult r) return [];

        var findings = new List<InsightFinding>(2);

        // ── Large static/strong root retention ─────────────────────────────
        ulong staticBytes = 0;
        int   staticCount = 0;
        foreach (RootKindSummary ks in r.ByKind)
        {
            if (ks.Kind is "StrongHandle")
            {
                staticBytes = ks.EstimatedRetainedBytes;
                staticCount = ks.Count;
            }
        }

        if (staticBytes >= 10_000_000) // 10 MB+
        {
            string evidence =
                $"{staticCount:N0} strong-handle (static) roots estimated to retain {FormatBytes(staticBytes)}. " +
                $"Total roots: {r.TotalRoots:N0}. Top suspect: {(r.TopRootsBySeverity.Count > 0 ? r.TopRootsBySeverity[0].TargetTypeName : "N/A")}.";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: staticBytes >= 100_000_000 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title: $"Large static root retention: {FormatBytes(staticBytes)} via {staticCount:N0} strong handles",
                Evidence: evidence,
                Recommendation: "Inspect static fields and GC handles retaining large object graphs. " +
                                "Consider weak references, clearing stale caches, or scoping long-lived state.",
                Tags: ["gc", "roots", "static", "retention", "leak"],
                MetricValue: staticBytes,
                MetricUnit: "bytes"));
        }

        // ── Finalizer queue pressure ──────────────────────────────────────
        int finalizerCount = 0;
        foreach (RootKindSummary ks in r.ByKind)
        {
            if (ks.Kind is "FinalizerQueue")
                finalizerCount = ks.Count;
        }

        if (finalizerCount >= 500)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: finalizerCount >= 5_000 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: $"Finalizer queue pressure: {finalizerCount:N0} finalizable roots",
                Evidence: $"{finalizerCount:N0} objects are rooted via the finalizer queue. " +
                          "These objects require an extra GC cycle to be freed.",
                Recommendation: "Ensure finalizable objects call Dispose() promptly. " +
                                "Consider suppressing finalization after disposal via GC.SuppressFinalize().",
                Tags: ["gc", "roots", "finalizer", "pressure"],
                MetricValue: finalizerCount,
                MetricUnit: "objects"));
        }

        // ── Root path cap signal ──────────────────────────────────────────
        if (r.PathSearchCapped && r.PathSearchCappedCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Confidence",
                Severity: FindingSeverity.Info,
                Title: $"Root path search capped ({r.PathSearchCappedCount} paths truncated)",
                Evidence: $"BFS traversal hit the {r.PathSearchCappedCount} node/depth budget for some root paths. " +
                          "Root path type chains may be incomplete for deeply nested object graphs.",
                Recommendation: "Results are indicative — large object graphs may have deeper retention chains not shown.",
                Tags: ["gc", "roots", "confidence", "cap"],
                MetricValue: r.PathSearchCappedCount,
                MetricUnit: "paths"));
        }

        return findings;
    }

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024         => $"{bytes / 1_024.0:F1} KB",
        _                => $"{bytes} B"
    };
}
