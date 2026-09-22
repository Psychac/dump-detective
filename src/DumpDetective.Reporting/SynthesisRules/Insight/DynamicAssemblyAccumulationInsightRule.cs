using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectDynamicAssemblyAccumulation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class DynamicAssemblyAccumulationInsightRule : ISynthesisRule
{
    private const int WarningCount = 20;

    public string RuleId => "insight.dynamic-assembly-accumulation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["runtime.module-population"] };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        Observation appDomains = matched.Matched[0];
        long totalDynamicModules = (long)appDomains.Measures["total-dynamic.count"].Value;
        if (totalDynamicModules < WarningCount)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long anonymousModuleCount = (long)appDomains.Measures["anonymous-module.count"].Value;
        long totalDomains = (long)appDomains.Measures["total-domains.count"].Value;

        string anonNote = anonymousModuleCount > 0
            ? $" {anonymousModuleCount:N0} anonymous module(s) detected (no file path)."
            : string.Empty;

        Severity severity = totalDynamicModules > 100 ? Severity.Warning : Severity.Info;

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::dynamic-assembly-accumulation",
                Analyzer = "InsightEngine",
                Category = "Modules",
                Severity = severity,
                Title = "High dynamic/anonymous assembly count detected",
                Narrative = $"{totalDynamicModules:N0} dynamic module(s) found " +
                            $"across {totalDomains} AppDomain(s).{anonNote}",
                Recommendation = "Dynamic assemblies created by Expression.Compile, Reflection.Emit, or " +
                                 "code generators accumulate until their AssemblyLoadContext is unloaded. " +
                                 "Cache compiled expressions / delegates; prefer collectible AssemblyLoadContext " +
                                 "for plugin or script scenarios.",
                Tags = ["modules", "dynamic-assembly", "reflection", "memory-leak"],
                MetricValue = totalDynamicModules,
                MetricUnit = "modules",
                DerivedFrom = [appDomains.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
