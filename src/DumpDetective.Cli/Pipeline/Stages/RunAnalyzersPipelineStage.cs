using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Pipeline.Stages;

using PipelineAnalysisContext = DumpDetective.Analysis.Pipeline.AnalysisContext;

internal sealed class RunAnalyzersPipelineStage : IAnalysisStage
{
    public string Name => "Run analyzers";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        PipelineAnalysisContext context = BuildContext(state);

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

    private static PipelineAnalysisContext BuildContext(SingleDumpPipelineState state)
    {
        ResolvedExecutionOptions resolved = state.Resolved;
        return new PipelineAnalysisContext
        {
            Runtime = state.LoadContext!.Runtime,
            Heap = state.LoadContext.Heap,
            Cache = state.HeapCache!,
            Diagnostics = resolved.Diagnostics,
            Options = new Dictionary<string, object?>
            {
                [nameof(MemoryLeakOptions)]    = resolved.MemoryLeak,
                [nameof(ReferenceChainOptions)] = resolved.ReferenceChain,
                [nameof(EventLeakOptions)]      = resolved.EventLeak,
                [nameof(DiagnosticsOptions)]    = resolved.Diagnostics
            },
            MemoryLeakOptions      = resolved.MemoryLeak,
            ReferenceChainOptions  = resolved.ReferenceChain,
            EventLeakOptions       = resolved.EventLeak,
            DiagnosticsOptions     = resolved.Diagnostics,
            DiagnosticsSink        = new ConsoleDiagnosticsSink(resolved.DiagnosticMode, state.ActiveAnalyzers)
        };
    }
}
