using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="TimerLeakAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c> marker directly —
/// same reasoning as <c>DbConnectionAnalyzerLegacyAdapter</c>'s/<c>GCRootAnalyzerLegacyAdapter</c>'s
/// remarks.
/// </remarks>
public sealed class TimerLeakAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<TimerLeakAnalyzerLegacyAdapter, TimerLeakAnalyzer>, IRequiresReachableGraphIndex
{
    public TimerLeakAnalyzerLegacyAdapter() : base(new TimerLeakAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) => TimerLeakAnalyzer.IsCandidateType(typeName);
}
