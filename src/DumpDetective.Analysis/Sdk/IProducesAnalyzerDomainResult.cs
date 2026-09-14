using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Result-extraction contract for every <c>Sdk.Analysis.IAnalyzer</c> implementation.
/// <c>Sdk.Analysis.IAnalyzer.AnalyzeAsync</c> deliberately returns a bare <see cref="ValueTask"/> —
/// the SDK has zero dependency on <c>Core.Models.AnalyzerDomainResult</c> and cannot return it — so
/// every analyzer sets <see cref="LastResult"/> as the last step of its own <c>AnalyzeAsync</c>, and
/// <see cref="Pipeline.AnalyzerExecutionRunner"/> reads it back out after <c>AnalyzeAsync</c> completes. Once
/// Phase 5 gives analyzers a real way to report results (observations, or a resolved base-type
/// decision), this contract retires.
/// </summary>
public interface IProducesAnalyzerDomainResult
{
    AnalyzerDomainResult? LastResult { get; }
}
