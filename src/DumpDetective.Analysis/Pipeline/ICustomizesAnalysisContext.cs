namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Opt-in for the handful of analyzers whose <see cref="Sdk.Analysis.AnalysisContext"/> can't be
/// built by <see cref="AnalysisContextBuilder"/>'s shared path alone — e.g. injecting a precomputed
/// query from data the shared heap/thread scan already accumulated (<c>HangAnalyzer</c>,
/// <c>LockGraphAnalyzer</c>), or reading <see cref="RuntimeAnalysisContext.CompletedRunResults"/> for
/// cross-analyzer correlation (<c>LeakCandidateAnalyzer</c>). Everyone else goes through
/// <see cref="AnalysisContextBuilder.Build"/> directly.
/// </summary>
internal interface ICustomizesAnalysisContext
{
    Sdk.Analysis.AnalysisContext BuildAnalysisContext(RuntimeAnalysisContext context);
}
