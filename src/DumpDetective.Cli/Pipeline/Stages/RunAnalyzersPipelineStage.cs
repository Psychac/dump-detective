using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class RunAnalyzersPipelineStage(AnalyzerExecutionService analyzerExecutionService) : IAnalysisStage
{
    public string Name => "Run analyzers";
    private readonly AnalyzerExecutionService _analyzerExecutionService = analyzerExecutionService;

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        RuntimeAnalysisContext context = _analyzerExecutionService.BuildContext(
            state.Resolved,
            state.LoadContext!,
            state.HeapCache!,
            state.ActiveAnalyzers);

        IReadOnlyList<AnalyzerRunResult> runs;
        try
        {
            runs = await _analyzerExecutionService.ExecuteAsync(context, state.ActiveAnalyzers, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AnalysisPipelineException("Analysis pipeline failed unexpectedly.", ex);
        }

        if (runs.Any(r => r.Status == AnalyzerExecutionStatus.SkippedByCancellation))
        {
            throw new OperationCanceledException("Analysis canceled.");
        }

        state.Runs = AnalyzerFilterService.BuildSkippedByFilterResults(state.AllAnalyzers, state.ActiveAnalyzers)
            .Concat(runs)
            .ToList();
        state.AnalysisElapsed = state.PipelineStopwatch.Elapsed;

        state.IncidentContext = IncidentContextFactory.Create(
            mode: "Single",
            loadContext: state.LoadContext,
            resolved: state.Resolved,
            activeAnalyzers: state.ActiveAnalyzers,
            elapsed: state.AnalysisElapsed);
    }
}
