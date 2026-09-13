using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Runs the retyped <see cref="ReferenceChainAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c> marker directly —
/// same reasoning as <c>GCRootAnalyzerLegacyAdapter</c>/<c>DominatorAnalyzerLegacyAdapter</c>:
/// <c>DiskBackedObjectIndexWriter.Build</c>'s gating checks the registered
/// <c>Core.Abstractions.IAnalyzer</c> instances (this adapter, not the inner
/// <see cref="ReferenceChainAnalyzer"/>, which the pipeline never sees directly). No
/// <c>IRequiresDominatorTreeIndex</c> — unlike <c>DominatorAnalyzer</c>, this analyzer only ever
/// reads the dominator tree when one already happens to be built for another reason, it never
/// requests one for itself.
///
/// No bespoke scan participant needed — nothing here needs data accumulated during a shared
/// heap-index scan pass.
/// </remarks>
public sealed class ReferenceChainAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<ReferenceChainAnalyzer>, IRequiresReachableGraphIndex
{
    public ReferenceChainAnalyzerLegacyAdapter() : base(new ReferenceChainAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.ReferenceChain;
}
