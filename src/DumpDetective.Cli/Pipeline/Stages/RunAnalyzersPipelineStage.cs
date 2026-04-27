using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class RunAnalyzersPipelineStage : IAnalysisStage
{
    public string Name => "Run analyzers";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        RuntimeAnalysisContext context = BuildContext(state);

        AnalysisPipeline pipeline = new(state.ActiveAnalyzers);

        IReadOnlyList<AnalyzerRunResult> runs;
        try
        {
            runs = await pipeline.ExecuteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AnalysisPipelineException("Analysis pipeline failed unexpectedly.", ex);
        }

        if (runs.Any(r => r.Status == AnalyzerExecutionStatus.Canceled))
        {
            throw new OperationCanceledException("Analysis canceled.");
        }

        state.Runs = runs;
        state.AnalysisElapsed = state.PipelineStopwatch.Elapsed;
    }

    private static RuntimeAnalysisContext BuildContext(SingleDumpPipelineState state)
    {
        ResolvedExecutionOptions resolved = state.Resolved;
        return new RuntimeAnalysisContext
        {
            Runtime = state.LoadContext!.Runtime,
            Heap = state.LoadContext.Heap,
            Cache = state.HeapCache!,
            Diagnostics = resolved.Diagnostics,
            Options = new Dictionary<Type, object?>
            {
                [typeof(MemoryLeakOptions)]         = resolved.MemoryLeak,
                [typeof(ReferenceChainOptions)]     = resolved.ReferenceChain,
                [typeof(EventLeakOptions)]          = resolved.EventLeak,
                [typeof(DiagnosticsOptions)]        = resolved.Diagnostics,
                [typeof(CollectionAnalyzerOptions)] = resolved.Collection,
            },
            DiagnosticsSink = new ConsoleDiagnosticsSink(resolved.DiagnosticMode, state.ActiveAnalyzers)
        };
    }
}
