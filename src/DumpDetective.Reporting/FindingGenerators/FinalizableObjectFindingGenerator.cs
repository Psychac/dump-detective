using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class FinalizableObjectFindingGenerator : IFindingGenerator
{
    private const int Gen2WarningThreshold = 1_000;
    private const int Gen2CriticalThreshold = 10_000;
    private const ulong QueueRetainedWarningBytes = 10_000_000UL;  // 10 MB
    private const ulong QueueRetainedCriticalBytes = 100_000_000UL;  // 100 MB

    public string AnalyzerName => "Finalizable Object Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is FinalizableObjectDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not FinalizableObjectDomainResult r) return [];

        var findings = new List<InsightFinding>(3);

        // ── Gen2 finalizable accumulation ─────────────────────────────────────
        if (r.Gen2Count >= Gen2WarningThreshold)
        {
            FindingSeverity sev = r.Gen2Count >= Gen2CriticalThreshold
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string topType = r.TopFinalizableTypesByGen2Count.Count > 0
                ? r.TopFinalizableTypesByGen2Count[0].TypeName
                : "N/A";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"High Gen2 finalizable object count: {r.Gen2Count:N0} objects",
                Evidence: $"{r.Gen2Count:N0} finalizable objects survived to Gen2 " +
                          $"out of {r.TotalFinalizableObjects:N0} total ({FormatBytes(r.TotalFinalizableBytes)}). " +
                          $"Top type: {topType}.",
                Recommendation: "Gen2 finalizable objects extend memory pressure by at least two GC cycles. " +
                                "Implement IDisposable + using to enable early cleanup and call GC.SuppressFinalize " +
                                "in the Dispose method to prevent unnecessary finalization.",
                Tags: ["finalizer", "gen2", "gc", "dispose"],
                MetricValue: r.Gen2Count,
                MetricUnit: "objects"));
        }

        // ── Finalizer queue retained bytes ─────────────────────────────────────
        if (r.FinalizerQueueRetainedBytes >= QueueRetainedWarningBytes)
        {
            FindingSeverity sev = r.FinalizerQueueRetainedBytes >= QueueRetainedCriticalBytes
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"Finalizer queue retains {FormatBytes(r.FinalizerQueueRetainedBytes)} in sub-graphs",
                Evidence: $"{r.FinalizerQueueCount:N0} objects in finalizer queue. " +
                          $"Top {r.TopQueueEntriesByRetainedSize.Count} entries retain an estimated " +
                          $"{FormatBytes(r.FinalizerQueueRetainedBytes)} via bounded BFS." +
                          (r.PotentialResurrectionDetected ? " Potential resurrection pattern detected." : string.Empty),
                Recommendation: "Objects retaining large sub-graphs in the finalizer queue block GC from collecting " +
                                "those sub-graphs until finalization completes. " +
                                "Prefer IDisposable + using; avoid keeping large references in finalizable types.",
                Tags: ["finalizer", "queue", "retention", "dispose"],
                MetricValue: r.FinalizerQueueRetainedBytes,
                MetricUnit: "bytes"));
        }

        // ── Undisposed IDisposable in queue ────────────────────────────────────
        int undisposedCount = 0;
        foreach (FinalizerQueueEntry entry in r.TopQueueEntriesByRetainedSize)
        {
            if (entry.IsDisposableType && entry.DisposedFieldFound && !entry.DisposedFieldValue)
                undisposedCount++;
        }

        if (undisposedCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: $"{undisposedCount} undisposed IDisposable object(s) detected in finalizer queue",
                Evidence: $"{undisposedCount} of the top finalizer queue entries implement IDisposable " +
                          $"but have a '_disposed' field that is false, indicating they were not disposed " +
                          $"before being collected.",
                Recommendation: "Use using statements or explicit Dispose() calls to ensure all IDisposable " +
                                "objects are disposed before GC collection. This eliminates finalizer queue " +
                                "pressure for these types.",
                Tags: ["finalizer", "idisposable", "undisposed", "leak"],
                MetricValue: undisposedCount,
                MetricUnit: "objects"));
        }

        return findings;
    }

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
