using DumpDetective.Reporting.SynthesisRules;
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
internal sealed class DefaultSynthesisRuleCatalog : ISynthesisRuleCatalog
{
    public IReadOnlyList<Type> RuleTypes { get; } =
        [typeof(ArraySynthesisRule), typeof(LohFragmentationSynthesisRule), typeof(AsyncStateMachineSynthesisRule), typeof(AsyncTaskSynthesisRule), typeof(FinalizableObjectSynthesisRule), typeof(GCHandleSynthesisRule), typeof(GCRootSynthesisRule), typeof(WeakReferenceSynthesisRule), typeof(SqlConnectionPoolSynthesisRule), typeof(SqlCommandSynthesisRule), typeof(SqlTransactionSynthesisRule), typeof(DbConnectionSynthesisRule), typeof(HttpObjectSynthesisRule), typeof(TimerLeakSynthesisRule), typeof(WcfChannelSynthesisRule), typeof(DominatorSynthesisRule), typeof(EventLeakSynthesisRule), typeof(ReferenceChainSynthesisRule), typeof(StaticRootSynthesisRule), typeof(AllocationPatternSynthesisRule), typeof(GCGenerationSynthesisRule), typeof(MemorySynthesisRule), typeof(SegmentReservationSynthesisRule), typeof(CrashSynthesisRule), typeof(HeapTopologySynthesisRule)];
}
