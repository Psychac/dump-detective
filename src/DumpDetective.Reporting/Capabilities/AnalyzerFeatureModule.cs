using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Capabilities;

internal interface IAnalyzerFeatureModuleCatalog
{
    IReadOnlyList<AnalyzerFeatureModule> Modules { get; }
    IReadOnlyList<Type> GlobalReportSectionBuilderTypes { get; }
}

internal sealed record AnalyzerFeatureModule(
    string Key,
    string DisplayName,
    Type AnalyzerType,
    Type SynthesisRuleType,
    Type? TrendComparerType,
    Type AnalyzerSectionBuilderType,
    IReadOnlyList<Type> ReportSectionContributionTypes,
    int Order,
    IReadOnlyCollection<string> Tags)
{
    public bool IsShapeValid()
    {
        return !string.IsNullOrWhiteSpace(Key)
            && !string.IsNullOrWhiteSpace(DisplayName)
            && typeof(IAnalyzer).IsAssignableFrom(AnalyzerType)
            && typeof(ISynthesisRule).IsAssignableFrom(SynthesisRuleType)
            // Phase 5 step 4: a retired trend comparer drops TrendComparerType once its analyzer's
            // headline metrics are fully covered by the generic ObservationTrendDiffer (see
            // ObservationDifferAnalyzerOwnership) — no per-analyzer companion type replaces it,
            // unlike SynthesisRuleType, since one shared differ instance
            // serves every retired analyzer.
            && (TrendComparerType is null || typeof(IAnalyzerTrendComparer).IsAssignableFrom(TrendComparerType))
            && typeof(IAnalyzerSectionBuilder).IsAssignableFrom(AnalyzerSectionBuilderType)
            && ReportSectionContributionTypes.All(t => typeof(IReportSectionBuilder).IsAssignableFrom(t))
            && Order >= 0;
    }
}