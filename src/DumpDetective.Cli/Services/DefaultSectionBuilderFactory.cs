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
        new WeakReferenceSectionBuilder(),         // B8
        new AsyncAnalysisSectionBuilder(),         // E1
        new AsyncStateMachineSectionBuilder(),     // E2
        // Domain F — Exceptions
        new ExceptionAnalysisSectionBuilder(),     // F1
        new GCHandleSectionBuilder(),              // B7
        new DependentHandleSectionBuilder(),       // B9
        // Domain G — Runtime
        new ModuleSectionBuilder(),                // G1
        new AppDomainSectionBuilder(),             // G1b
        new ReferenceChainSectionBuilder(),
        new JitSectionBuilder(),
        // Domain H — Infrastructure / Network
        new DbConnectionSectionBuilder(),          // H1
        new WcfChannelSectionBuilder(),            // H2
        new HttpObjectSectionBuilder(),            // H3
        new TimerLeakSectionBuilder(),             // H4
    ];

    public IReadOnlyList<IReportSectionBuilder> CreateReportBuilders() =>
    [
        new ExecutiveSummarySectionBuilder(),
        new TypeSystemSectionBuilder(),            // C1
        new InsightsSectionBuilder(),              // X1 — Cross-Domain Insights
        new ConfidenceSectionBuilder(),            // Z3 — Known Limitations
    ];
}
