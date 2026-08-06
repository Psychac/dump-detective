using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class TimerLeakFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Timer Leak Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is TimerLeakDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not TimerLeakDomainResult r || !r.TimersFound)
            return [];

        var findings = new List<InsightFinding>(2);

        if (r.LogicalTimerCount >= 100)
        {
            FindingSeverity severity = r.LogicalTimerCount >= 250 ? FindingSeverity.Critical : FindingSeverity.Warning;

            Evidence? topEvidence = null;
            int topCount = -1;
            for (int i = 0; i < r.ByType.Count; i++)
            {
                TimerObjectTypeSummary type = r.ByType[i];
                if (type.Count > topCount)
                {
                    topCount = type.Count;
                    topEvidence = type.Evidence;
                }
            }

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: severity,
                Title: $"{r.LogicalTimerCount:N0} logical timers (undisposed) on managed heap",
                Evidence: $"Raw object count: {r.TotalTimers:N0} (Threading.Timer={r.ThreadingTimerCount:N0}, Timers.Timer={r.TimersTimerCount:N0}, " +
                          $"TimerQueueTimer={r.TimerQueueTimerCount:N0}, TimerHolder={r.TimerHolderCount:N0}, PeriodicTimer={r.PeriodicTimerCount:N0}, Other={r.OtherTimerCount:N0}).",
                Recommendation: "Dispose timers explicitly when they are no longer needed. " +
                                "Avoid creating per-request or per-entity timers; use shared scheduling services where possible.",
                Tags: ["infrastructure", "timer", "leak", "dispose"],
                MetricValue: r.LogicalTimerCount,
                MetricUnit: "timers",
                ConfidenceScore: EvidenceConfidence.Compute(topEvidence)));
        }

        int queuePressure = r.TimerHolderCount + r.TimerQueueTimerCount;
        if (queuePressure >= 50)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: queuePressure >= 150 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Timer queue/finalizer pressure detected",
                Evidence: $"{queuePressure:N0} TimerHolder/TimerQueueTimer object(s) were observed, which typically indicates undisposed System.Threading.Timer usage.",
                Recommendation: "Track timer lifetimes and call Dispose() on completion or shutdown. " +
                                "For periodic async loops, prefer PeriodicTimer or hosted background services.",
                Tags: ["timer", "timerqueue", "finalizer", "dispose"],
                MetricValue: queuePressure,
                MetricUnit: "timer queue objects"));
        }

        return findings;
    }
}
