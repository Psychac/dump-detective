using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runs the retyped <see cref="GCRootAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c>/
/// <c>IRequiresDominatorTreeIndex</c> markers directly — <c>DiskBackedObjectIndexWriter.Build</c>'s
/// Stage B gating checks <c>activeAnalyzers.Any(a => a is IRequiresDominatorTreeIndex)</c> against
/// the registered <c>Core.Abstractions.IAnalyzer</c> instances (this adapter, post-retyping — not
/// the inner <see cref="GCRootAnalyzer"/>, which the pipeline never sees directly), so dropping
/// these here would silently stop the dominator tree from being pre-built for this analyzer's
/// benefit.
/// </remarks>
public sealed class GCRootAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<GCRootAnalyzer>, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
{
    public GCRootAnalyzerLegacyAdapter() : base(new GCRootAnalyzer())
    {
    }
}
