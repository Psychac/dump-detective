using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Runs the retyped <see cref="DominatorAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c>/
/// <c>IRequiresDominatorTreeIndex</c> markers directly — same reasoning as
/// <c>GCRootAnalyzerLegacyAdapter</c>: <c>DiskBackedObjectIndexWriter.Build</c>'s Stage B gating
/// checks the registered <c>Core.Abstractions.IAnalyzer</c> instances (this adapter, not the inner
/// <see cref="DominatorAnalyzer"/>, which the pipeline never sees directly), so dropping these here
/// would silently stop the dominator tree from being pre-built for this analyzer's benefit.
///
/// No bespoke scan participant needed — unlike the pre-retyping analyzer's own
/// <c>IParallelHeapIndexScanParticipant</c> registration (retired during Phase 1's reverse-index
/// work, per the inner analyzer's own remarks on <c>BuildLeakSignalsFromReverseIndex</c>), nothing
/// here needs data accumulated during a shared heap-index scan pass.
/// </remarks>
public sealed class DominatorAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<DominatorAnalyzer>, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
{
    public DominatorAnalyzerLegacyAdapter() : base(new DominatorAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.MemoryLeak;
}
