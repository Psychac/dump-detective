using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Dump;
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
            RuntimeFacade = new RuntimeFacade(state.LoadContext.Runtime, state.LoadContext.Heap),
            Diagnostics = resolved.Diagnostics,
            Options = new Dictionary<Type, object?>
            {
                [typeof(MemoryLeakOptions)]         = resolved.MemoryLeak,
                [typeof(ReferenceChainOptions)]     = resolved.ReferenceChain,
                [typeof(EventLeakOptions)]          = resolved.EventLeak,
                [typeof(DiagnosticsOptions)]        = resolved.Diagnostics,
                [typeof(CrashAnalysisOptions)]      = resolved.Crash,
                [typeof(AsyncTaskAnalysisOptions)] = resolved.AsyncTaskAnalysis,
                [typeof(AsyncStateMachineAnalysisOptions)] = resolved.AsyncStateMachineAnalysis,
                [typeof(ArrayAnalysisOptions)] = resolved.ArrayAnalysis,
                [typeof(BoxingAnalysisOptions)] = resolved.BoxingAnalysis,
                [typeof(CollectionAnalysisOptions)] = resolved.Collection,
                [typeof(StringAnalysisOptions)]     = resolved.StringAnalysis,
                [typeof(SegmentAnalysisOptions)]    = resolved.SegmentAnalysis,
                [typeof(AppDomainAnalysisOptions)] = resolved.AppDomainAnalysis,
                [typeof(AllocationPatternAnalysisOptions)] = resolved.AllocationPatternAnalysis,
                [typeof(ThreadStackClusterAnalysisOptions)] = resolved.ThreadStackClusterAnalysis,
                [typeof(LockGraphAnalysisOptions)] = resolved.LockGraphAnalysis,
                [typeof(FinalizableObjectAnalysisOptions)] = resolved.FinalizableObjectAnalysis,
                [typeof(GCGenerationAnalysisOptions)] = resolved.GCGenerationAnalysis,
                [typeof(GCRootAnalysisOptions)] = resolved.GCRootAnalysis,
                [typeof(LohFragmentationAnalysisOptions)] = resolved.LohFragmentationAnalysis,
                [typeof(SegmentReservationAnalysisOptions)] = resolved.SegmentReservationAnalysis,
                [typeof(ThreadAnalysisOptions)] = resolved.ThreadAnalysis,
                [typeof(HangAnalysisOptions)] = resolved.HangAnalysis,
                [typeof(JitAnalysisOptions)] = resolved.JitAnalysis,
                [typeof(WeakReferenceAnalysisOptions)] = resolved.WeakReferenceAnalysis,
                [typeof(ObjectShapeAnalysisOptions)] = resolved.ObjectShapeAnalysis,
                [typeof(ModuleAnalysisOptions)] = resolved.ModuleAnalysis,
                [typeof(DependentHandleAnalysisOptions)] = resolved.DependentHandleAnalysis,
                [typeof(GCHandleAnalysisOptions)] = resolved.GCHandleAnalysis,
                [typeof(StaticRootLeakAnalysisOptions)] = resolved.StaticRootLeakAnalysis,
                [typeof(MemoryAnalysisOptions)] = resolved.MemoryAnalysis,
            },
            DiagnosticsSink = new ConsoleDiagnosticsSink(resolved.DiagnosticMode, state.ActiveAnalyzers)
        };
    }
}
