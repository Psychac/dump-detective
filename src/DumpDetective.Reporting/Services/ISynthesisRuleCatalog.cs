using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

internal interface ISynthesisRuleCatalog
{
    IReadOnlyList<Type> RuleTypes { get; }
}

/// <summary>
/// First entry (<see cref="ArraySynthesisRule"/>) added 2026-09-18, Phase 5 step 2's first real
/// rule migration — see docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer
/// playbook. Still not a per-plugin-package aggregation like <c>AnalyzerFeatureModule</c>; a flat
/// list stays proportionate until enough rules exist to justify that shape.
/// </summary>
/// <remarks>
/// The <c>*InsightRule</c> entries (added phase 5 step 6.2, docs/refactor/modularity/
/// phase-5-step6-insight-engine-plan.md) are the first rules with no backing <c>IAnalyzer</c> at
/// all — replacing <c>InsightEngine</c>'s <c>BaselineRuleGroup</c> methods one at a time as each is
/// migrated, per that doc's tracking. They can't be resolved by <c>PluginCatalogBuilder</c>'s
/// <c>{AnalyzerName}SynthesisRule</c> naming convention (there's no analyzer to name them after), so
/// they're registered here explicitly, same as <see cref="LeakCandidateSynthesisRule"/> and every
/// other entry in this list.
/// </remarks>
internal sealed class DefaultSynthesisRuleCatalog : ISynthesisRuleCatalog
{
    public IReadOnlyList<Type> RuleTypes { get; } =
        [typeof(ArraySynthesisRule), typeof(LohFragmentationSynthesisRule), typeof(AsyncStateMachineSynthesisRule), typeof(AsyncTaskSynthesisRule), typeof(FinalizableObjectSynthesisRule), typeof(GCHandleSynthesisRule), typeof(GCRootSynthesisRule), typeof(WeakReferenceSynthesisRule), typeof(SqlConnectionPoolSynthesisRule), typeof(SqlCommandSynthesisRule), typeof(SqlTransactionSynthesisRule), typeof(DbConnectionSynthesisRule), typeof(HttpObjectSynthesisRule), typeof(TimerLeakSynthesisRule), typeof(WcfChannelSynthesisRule), typeof(DominatorSynthesisRule), typeof(EventLeakSynthesisRule), typeof(ReferenceChainSynthesisRule), typeof(StaticRootSynthesisRule), typeof(AllocationPatternSynthesisRule), typeof(GCGenerationSynthesisRule), typeof(MemorySynthesisRule), typeof(SegmentReservationSynthesisRule), typeof(CrashSynthesisRule), typeof(HeapTopologySynthesisRule), typeof(JitSynthesisRule), typeof(ModuleSynthesisRule), typeof(HangSynthesisRule), typeof(LockGraphSynthesisRule), typeof(ThreadSynthesisRule), typeof(ThreadStackClusterSynthesisRule), typeof(BoxingSynthesisRule), typeof(CollectionSynthesisRule), typeof(ObjectShapeSynthesisRule), typeof(StringSynthesisRule), typeof(LeakCandidateSynthesisRule),
        typeof(LohPressureInsightRule), typeof(LohFragmentationInsightRule), typeof(PohGrowthInsightRule), typeof(ThreadContentionInsightRule), typeof(FinalizerQueueBacklogInsightRule), typeof(PinnedHandlePressureInsightRule), typeof(ActiveCrashInsightRule), typeof(LeakSuspicionInsightRule), typeof(WastefulCollectionsInsightRule), typeof(OrphanedTaskAccumulationInsightRule),
        typeof(GCRootLargeRetentionInsightRule), typeof(AllocationPressureCrossCorrelationInsightRule), typeof(StringDuplicationRatioInsightRule), typeof(LohArrayPressureInsightRule), typeof(AsyncStateMachineFireAndForgetInsightRule), typeof(StaleWeakReferenceAccumulationInsightRule), typeof(AddressSpacePressureInsightRule), typeof(DynamicAssemblyAccumulationInsightRule), typeof(JitHeapBloatInsightRule), typeof(BoxingGCCorrelationInsightRule)];
}
