using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class WeakReferenceFindingGenerator : IFindingGenerator
{
    private readonly record struct WeakRefSignal(
        FindingSeverity Severity,
        int Priority,
        string Title,
        string Evidence,
        string Recommendation,
        string[] Tags,
        double MetricValue,
        string MetricUnit);

    public string AnalyzerName => "Weak Reference Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is WeakReferenceDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not WeakReferenceDomainResult r) return [];

        var signals = new List<WeakRefSignal>(2);

        // High dead-target ratio — stale wrapper accumulation.
        if (r.TotalWeakHandles > 0 && r.DeadTargetRatio >= 0.5)
        {
            signals.Add(new WeakRefSignal(
                Severity: r.DeadTargetRatio >= 0.8 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Priority: 200,
                Title: "High proportion of dead weak handle targets",
                Evidence: $"{r.DeadWeakTargets:N0} of {r.TotalWeakHandles:N0} weak handles " +
                          $"({r.DeadTargetRatio:P1}) point to already-collected objects. " +
                          $"Stale WeakReference wrappers: {r.StaleWrapperCount:N0}.",
                Recommendation: "Audit caches and event subscriptions that hold WeakReference objects. " +
                                "Ensure entries are purged after the target is collected to avoid accumulation.",
                Tags: ["weak-reference", "handles", "cache", "memory-leak"],
                MetricValue: r.DeadTargetRatio,
                MetricUnit: "ratio"));
        }

        // Dependent handle dead-key signal.
        if (r.DependentHandleDeadKeyCount > 0)
        {
            signals.Add(new WeakRefSignal(
                Severity: FindingSeverity.Info,
                Priority: 100,
                Title: "Dependent handles with dead primary keys detected",
                Evidence: $"{r.DependentHandleDeadKeyCount:N0} dependent handle(s) have a dead primary key, " +
                          $"indicating ConditionalWeakTable entries whose source object has been collected.",
                Recommendation: "Review ConditionalWeakTable usage; dead-key entries are cleaned up by the GC " +
                                "but a high count may indicate object lifecycle issues.",
                Tags: ["dependent-handle", "conditional-weak-table"],
                MetricValue: r.DependentHandleDeadKeyCount,
                MetricUnit: "handles"));
        }

        // Summary finding (always).
        string scanNote = r.ScanCapped ? $" (scan capped at {r.ScanCapUsed:N0} handles)" : string.Empty;
        FindingSeverity summarySeverity = FindingSeverity.Info;
        for (int i = 0; i < signals.Count; i++)
        {
            if (SeverityRank(signals[i].Severity) > SeverityRank(summarySeverity))
                summarySeverity = signals[i].Severity;
        }

        var findings = new List<InsightFinding>(2)
        {
            new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Memory",
            Severity: summarySeverity,
            Title: "Weak reference overview",
            Evidence: $"Total weak handles: {r.TotalWeakHandles:N0}{scanNote}. " +
                      $"Alive targets: {r.AliveWeakTargets:N0}, dead: {r.DeadWeakTargets:N0} " +
                      $"({r.DeadTargetRatio:P1}). " +
                      $"WeakReference objects: {r.WeakReferenceObjectCount:N0} " +
                      $"({FormatHelper.FormatBytes(r.WeakReferenceObjectBytes)}). " +
                      $"Stale wrappers: {r.StaleWrapperCount:N0}.",
            Recommendation: signals.Count > 0
                ? "Review the highest-impact weak-reference signal below."
                : "Weak handle health is acceptable.",
            Tags: ["weak-reference", "handles", "overview"],
            MetricValue: (double)r.TotalWeakHandles,
            MetricUnit: "handles")
        };

        if (signals.Count > 0)
        {
            WeakRefSignal top = signals[0];
            for (int i = 1; i < signals.Count; i++)
            {
                WeakRefSignal s = signals[i];
                bool betterSeverity = SeverityRank(s.Severity) > SeverityRank(top.Severity);
                bool sameSeverityHigherPriority = SeverityRank(s.Severity) == SeverityRank(top.Severity) && s.Priority > top.Priority;
                if (betterSeverity || sameSeverityHigherPriority)
                    top = s;
            }

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: top.Severity,
                Title: top.Title,
                Evidence: top.Evidence,
                Recommendation: top.Recommendation,
                Tags: top.Tags,
                MetricValue: top.MetricValue,
                MetricUnit: top.MetricUnit));
        }

        return findings;
    }

    private static int SeverityRank(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 3,
        FindingSeverity.Warning => 2,
        _ => 1
    };
}
