using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="WcfChannelAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 wave.
/// </summary>
public sealed class WcfChannelAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<WcfChannelAnalyzerLegacyAdapter, WcfChannelAnalyzer>
{
    public WcfChannelAnalyzerLegacyAdapter() : base(new WcfChannelAnalyzer())
    {
    }

    // A genuine (non-nested) factory type name also satisfies IsCandidateType's "Channel" token
    // match (e.g. "TcpChannelFactory" contains "Channel") -- excluded here so factory instances
    // flow only through WcfChannelAnalyzer's independent HeapTypeStatistics-based FactoryCount,
    // never into the per-object channel-state candidate stream. Mirrors the pre-retyping
    // analyzer's own OnHeapEntry priority, where a factory-MethodTable match short-circuited
    // before candidate-type classification ever ran.
    public override bool IsCandidateType(string typeName) =>
        WcfChannelAnalyzer.IsCandidateType(typeName) && !WcfChannelAnalyzer.IsFactoryType(typeName);
}
