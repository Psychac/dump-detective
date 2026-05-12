using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.SectionBuilders;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultSectionBuilderFactory : ISectionBuilderFactory
{
    public IReadOnlyList<IAnalyzerSectionBuilder> CreateAnalyzerBuilders() =>
    [
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

    public IReadOnlyList<IReportSectionBuilder> CreateReportBuilders() =>
    [
        new ExecutiveSummarySectionBuilder(),
        new MemoryTopologySectionBuilder(),
        new TypeSystemSectionBuilder(),
        new GCRootIntelligenceSectionBuilder(),
        new RetentionDominatorSectionBuilder(),
        new ThreadConcurrencySectionBuilder(),
        new AsyncAnalysisSectionBuilder(),
        new ExceptionAnalysisSectionBuilder(),
        new AppDomainAssemblySectionBuilder(),
        new FindingNarrativeSectionBuilder(),
        new InsightsSectionBuilder(),
        new GCPressureSectionBuilder(),
        new HeapSegmentDiagnosticsSectionBuilder(),
        new LeakAnalysisSectionBuilder(),
        new ConfidenceSectionBuilder()
    ];
}
