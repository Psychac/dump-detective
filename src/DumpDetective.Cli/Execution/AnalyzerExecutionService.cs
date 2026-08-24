using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using DumpDetective.Cli.Services;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Models;

namespace DumpDetective.Cli.Execution;

internal sealed class AnalyzerExecutionService(FindingGenerationPipeline findingGenerationPipeline)
{
    private readonly FindingGenerationPipeline _findingGenerationPipeline = findingGenerationPipeline;

    public RuntimeAnalysisContext BuildContext(
        ResolvedExecutionOptions resolved,
        DumpLoadContext loadContext,
        IHeapAnalysisCache heapCache,
        IReadOnlyList<IAnalyzer> activeAnalyzers)
    {
        return new RuntimeAnalysisContext
        {
            Runtime = loadContext.Runtime,
            Cache = heapCache,
            RuntimeFacade = new RuntimeFacade(loadContext.Runtime, loadContext.Heap),
            AnalysisOptions = new AnalysisOptions
            {
                MemoryLeak = resolved.MemoryLeak,
                ReferenceChain = resolved.ReferenceChain,
                EventLeak = resolved.EventLeak,
                Diagnostics = resolved.Diagnostics,
                ExecutionPolicy = resolved.ExecutionPolicy,
                Crash = resolved.Crash,
                AsyncStateMachineAnalysis = resolved.AsyncStateMachineAnalysis,
                ArrayAnalysis = resolved.ArrayAnalysis,
                BoxingAnalysis = resolved.BoxingAnalysis,
                Collection = resolved.Collection,
                StringAnalysis = resolved.StringAnalysis,
                AllocationPatternAnalysis = resolved.AllocationPatternAnalysis,
                ThreadStackClusterAnalysis = resolved.ThreadStackClusterAnalysis,
                GCGenerationAnalysis = resolved.GCGenerationAnalysis,
                SegmentReservationAnalysis = resolved.SegmentReservationAnalysis,
                ThreadAnalysis = resolved.ThreadAnalysis,
                HangAnalysis = resolved.HangAnalysis,
                JitAnalysis = resolved.JitAnalysis,
                WeakReferenceAnalysis = resolved.WeakReferenceAnalysis,
                ModuleAnalysis = resolved.ModuleAnalysis,
                GCHandleAnalysis = resolved.GCHandleAnalysis,
                StaticRootLeakAnalysis = resolved.StaticRootLeakAnalysis,
                MemoryAnalysis = resolved.MemoryAnalysis,
            },
            Diagnostics = resolved.Diagnostics,
            DiagnosticsSink = new ConsoleDiagnosticsSink(resolved.DiagnosticMode, activeAnalyzers)
        };
    }

    public AnalysisPipeline CreatePipeline(IReadOnlyList<IAnalyzer> activeAnalyzers) =>
        new(activeAnalyzers, _findingGenerationPipeline);

    /// <summary>
    /// Runs the shared heap-index/thread-stack scan passes for <paramref name="pipeline"/>.
    /// Callers that want this work attributed to the indexing phase rather than "running
    /// analyzers" should call this before that phase transition; safe to call again (or not at
    /// all) before <see cref="ExecuteAsync"/> — the pipeline only runs the shared scans once.
    /// </summary>
    public void RunSharedScans(AnalysisPipeline pipeline, RuntimeAnalysisContext context, CancellationToken cancellationToken) =>
        pipeline.RunSharedScans(context, cancellationToken);

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(
        AnalysisPipeline pipeline,
        RuntimeAnalysisContext context,
        CancellationToken cancellationToken)
    {
        return await pipeline.ExecuteAsync(context, cancellationToken);
    }
}
