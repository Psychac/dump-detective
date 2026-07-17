using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SectionBuilders;

namespace DumpDetective.Reporting.Capabilities;

internal interface IAnalyzerFeatureModuleCatalog
{
    IReadOnlyList<AnalyzerFeatureModule> Modules { get; }
    IReadOnlyList<Type> GlobalReportSectionBuilderTypes { get; }
}

internal sealed class DefaultAnalyzerFeatureModuleCatalog : IAnalyzerFeatureModuleCatalog
{
    public IReadOnlyList<AnalyzerFeatureModule> Modules { get; } =
    [
        Module("memory", "Memory Analysis", typeof(MemoryAnalyzer), typeof(MemoryFindingGenerator), typeof(MemoryAnalyzerTrendComparer), typeof(MemoryAnalysisSectionBuilder), 100, ["memory"]),
        Module("gc-generation", "GC Generation Analysis", typeof(GCGenerationAnalyzer), typeof(GCGenerationFindingGenerator), typeof(GCGenerationTrendComparer), typeof(GCPressureSectionBuilder), 110, ["gc"]),
        Module("allocation-pattern", "Allocation Pattern Analysis", typeof(AllocationPatternAnalyzer), typeof(AllocationPatternFindingGenerator), typeof(AllocationPatternTrendComparer), typeof(AllocationPatternSectionBuilder), 120, ["gc", "allocation"]),
        Module("object-shape", "Object Shape Analysis", typeof(ObjectShapeAnalyzer), typeof(ObjectShapeFindingGenerator), typeof(ObjectShapeTrendComparer), typeof(ObjectShapeSectionBuilder), 130, ["types"]),
        Module("gc-root", "GC Root Analysis", typeof(GCRootAnalyzer), typeof(GCRootFindingGenerator), typeof(GCRootTrendComparer), typeof(GCRootIntelligenceSectionBuilder), 140, ["roots"]),
        Module("heap-topology", "Heap Topology Analysis", typeof(HeapTopologyAnalyzer), typeof(HeapTopologyFindingGenerator), typeof(HeapTopologyTrendComparer), typeof(HeapTopologySectionBuilder), 150, ["heap"]),
        Module("module", "Module Analysis", typeof(ModuleAnalyzer), typeof(ModuleFindingGenerator), typeof(ModuleTrendComparer), typeof(ModuleSectionBuilder), 160, ["runtime"]),
        Module("crash", "Crash Analysis", typeof(CrashAnalyzer), typeof(CrashFindingGenerator), typeof(CrashTrendComparer), typeof(ExceptionAnalysisSectionBuilder), 170, ["exceptions"]),
        Module("hang", "Hang Analysis", typeof(HangAnalyzer), typeof(HangFindingGenerator), typeof(HangTrendComparer), typeof(HangSectionBuilder), 180, ["threads"]),
        Module("async-task", "Async Task Analysis", typeof(AsyncTaskAnalyzer), typeof(AsyncTaskFindingGenerator), typeof(AsyncTaskTrendComparer), typeof(AsyncAnalysisSectionBuilder), 190, ["async"]),
        Module("retention", "Retention Analysis", typeof(RetentionAnalyzer), typeof(RetentionFindingGenerator), typeof(RetentionTrendComparer), typeof(RetentionSectionBuilder), 200, ["memory", "retention"]),
        Module("leak-candidate", "Leak Candidate Analysis", typeof(LeakCandidateAnalyzer), typeof(LeakCandidateFindingGenerator), typeof(LeakCandidateTrendComparer), typeof(LeakAnalysisSectionBuilder), 210, ["leaks"]),
        Module("dominator", "Dominator Analysis", typeof(DominatorAnalyzer), typeof(DominatorFindingGenerator), typeof(DominatorTrendComparer), typeof(DominatorSectionBuilder), 220, ["retention", "dominator"]),
        Module("string", "String Analysis", typeof(StringAnalyzer), typeof(StringFindingGenerator), typeof(StringTrendComparer), typeof(StringSectionBuilder), 230, ["memory", "string"]),
        Module("collection", "Collection Analysis", typeof(CollectionAnalyzer), typeof(CollectionFindingGenerator), typeof(CollectionTrendComparer), typeof(CollectionSectionBuilder), 240, ["collections"]),
        Module("static-root", "Static Root Leak Detection", typeof(StaticRootLeakDetector), typeof(StaticRootFindingGenerator), typeof(StaticRootTrendComparer), typeof(StaticRootSectionBuilder), 250, ["roots", "leaks"]),
        Module("reference-chain", "Reference Chain Analysis", typeof(ReferenceChainAnalyzer), typeof(ReferenceChainFindingGenerator), typeof(ReferenceChainTrendComparer), typeof(ReferenceChainSectionBuilder), 260, ["roots"]),
        Module("gc-handle", "GC Handle Analysis", typeof(GCHandleAnalyzer), typeof(GCHandleFindingGenerator), typeof(GCHandleTrendComparer), typeof(GCHandleSectionBuilder), 270, ["handles"]),
        Module("dependent-handle", "Dependent Handle Analysis", typeof(DependentHandleAnalyzer), typeof(DependentHandleFindingGenerator), typeof(DependentHandleTrendComparer), typeof(DependentHandleSectionBuilder), 280, ["handles"]),
        Module("loh-fragmentation", "LOH Fragmentation Analysis", typeof(LohFragmentationAnalyzer), typeof(LohFragmentationFindingGenerator), typeof(LohFragmentationTrendComparer), typeof(LohFragmentationSectionBuilder), 290, ["gc", "loh"]),
        Module("thread-stack-cluster", "Thread Stack Cluster Analysis", typeof(ThreadStackClusterAnalyzer), typeof(ThreadStackClusterFindingGenerator), typeof(ThreadStackClusterTrendComparer), typeof(ThreadStackClusterSectionBuilder), 300, ["threads"]),
        Module("thread", "Thread Analysis", typeof(ThreadAnalyzer), typeof(ThreadFindingGenerator), typeof(ThreadTrendComparer), typeof(ThreadSectionBuilder), 310, ["threads"]),
        Module("lock-graph", "Lock Graph Analysis", typeof(LockGraphAnalyzer), typeof(LockGraphFindingGenerator), typeof(LockGraphTrendComparer), typeof(LockGraphSectionBuilder), 320, ["threads", "locks"]),
        Module("event-leak", "Event Leak Analysis", typeof(EventLeakAnalyzer), typeof(EventLeakFindingGenerator), typeof(EventLeakTrendComparer), typeof(EventLeakSectionBuilder), 330, ["events", "leaks"]),
        Module("finalizable-object", "Finalizable Object Analysis", typeof(FinalizableObjectAnalyzer), typeof(FinalizableObjectFindingGenerator), typeof(FinalizableObjectTrendComparer), typeof(FinalizableObjectSectionBuilder), 340, ["gc"]),
        Module("async-state-machine", "Async State Machine Analysis", typeof(AsyncStateMachineAnalyzer), typeof(AsyncStateMachineFindingGenerator), typeof(AsyncStateMachineTrendComparer), typeof(AsyncStateMachineSectionBuilder), 350, ["async"]),
        Module("array", "Array Analysis", typeof(ArrayAnalyzer), typeof(ArrayFindingGenerator), typeof(ArrayTrendComparer), typeof(ArraySectionBuilder), 360, ["types"]),
        Module("appdomain", "AppDomain Analysis", typeof(AppDomainAnalyzer), typeof(AppDomainFindingGenerator), typeof(AppDomainTrendComparer), typeof(AppDomainSectionBuilder), 370, ["runtime"]),
        Module("segment-reservation", "Segment Reservation Analysis", typeof(SegmentReservationAnalyzer), typeof(SegmentReservationFindingGenerator), typeof(SegmentReservationTrendComparer), typeof(SegmentReservationSectionBuilder), 380, ["gc", "segments"]),
        Module("weak-reference", "Weak Reference Analysis", typeof(WeakReferenceAnalyzer), typeof(WeakReferenceFindingGenerator), typeof(WeakReferenceTrendComparer), typeof(WeakReferenceSectionBuilder), 390, ["gc"]),
        Module("boxing", "Boxing Analysis", typeof(BoxingAnalyzer), typeof(BoxingFindingGenerator), typeof(BoxingTrendComparer), typeof(BoxingSectionBuilder), 400, ["types", "perf"]),
        Module("jit", "JIT Analysis", typeof(JitAnalyzer), typeof(JitFindingGenerator), typeof(JitTrendComparer), typeof(JitSectionBuilder), 410, ["runtime", "perf"]),
        Module("db-connection", "DbConnection Analysis", typeof(DbConnectionAnalyzer), typeof(DbConnectionFindingGenerator), typeof(DbConnectionTrendComparer), typeof(DbConnectionSectionBuilder), 420, ["infra", "network"]),
        Module("wcf-channel", "WCF Channel Analysis", typeof(WcfChannelAnalyzer), typeof(WcfChannelFindingGenerator), typeof(WcfChannelTrendComparer), typeof(WcfChannelSectionBuilder), 430, ["infra", "network"]),
        Module("http-object", "Http Object Analysis", typeof(HttpObjectAnalyzer), typeof(HttpObjectFindingGenerator), typeof(HttpObjectTrendComparer), typeof(HttpObjectSectionBuilder), 440, ["infra", "network"]),
        Module("timer-leak", "Timer Leak Analysis", typeof(TimerLeakAnalyzer), typeof(TimerLeakFindingGenerator), typeof(TimerLeakTrendComparer), typeof(TimerLeakSectionBuilder), 450, ["infra", "timers"]),
    ];

    public IReadOnlyList<Type> GlobalReportSectionBuilderTypes { get; } =
    [
        typeof(ExecutiveSummarySectionBuilder),
        typeof(TypeSystemSectionBuilder),
        typeof(InsightsSectionBuilder),
        typeof(ConfidenceSectionBuilder)
    ];

    private static AnalyzerFeatureModule Module(
        string key,
        string displayName,
        Type analyzerType,
        Type findingGeneratorType,
        Type trendComparerType,
        Type analyzerSectionBuilderType,
        int order,
        IReadOnlyCollection<string> tags)
    {
        return new AnalyzerFeatureModule(
            Key: key,
            DisplayName: displayName,
            AnalyzerType: analyzerType,
            FindingGeneratorType: findingGeneratorType,
            TrendComparerType: trendComparerType,
            AnalyzerSectionBuilderType: analyzerSectionBuilderType,
            ReportSectionContributionTypes: [],
            Order: order,
            Tags: tags);
    }
}