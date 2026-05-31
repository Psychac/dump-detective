using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.Abstractions;

namespace DumpDetective.Cli.Services.Capabilities;

internal static class AnalyzerFeatureModuleAdapter
{
    public static IReadOnlyList<AnalyzerFeatureModule> CreateResolvedModules(
        IReadOnlyList<IAnalyzer> analyzers,
        IEnumerable<IFindingGenerator> findingGenerators,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        IEnumerable<IAnalyzerSectionBuilder> analyzerSectionBuilders)
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

        var modules = new List<AnalyzerFeatureModule>();
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
                ReportSectionContributionTypes: Array.Empty<Type>(),
                Order: i,
                Tags: new[] { "resolved", "phase2-adapter" }));
        }

        return modules;
    }

    public static AnalyzerFeatureModuleCoverage ComputeCoverage(
        IEnumerable<AnalyzerFeatureModule> modules,
        IReadOnlyList<IAnalyzer> analyzers,
        IEnumerable<IFindingGenerator> findingGenerators,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        IEnumerable<IAnalyzerSectionBuilder> analyzerSectionBuilders)
    {
        HashSet<Type> analyzerTypes = analyzers.Select(a => a.GetType()).ToHashSet();
        HashSet<Type> generatorTypes = findingGenerators.Select(g => g.GetType()).ToHashSet();
        HashSet<Type> comparerTypes = trendComparers.Select(c => c.GetType()).ToHashSet();
        HashSet<Type> sectionBuilderTypes = analyzerSectionBuilders.Select(b => b.GetType()).ToHashSet();

        var modulesList = modules as IReadOnlyList<AnalyzerFeatureModule> ?? modules.ToArray();

        string[] invalidShape = modulesList
            .Where(m => !m.IsShapeValid())
            .Select(m => m.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        string[] missingAnalyzerModules = analyzers
            .Where(a => !modulesList.Any(m => m.AnalyzerType == a.GetType()))
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] missingFindingGenerators = modulesList
            .Where(m => !generatorTypes.Contains(m.FindingGeneratorType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] missingTrendComparers = modulesList
            .Where(m => !comparerTypes.Contains(m.TrendComparerType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] missingAnalyzerSectionBuilders = modulesList
            .Where(m => !sectionBuilderTypes.Contains(m.AnalyzerSectionBuilderType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] extraAnalyzerTypes = modulesList
            .Where(m => !analyzerTypes.Contains(m.AnalyzerType))
            .Select(m => m.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        return new AnalyzerFeatureModuleCoverage(
            ModuleCount: modulesList.Count,
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