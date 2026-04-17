using DumpDetective.Core.Models;
using System.Diagnostics;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalysisPipeline(IEnumerable<IAnalyzer> analyzers)
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers = analyzers
        .OrderBy(a => a.Order)
        .ThenBy(a => a.Name, StringComparer.Ordinal)
        .ToList();

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        List<AnalyzerRunResult> runResults = new(_analyzers.Count);

        foreach (IAnalyzer analyzer in _analyzers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                runResults.Add(new AnalyzerRunResult(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Skipped,
                    TimeSpan.Zero,
                    null,
                    "Skipped because cancellation was requested before analyzer start.",
                    nameof(OperationCanceledException)));
                continue;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            context.DiagnosticsSink.AnalyzerStarted(analyzer.Name, analyzer.Category);

            try
            {
                AnalyzerDomainResult analyzerResult = await analyzer.AnalyzeAsync(context, cancellationToken);

                stopwatch.Stop();
                context.DiagnosticsSink.AnalyzerCompleted(analyzer.Name, analyzer.Category, stopwatch.Elapsed, analyzerResult.Metrics);

                runResults.Add(new AnalyzerRunResult(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Success,
                    stopwatch.Elapsed,
                    analyzerResult,
                    null,
                    null));
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                context.DiagnosticsSink.AnalyzerCanceled(analyzer.Name, analyzer.Category, stopwatch.Elapsed);
                runResults.Add(new AnalyzerRunResult(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Canceled,
                    stopwatch.Elapsed,
                    null,
                    "Analyzer execution canceled.",
                    nameof(OperationCanceledException)));
                break;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                context.DiagnosticsSink.AnalyzerFailed(analyzer.Name, analyzer.Category, stopwatch.Elapsed, ex.GetType().Name, ex.Message);

                runResults.Add(new AnalyzerRunResult(
                    analyzer.Name,
                    AnalyzerExecutionStatus.Failed,
                    stopwatch.Elapsed,
                    null,
                    ex.Message,
                    ex.GetType().Name));

                if (!context.DiagnosticsOptions.ContinueOnAnalyzerFailure)
                {
                    break;
                }
            }
        }

        return runResults;
    }
}


