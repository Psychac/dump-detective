using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Wraps one <c>Sdk.Analysis.IAnalyzer</c> so it can run, unmodified, through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline — the one bridging adapter step 2 of
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's migration calls for. Every
/// property delegates to the inner SDK analyzer; <see cref="AnalyzeAsync"/> translates the legacy
/// context (<see cref="LegacyAnalysisContextTranslator"/>), runs the inner analyzer, and reads its
/// result back out through <see cref="IProducesAnalyzerDomainResult"/>.
/// </summary>
/// <remarks>
/// A per-analyzer subclass supplies a parameterless constructor (required by
/// <c>ActivatorUtilities.CreateInstance</c> and by <c>AnalyzerBenchmarkBase&lt;T&gt;</c>'s
/// <c>where T : IAnalyzer, new()</c> constraint) and overrides <see cref="ResolveOptions"/> to pick
/// its analyzer's own option record out of the legacy 23-sub-record <c>AnalysisOptions</c> bag —
/// see <see cref="Analyzers.GCGenerationAnalyzerLegacyAdapter"/> for the pilot's instance.
///
/// Public, not internal — <c>AnalyzerBenchmarkBase&lt;T&gt;</c>'s <c>where T : IAnalyzer, new()</c>
/// constraint needs a public parameterless constructor on the concrete per-analyzer subclass, which
/// in turn needs this base (and <see cref="IProducesAnalyzerDomainResult"/>, its other generic
/// constraint) to be at least as accessible (CS0060/CS0703).
/// </remarks>
public abstract class LegacyAnalyzerAdapter<TSdkAnalyzer> : IAnalyzer
    where TSdkAnalyzer : Sdk.Analysis.IAnalyzer, IProducesAnalyzerDomainResult
{
    private readonly TSdkAnalyzer _inner;

    protected LegacyAnalyzerAdapter(TSdkAnalyzer inner) => _inner = inner;

    public string Name => _inner.Name;
    public string Category => _inner.Category;
    public IReadOnlyCollection<string> Tags => _inner.Tags;
    public int Order => _inner.Order;
    public bool IsThreadSafe => _inner.IsThreadSafe;

    /// <summary>Picks this analyzer's own options record out of the legacy options bag, or
    /// <c>null</c> if it declares none. Base default is <c>null</c> — override when the wrapped
    /// analyzer needs one.</summary>
    protected virtual object? ResolveOptions(AnalysisOptions options) => null;

    public async ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Sdk.Analysis.AnalysisContext sdkContext = LegacyAnalysisContextTranslator.Translate(context, ResolveOptions(context.AnalysisOptions));
        await _inner.AnalyzeAsync(sdkContext, cancellationToken).ConfigureAwait(false);

        AnalyzerDomainResult result = _inner.LastResult
            ?? throw new InvalidOperationException($"{_inner.Name} completed without producing a result via {nameof(IProducesAnalyzerDomainResult)}.");

        return result.Stamp(this);
    }

    public void Dispose() => _inner.Dispose();
}
