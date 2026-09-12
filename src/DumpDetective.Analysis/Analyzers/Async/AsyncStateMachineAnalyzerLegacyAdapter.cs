using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Async;

/// <summary>
/// Runs retyped <see cref="AsyncStateMachineAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 wave.
/// </summary>
public sealed class AsyncStateMachineAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<AsyncStateMachineAnalyzerLegacyAdapter, AsyncStateMachineAnalyzer>
{
    public AsyncStateMachineAnalyzerLegacyAdapter() : base(new AsyncStateMachineAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) => AsyncStateMachineNamePattern.Regex.IsMatch(typeName);

    protected override object? ResolveOptions(AnalysisOptions options) => options.AsyncStateMachineAnalysis;
}
