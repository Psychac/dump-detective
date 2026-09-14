namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Marks an analyzer that must run only after every other analyzer has completed, so it can
/// query the finished run list via <c>AnalyzerRunResultsExtensions.GetResult&lt;T&gt;</c> without
/// depending on pipeline execution order. Implementations are still ordered, filtered and
/// registered exactly like any other <see cref="Sdk.Analysis.IAnalyzer"/> — the pipeline simply
/// defers their execution to a second pass after all non-deferred analyzers have run.
/// </summary>
internal interface IDeferredAnalyzer : Sdk.Analysis.IAnalyzer
{
}
