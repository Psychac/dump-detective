using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;

namespace DumpDetective.Cli.Services.Capabilities;

internal static class AnalyzerFeatureModuleAdapter
{
    public static IReadOnlyList<AnalyzerFeatureModule> CreateResolvedModules(
        IReadOnlyList<IAnalyzer> analyzers,
        IReadOnlyList<IFindingGenerator> findingGenerators,
        IReadOnlyList<IAnalyzerTrendComparer> trendComparers,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerSectionBuilders)
    {
        Dictionary<string, IFindingGenerator> generatorByAnalyzer = findingGenerators
            .GroupBy(g => g.AnalyzerName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        Dictionary<string, IAnalyzerTrendComparer> comparerByAnalyzer = trendComparers
            .GroupBy(c => c.AnalyzerName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        Dictionary<string, IAnalyzerSectionBuilder> sectionBuilderByAnalyzer = analyzerSectionBuilders
            .GroupBy(b => b.AnalyzerName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        List<AnalyzerFeatureModule> modules = [];
        HashSet<string> usedKeys = new(StringComparer.Ordinal);

        for (int i = 0; i < analyzers.Count; i++)
        {
            IAnalyzer analyzer = analyzers[i];

            if (!generatorByAnalyzer.TryGetValue(analyzer.Name, out IFindingGenerator? generator))
                continue;

            if (!comparerByAnalyzer.TryGetValue(analyzer.Name, out IAnalyzerTrendComparer? comparer))
                continue;

            if (!sectionBuilderByAnalyzer.TryGetValue(analyzer.Name, out IAnalyzerSectionBuilder? sectionBuilder))
                continue;

            string key = ToKey(analyzer.GetType().Name, usedKeys);

            modules.Add(new AnalyzerFeatureModule(
                Key: key,
                DisplayName: analyzer.Name,
                AnalyzerType: analyzer.GetType(),
                FindingGeneratorType: generator.GetType(),
                TrendComparerType: comparer.GetType(),
                AnalyzerSectionBuilderType: sectionBuilder.GetType(),
                ReportSectionContributionTypes: [],
                Order: i,
                Tags: ["resolved", "phase2-adapter"]));
        }

        return modules;
    }

    public static AnalyzerFeatureModuleCoverage ComputeCoverage(
        IReadOnlyList<AnalyzerFeatureModule> modules,
        IReadOnlyList<IAnalyzer> analyzers,
        IReadOnlyList<IFindingGenerator> findingGenerators,
        IReadOnlyList<IAnalyzerTrendComparer> trendComparers,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerSectionBuilders)
    {
        HashSet<Type> analyzerTypes = analyzers.Select(a => a.GetType()).ToHashSet();
        HashSet<Type> generatorTypes = findingGenerators.Select(g => g.GetType()).ToHashSet();
        HashSet<Type> comparerTypes = trendComparers.Select(c => c.GetType()).ToHashSet();
        HashSet<Type> sectionBuilderTypes = analyzerSectionBuilders.Select(b => b.GetType()).ToHashSet();

        List<string> invalidShape = modules
            .Where(m => !m.IsShapeValid())
            .Select(m => m.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        List<string> missingAnalyzerModules = analyzers
            .Where(a => !modules.Any(m => m.AnalyzerType == a.GetType()))
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        List<string> missingFindingGenerators = modules
            .Where(m => !generatorTypes.Contains(m.FindingGeneratorType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        List<string> missingTrendComparers = modules
            .Where(m => !comparerTypes.Contains(m.TrendComparerType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        List<string> missingAnalyzerSectionBuilders = modules
            .Where(m => !sectionBuilderTypes.Contains(m.AnalyzerSectionBuilderType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        List<string> extraAnalyzerTypes = modules
            .Where(m => !analyzerTypes.Contains(m.AnalyzerType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new AnalyzerFeatureModuleCoverage(
            ModuleCount: modules.Count,
            AnalyzerCount: analyzers.Count,
            MissingAnalyzerModules: missingAnalyzerModules,
            MissingFindingGenerators: missingFindingGenerators,
            MissingTrendComparers: missingTrendComparers,
            MissingAnalyzerSectionBuilders: missingAnalyzerSectionBuilders,
            ExtraAnalyzerTypes: extraAnalyzerTypes,
            InvalidShapeModules: invalidShape);
    }

    private static string ToKey(string typeName, HashSet<string> usedKeys)
    {
        string baseName = typeName.EndsWith("Analyzer", StringComparison.Ordinal)
            ? typeName[..^"Analyzer".Length]
            : typeName;

        string key = baseName.ToLowerInvariant();
        string candidate = key;
        int suffix = 2;

        while (!usedKeys.Add(candidate))
        {
            candidate = key + "-" + suffix;
            suffix++;
        }

        return candidate;
    }
}

internal sealed record AnalyzerFeatureModuleCoverage(
    int ModuleCount,
    int AnalyzerCount,
    IReadOnlyList<string> MissingAnalyzerModules,
    IReadOnlyList<string> MissingFindingGenerators,
    IReadOnlyList<string> MissingTrendComparers,
    IReadOnlyList<string> MissingAnalyzerSectionBuilders,
    IReadOnlyList<string> ExtraAnalyzerTypes,
    IReadOnlyList<string> InvalidShapeModules)
{
    public bool HasFullCoverage =>
        MissingAnalyzerModules.Count == 0
        && MissingFindingGenerators.Count == 0
        && MissingTrendComparers.Count == 0
        && MissingAnalyzerSectionBuilders.Count == 0
        && ExtraAnalyzerTypes.Count == 0
        && InvalidShapeModules.Count == 0;
}