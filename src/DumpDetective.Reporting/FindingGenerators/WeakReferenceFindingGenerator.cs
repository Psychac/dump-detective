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

        var signals = new List<WeakRefSignal>(3);

        // §24.2 P3-4: qualifies StaleWrapperCount wherever it's cited — see
        // WeakReferenceDomainResult.StaleWrapperCountIsExact for when this applies.
        string staleWrapperQualifier = r.StaleWrapperCountIsExact ? "" : " (estimated)";

        // High dead-target ratio — stale wrapper accumulation.
        if (r.TotalWeakHandles > 0 && r.DeadTargetRatio >= 0.5)
        {
            signals.Add(new WeakRefSignal(
                Severity: r.DeadTargetRatio >= 0.8 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Priority: 200,
                Title: "High proportion of dead weak handle targets",
                Evidence: $"{r.DeadWeakTargets:N0} of {r.TotalWeakHandles:N0} weak handles " +
                          $"({r.DeadTargetRatio:P1}) point to already-collected objects. " +
                          $"Stale WeakReference wrappers: {r.StaleWrapperCount:N0}{staleWrapperQualifier}.",
                Recommendation: "Audit caches and event subscriptions that hold WeakReference objects. " +
                                "Ensure entries are purged after the target is collected to avoid accumulation.",
                Tags: ["weak-reference", "handles", "cache", "memory-leak"],
                MetricValue: r.DeadTargetRatio,
                MetricUnit: "ratio"));
        }

        // Absolute dead-count threshold signal (complementary to ratio).
        const int absoluteDeadCountThreshold = 10_000;
        if (r.DeadWeakTargets > absoluteDeadCountThreshold)
        {
            signals.Add(new WeakRefSignal(
                Severity: r.DeadWeakTargets > 100_000 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Priority: 150,
                Title: "High absolute count of dead weak handle targets",
                Evidence: $"{r.DeadWeakTargets:N0} dead weak handles accumulated. " +
                          $"Large-scale applications with millions of handles can have benign ratios but still accumulate significant dead count.",
                Recommendation: "Review cache retention patterns and event subscription cleanup. " +
                                "Implement periodic purging of dead entries to prevent unbounded growth.",
                Tags: ["weak-reference", "handles", "cache", "memory-leak"],
                MetricValue: r.DeadWeakTargets,
                MetricUnit: "handles"));
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

        // "Held only via weak reference" signal — objects still alive but unreachable from any
        // GC root, so the weak handle is the only known reference and they'll be collected next GC.
        if (r.HeldOnlyViaWeakReferenceDetectionAvailable && r.HeldOnlyViaWeakReferenceCount > 0)
        {
            string topTypeSuffix = r.HeldOnlyViaWeakReferenceTopTypes is { Count: > 0 } topTypes
                ? $" Most common: {topTypes[0].Name} ({topTypes[0].Count:N0})."
                : "";

            signals.Add(new WeakRefSignal(
                Severity: FindingSeverity.Info,
                Priority: 120,
                Title: "Objects held only via weak reference",
                Evidence: $"{r.HeldOnlyViaWeakReferenceCount:N0} alive weak target(s) are unreachable from any GC root — " +
                          $"the weak handle is currently the only known reference to them.{topTypeSuffix}",
                Recommendation: "Informational: these objects are pending collection on the next GC and are not " +
                                "themselves a leak. If this count is unexpectedly large, it may indicate churn in a " +
                                "cache or subscription pattern that repeatedly creates short-lived weakly-referenced objects.",
                Tags: ["weak-reference", "gc-root", "diagnostic"],
                MetricValue: r.HeldOnlyViaWeakReferenceCount,
                MetricUnit: "objects"));
        }

        // Summary finding (always).
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
            Evidence: $"Total weak handles: {r.TotalWeakHandles:N0}. " +
                      $"Alive targets: {r.AliveWeakTargets:N0}, dead: {r.DeadWeakTargets:N0} " +
                      $"({r.DeadTargetRatio:P1}). " +
                      $"WeakReference objects: {r.WeakReferenceObjectCount:N0} " +
                      $"({FormatHelper.FormatBytes(r.WeakReferenceObjectBytes)}). " +
                      $"Stale wrappers: {r.StaleWrapperCount:N0}{staleWrapperQualifier}.",
            Recommendation: signals.Count > 0
                ? $"Review the {signals.Count} weak-reference signal(s) below."
                : "Weak handle health is acceptable.",
            Tags: ["weak-reference", "handles", "overview"],
            MetricValue: (double)r.TotalWeakHandles,
            MetricUnit: "handles")
        };

        if (signals.Count > 0)
        {
            // Sort by severity (descending) then priority (descending)
            var sortedSignals = signals.OrderByDescending(s => SeverityRank(s.Severity))
                                       .ThenByDescending(s => s.Priority)
                                       .ToList();

            foreach (var signal in sortedSignals)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Memory",
                    Severity: signal.Severity,
                    Title: signal.Title,
                    Evidence: signal.Evidence,
                    Recommendation: signal.Recommendation,
                    Tags: signal.Tags,
                    MetricValue: signal.MetricValue,
                    MetricUnit: signal.MetricUnit));
            }
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
