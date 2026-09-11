using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="DbConnectionAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c> marker directly —
/// same reasoning as <c>GCRootAnalyzerLegacyAdapter</c>'s remarks: the pipeline's Stage-A gating
/// checks this against the registered <c>Core.Abstractions.IAnalyzer</c> instances (this adapter,
/// not the inner <see cref="DbConnectionAnalyzer"/>, which the pipeline never sees directly).
/// Deliberately does <em>not</em> add <c>IRequiresDominatorTreeIndex</c> — the pre-retyping analyzer
/// never had it either, relying on another already-registered analyzer (e.g. <c>GCRootAnalyzer</c>)
/// to have requested Stage B, and degrading gracefully (no <c>RetainedBytes</c>) when none did.
/// </remarks>
public sealed class DbConnectionAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<DbConnectionAnalyzerLegacyAdapter, DbConnectionAnalyzer>, IRequiresReachableGraphIndex
{
    public DbConnectionAnalyzerLegacyAdapter() : base(new DbConnectionAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) => DbConnectionAnalyzer.IsCandidateType(typeName);
}
