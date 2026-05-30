using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;

namespace DumpDetective.Cli.Services.Capabilities;

internal sealed record AnalyzerFeatureModule(
    string Key,
    string DisplayName,
    Type AnalyzerType,
    Type FindingGeneratorType,
    Type TrendComparerType,
    Type AnalyzerSectionBuilderType,
    int Order,
    IReadOnlyCollection<string> Tags)
{
    public bool IsShapeValid()
    {
        return !string.IsNullOrWhiteSpace(Key)
            && !string.IsNullOrWhiteSpace(DisplayName)
            && typeof(IAnalyzer).IsAssignableFrom(AnalyzerType)
            && typeof(IFindingGenerator).IsAssignableFrom(FindingGeneratorType)
            && typeof(IAnalyzerTrendComparer).IsAssignableFrom(TrendComparerType)
            && typeof(IAnalyzerSectionBuilder).IsAssignableFrom(AnalyzerSectionBuilderType)
            && Order >= 0;
    }
}
