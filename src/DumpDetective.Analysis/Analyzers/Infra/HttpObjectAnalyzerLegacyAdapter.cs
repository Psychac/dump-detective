using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="HttpObjectAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 wave.
/// </summary>
public sealed class HttpObjectAnalyzerLegacyAdapter :
    TypedResourceCandidateStreamingLegacyAdapter<HttpObjectAnalyzerLegacyAdapter, HttpObjectAnalyzer>
{
    public HttpObjectAnalyzerLegacyAdapter() : base(new HttpObjectAnalyzer())
    {
    }

    public override bool IsCandidateType(string typeName) => HttpObjectAnalyzer.ClassifyType(typeName) != HttpObjectAnalyzer.HttpObjectCategory.None;
}
