namespace DumpDetective.Core.Abstractions;

/// <summary>
/// Marks an analyzer that must run only after every other analyzer has completed, so it can
/// query the finished run list via <c>AnalyzerRunResultsExtensions.GetResult&lt;T&gt;</c> without
/// depending on pipeline execution order. Implementations are still ordered, filtered and
/// registered exactly like any other <see cref="IAnalyzer"/> — the pipeline simply defers their
/// execution to a second pass after all non-deferred analyzers have run.
/// </summary>
public interface IDeferredAnalyzer : IAnalyzer
{
}
