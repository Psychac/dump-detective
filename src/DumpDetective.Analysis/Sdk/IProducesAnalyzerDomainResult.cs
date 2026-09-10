using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Legacy-bridge-only interface, not part of the SDK. <c>Sdk.Analysis.IAnalyzer.AnalyzeAsync</c>
/// deliberately returns a bare <see cref="ValueTask"/> — the SDK has zero dependency on
/// <c>Core.Models.AnalyzerDomainResult</c> and cannot return it. <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}"/>
/// needs the built result back out to satisfy <c>Core.Abstractions.IAnalyzer</c>'s
/// <c>ValueTask&lt;AnalyzerDomainResult&gt;</c> contract, so an SDK-typed analyzer that still needs
/// to run through the legacy pipeline implements this alongside <c>Sdk.Analysis.IAnalyzer</c>,
/// setting <see cref="LastResult"/> as the last step of its own <c>AnalyzeAsync</c>. This is exactly
/// the "real answer" <c>Sdk.Analysis.IAnalyzer</c>'s own remarks left for the retyping pilot to
/// force, not guess at design time — see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. Once Phase 5 gives analyzers a
/// real way to report results (observations, or a resolved base-type decision), this bridge retires.
/// </summary>
/// <remarks>Public, not internal — it appears in <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}"/>'s
/// generic constraint list, and a public generic type cannot constrain on a less-accessible
/// type (CS0703).</remarks>
public interface IProducesAnalyzerDomainResult
{
    AnalyzerDomainResult? LastResult { get; }
}
