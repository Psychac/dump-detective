using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.FindingGenerators;
using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Reporting.SectionBuilders;

namespace DumpDetective.Cli.Services.Capabilities;

internal static class AnalyzerFeatureModuleSpikeCatalog
{
    public static IReadOnlyList<AnalyzerFeatureModule> CreateSpikeModules()
    {
        return
        [
            new AnalyzerFeatureModule(
                Key: "memory",
                DisplayName: "Memory Core",
                AnalyzerType: typeof(MemoryAnalyzer),
                FindingGeneratorType: typeof(MemoryFindingGenerator),
                TrendComparerType: typeof(MemoryAnalyzerTrendComparer),
                AnalyzerSectionBuilderType: typeof(MemoryAnalysisSectionBuilder),
                Order: 100,
                Tags: ["memory", "baseline", "phase2-spike"]),

            new AnalyzerFeatureModule(
                Key: "thread",
                DisplayName: "Thread Core",
                AnalyzerType: typeof(ThreadAnalyzer),
                FindingGeneratorType: typeof(ThreadFindingGenerator),
                TrendComparerType: typeof(ThreadTrendComparer),
                AnalyzerSectionBuilderType: typeof(ThreadSectionBuilder),
                Order: 200,
                Tags: ["thread", "concurrency", "phase2-spike"]),

            new AnalyzerFeatureModule(
                Key: "dominator",
                DisplayName: "Dominator",
                AnalyzerType: typeof(DominatorAnalyzer),
                FindingGeneratorType: typeof(DominatorFindingGenerator),
                TrendComparerType: typeof(DominatorTrendComparer),
                AnalyzerSectionBuilderType: typeof(DominatorSectionBuilder),
                Order: 300,
                Tags: ["retention", "dominator", "phase2-spike"])
        ];
    }
}
