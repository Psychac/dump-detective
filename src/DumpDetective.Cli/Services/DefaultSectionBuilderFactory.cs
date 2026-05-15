using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.SectionBuilders;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultSectionBuilderFactory : ISectionBuilderFactory
{
    public IReadOnlyList<IAnalyzerSectionBuilder> CreateAnalyzerBuilders() =>
    [
        // Domain A — Memory & Leaks
        new LeakAnalysisSectionBuilder(),          // A1
        new MemoryTopologySectionBuilder(),        // A2
        new DominatorSectionBuilder(),             // A3
        new RetentionSectionBuilder(),             // A4
        new GCRootIntelligenceSectionBuilder(),    // A5
        new StaticRootSectionBuilder(),            // A6
        new StringSectionBuilder(),                // A7
        // Domain B — GC Health
        new GCPressureSectionBuilder(),            // B1
        new AllocationPatternSectionBuilder(),     // B2
        new HeapSegmentDiagnosticsSectionBuilder(), // B3
        new LohFragmentationSectionBuilder(),      // B4
        new SegmentReservationSectionBuilder(),    // B5
        new FinalizableObjectSectionBuilder(),     // B6
        // Domain C — Type System
        new ObjectShapeSectionBuilder(),           // C2
        new CollectionSectionBuilder(),            // C3
        new ArraySectionBuilder(),                 // C4
        new BoxingSectionBuilder(),                // C5
        // Domain D — Threads & Concurrency
        new ThreadSectionBuilder(),                // D1
        new HangSectionBuilder(),                  // D2
        new LockGraphSectionBuilder(),             // D3
        new ThreadStackClusterSectionBuilder(),    // D3 (stack clusters)
        new EventLeakSectionBuilder(),             // D4
        // Domain E — Async
        new AsyncAnalysisSectionBuilder(),         // E1
        new AsyncStateMachineSectionBuilder(),     // E2
        // Domain F — Exceptions
        new ExceptionAnalysisSectionBuilder(),     // F1
        // Domain G — Runtime
        new ReferenceChainSectionBuilder(),
        new JitSectionBuilder(),
    ];

    public IReadOnlyList<IReportSectionBuilder> CreateReportBuilders() =>
    [
        new ExecutiveSummarySectionBuilder(),
        new TypeSystemSectionBuilder(),
        new AppDomainAssemblySectionBuilder(),
        new GCHandlesCombinedSectionBuilder(),     // B7
        new FindingNarrativeSectionBuilder(),
        new InsightsSectionBuilder(),
        new ConfidenceSectionBuilder(),
    ];
}
