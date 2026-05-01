using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class WeakReferenceFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Weak Reference Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is WeakReferenceDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not WeakReferenceDomainResult r) return [];

        var findings = new List<InsightFinding>();

        // High dead-target ratio — stale wrapper accumulation.
        if (r.TotalWeakHandles > 0 && r.DeadTargetRatio >= 0.5)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: r.DeadTargetRatio >= 0.8 ? FindingSeverity.Critical : FindingSeverity.Warning,
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
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Info,
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
        string scanNote = r.ScanCapped ? " (scan capped at 50 000 handles)" : string.Empty;
        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Memory",
            Severity: FindingSeverity.Info,
            Title: "Weak reference overview",
            Evidence: $"Total weak handles: {r.TotalWeakHandles:N0}{scanNote}. " +
                      $"Alive targets: {r.AliveWeakTargets:N0}, dead: {r.DeadWeakTargets:N0} " +
                      $"({r.DeadTargetRatio:P1}). " +
                      $"WeakReference objects: {r.WeakReferenceObjectCount:N0} " +
                      $"({FormatHelper.FormatBytes(r.WeakReferenceObjectBytes)}). " +
                      $"Stale wrappers: {r.StaleWrapperCount:N0}.",
            Recommendation: r.DeadTargetRatio < 0.5
                ? "Weak handle health is acceptable."
                : "Investigate stale cache entries or missed cleanup paths.",
            Tags: ["weak-reference", "handles"],
            MetricValue: (double)r.TotalWeakHandles,
            MetricUnit: "handles"));

        return findings;
    }
}
