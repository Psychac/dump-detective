using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.SectionBuilders;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultSectionBuilderFactory : ISectionBuilderFactory
{
    public IReadOnlyList<IAnalyzerSectionBuilder> CreateBuilders() =>
    [
        new MemorySectionBuilder(),
        new GCGenerationSectionBuilder(),
        new SegmentSectionBuilder(),
        new ModuleSectionBuilder(),
        new CrashSectionBuilder(),
        new HangSectionBuilder(),
        new AsyncTaskSectionBuilder(),
        new MemoryLeakSectionBuilder(),
        new StringSectionBuilder(),
        new CollectionSectionBuilder(),
        new StaticRootSectionBuilder(),
        new ReferenceChainSectionBuilder(),
        new GCHandleSectionBuilder(),
        new DependentHandleSectionBuilder(),
        new LohFragmentationSectionBuilder(),
        new ThreadStackClusterSectionBuilder(),
        new ThreadSectionBuilder(),
        new LockGraphSectionBuilder(),
        new EventLeakSectionBuilder()
    ];
}
