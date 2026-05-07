using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.SectionBuilders;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultSectionBuilderFactory : ISectionBuilderFactory
{
    public IReadOnlyList<IAnalyzerSectionBuilder> CreateBuilders() =>
    [
        new MemorySectionBuilder(),
        new GCGenerationSectionBuilder(),
        new AllocationPatternSectionBuilder(),
        new ObjectShapeSectionBuilder(),
        new GCRootSectionBuilder(),
        new SegmentSectionBuilder(),
        new SegmentReservationSectionBuilder(),
        new ModuleSectionBuilder(),
        new AppDomainSectionBuilder(),
        new CrashSectionBuilder(),
        new HangSectionBuilder(),
        new AsyncTaskSectionBuilder(),
        new RetentionSectionBuilder(),
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
        new EventLeakSectionBuilder(),
        new FinalizableObjectSectionBuilder(),
        new ArraySectionBuilder(),
        new AsyncStateMachineSectionBuilder(),
        new WeakReferenceSectionBuilder(),
        new BoxingSectionBuilder(),
        new JitSectionBuilder()
    ];
}
