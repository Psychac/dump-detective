using DumpDetective.Core.Abstractions;
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
    Type? FindingGeneratorType,
    Type? SynthesisRuleType,
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
            && (FindingGeneratorType is null || typeof(IFindingGenerator).IsAssignableFrom(FindingGeneratorType))
            && (SynthesisRuleType is null || typeof(ISynthesisRule).IsAssignableFrom(SynthesisRuleType))
            // Phase 5 step 3 (docs/refactor/modularity/phase-5-observations-synthesis.md, "## Work"
            // step 3): a retired analyzer drops FindingGeneratorType once its rule ships, but every
            // analyzer must keep at least one live path to a Finding — never both null at once.
            && (FindingGeneratorType is not null || SynthesisRuleType is not null)
            // Phase 5 step 4: a retired trend comparer drops TrendComparerType once its analyzer's
            // headline metrics are fully covered by the generic ObservationTrendDiffer (see
            // ObservationDifferAnalyzerOwnership) — no per-analyzer companion type replaces it,
            // unlike FindingGeneratorType/SynthesisRuleType, since one shared differ instance
            // serves every retired analyzer.
            && (TrendComparerType is null || typeof(IAnalyzerTrendComparer).IsAssignableFrom(TrendComparerType))
            && typeof(IAnalyzerSectionBuilder).IsAssignableFrom(AnalyzerSectionBuilderType)
            && ReportSectionContributionTypes.All(t => typeof(IReportSectionBuilder).IsAssignableFrom(t))
            && Order >= 0;
    }
}