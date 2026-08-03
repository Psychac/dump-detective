using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class AsyncStateMachineFindingGenerator : IFindingGenerator
{
    private const int FireAndForgetThreshold = 100;
    private const int HighCountWarning = 1_000;
    private const int HighCountCritical = 10_000;
    private const int MaxFireAndForgetFindings = 3;
    private const ulong LargeCaptureWarning = 50_000_000UL;  // 50 MB
    private const ulong LargeCaptureCritical = 200_000_000UL;  // 200 MB

    public string AnalyzerName => "Async State Machine Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is AsyncStateMachineDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not AsyncStateMachineDomainResult r) return [];

        var findings = new List<InsightFinding>(3);

        // ── High total state machine count ────────────────────────────────────
        if (r.TotalStateMachines >= HighCountWarning)
        {
            FindingSeverity sev = r.TotalStateMachines >= HighCountCritical
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string topType = r.TopStateMachineTypes.Count > 0
                ? r.TopStateMachineTypes[0].OriginatingMethod
                : "N/A";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"High async state machine count: {r.TotalStateMachines:N0} suspended state machines",
                Evidence: $"{r.TotalStateMachines:N0} async state machine objects found on heap " +
                          $"consuming {FormatBytes(r.TotalStateMachineBytes)}. " +
                          $"Top method: {topType}.",
                Recommendation: "Each suspended async method holds an allocation on the heap for the duration " +
                                "of the await. A high count indicates many in-flight async operations. " +
                                "Review for fire-and-forget patterns, unbounded parallelism, or awaited operations that never complete.",
                Tags: ["async", "state-machine", "memory", "suspend"],
                MetricValue: r.TotalStateMachines,
                MetricUnit: "objects"));
        }

        // ── Fire-and-forget detection (same method suspended > threshold) ──────
        int fireAndForgetCount = 0;
        foreach (SuspendedMethodEntry entry in r.SuspendedMethodMap)
        {
            if (entry.SuspendedCount >= FireAndForgetThreshold)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: FindingSeverity.Warning,
                    Title: $"Potential fire-and-forget leak: '{entry.MethodName}' has {entry.SuspendedCount:N0} suspended instances",
                    Evidence: $"{entry.SuspendedCount:N0} suspended instances of async method '{entry.MethodName}' " +
                              $"declared on '{entry.DeclaringType}' found on heap " +
                              $"(total {FormatBytes(entry.TotalBytes)}). " +
                              $"A large count for a single method suggests callers are not awaiting completion.",
                    Recommendation: "Ensure all async methods are properly awaited. " +
                                    "Fire-and-forget patterns using Task.Run or async void are common sources of " +
                                    "unbounded state machine accumulation. Consider using a managed work queue or TaskScheduler.",
                    Tags: ["async", "fire-and-forget", "leak", "state-machine"],
                    MetricValue: entry.SuspendedCount,
                    MetricUnit: "objects"));
                
                if (++fireAndForgetCount >= MaxFireAndForgetFindings)
                    break;
            }
        }

        // ── Large captured closures ────────────────────────────────────────────
        ulong totalCaptured = 0;
        foreach (HighCaptureStateMachine sm in r.TopByCapturedSize)
            totalCaptured += sm.TotalCapturedRefBytes;

        if (totalCaptured >= LargeCaptureWarning)
        {
            FindingSeverity sev = totalCaptured >= LargeCaptureCritical
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string topCapture = r.TopByCapturedSize.Count > 0
                ? $"{r.TopByCapturedSize[0].TypeName} ({FormatBytes(r.TopByCapturedSize[0].TotalCapturedRefBytes)})"
                : "N/A";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"Async state machines capturing large closures: {FormatBytes(totalCaptured)} total",
                Evidence: $"Top state machine instances retain an estimated {FormatBytes(totalCaptured)} " +
                          $"via captured reference fields. " +
                          $"Largest: {topCapture}.",
                Recommendation: "State machines capture all variables referenced across await boundaries. " +
                                "Avoid capturing large objects (DbContext, HttpClient, large arrays) in async methods. " +
                                "Consider null-ing captured references after use or restructuring the method to reduce capture scope.",
                Tags: ["async", "closure", "capture", "memory"],
                MetricValue: totalCaptured,
                MetricUnit: "bytes"));
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
