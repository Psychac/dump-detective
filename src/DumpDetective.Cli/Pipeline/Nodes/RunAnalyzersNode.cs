using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Cli.Pipeline.Nodes;

internal sealed class RunAnalyzersNode(AnalyzerExecutionService analyzerExecutionService, ArtifactPipelineKeys keys, string id, string mode) : IPipelineNode
{
    public string Id { get; } = id;
    public IReadOnlyCollection<string> Produces { get; } = [keys.AnalyzerRuns];
    public IReadOnlyCollection<string> Consumes { get; } =
        [keys.HeapIndex, keys.AnalysisContext, keys.Pipeline];

    public async ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken)
    {
        SingleDumpPipelineState state = context.Get<SingleDumpPipelineState>(keys.State);

        // Context and pipeline are built by BuildHeapIndexNode, which also runs the shared
        // heap-index/thread-stack scan passes under its own header/timer before this node starts.
        RuntimeAnalysisContext runtimeContext = state.Context!;
        AnalysisPipeline pipeline = state.Pipeline!;

        IReadOnlyList<AnalyzerRunResult> runs;
        try
        {
            runs = await analyzerExecutionService.ExecuteAsync(pipeline, runtimeContext, cancellationToken);
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
            .ToArray();
        state.AnalysisElapsed = state.PipelineStopwatch.Elapsed;

        state.IncidentContext = IncidentContextFactory.Create(
            mode: mode,
            loadContext: state.LoadContext,
            resolved: state.Resolved,
            activeAnalyzers: state.ActiveAnalyzers,
            elapsed: state.AnalysisElapsed);

        context.Set(keys.AnalyzerRuns, state.Runs);
    }
}
