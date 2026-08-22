using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using System.Security.Cryptography;
using System.Text;
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
        // TODO: need to evaluate the need for this.
        // The idea is that if the user doesn't specify a sampling seed,
        // we derive one from the dump path to ensure deterministic behavior across runs on
        // the same dump.
        ThreadAnalysisOptions? threadOptions = resolved.ThreadAnalysis;
        if (threadOptions != null && threadOptions.SamplingSeed == 0)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(loadContext.DumpPath ?? string.Empty);
            byte[] hash = SHA256.HashData(pathBytes);
            int derived = BitConverter.ToInt32(hash, 0);
            threadOptions = new ThreadAnalysisOptions
            {
                MaxFramesForThreadScan = threadOptions.MaxFramesForThreadScan,
                MaxStackRootsToCount = threadOptions.MaxStackRootsToCount,
                MaxThreadsToCaptureSnapshots = threadOptions.MaxThreadsToCaptureSnapshots,
                IncludeStackSamples = threadOptions.IncludeStackSamples,
                MaxSampledStackSnapshots = threadOptions.MaxSampledStackSnapshots,
                AsyncChainDetection = threadOptions.AsyncChainDetection,
                DetectWaitPatterns = threadOptions.DetectWaitPatterns,
                MaxTopHotspots = threadOptions.MaxTopHotspots,
                SamplingSeed = derived
            };
        }

        if (threadOptions != null)
            threadOptions = ThreadAnalysisOptions.AdaptForSize(threadOptions, heapCache.SizeTier);

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
                AsyncTaskAnalysis = resolved.AsyncTaskAnalysis,
                AsyncStateMachineAnalysis = resolved.AsyncStateMachineAnalysis,
                ArrayAnalysis = resolved.ArrayAnalysis,
                BoxingAnalysis = resolved.BoxingAnalysis,
                Collection = resolved.Collection,
                StringAnalysis = resolved.StringAnalysis,
                HeapTopology = resolved.HeapTopology,
                AllocationPatternAnalysis = resolved.AllocationPatternAnalysis,
                ThreadStackClusterAnalysis = resolved.ThreadStackClusterAnalysis,
                LockGraphAnalysis = resolved.LockGraphAnalysis,
                FinalizableObjectAnalysis = resolved.FinalizableObjectAnalysis,
                GCGenerationAnalysis = resolved.GCGenerationAnalysis,
                GCRootAnalysis = resolved.GCRootAnalysis,
                LohFragmentationAnalysis = resolved.LohFragmentationAnalysis,
                SegmentReservationAnalysis = resolved.SegmentReservationAnalysis,
                ThreadAnalysis = threadOptions ?? resolved.ThreadAnalysis,
                HangAnalysis = resolved.HangAnalysis,
                JitAnalysis = resolved.JitAnalysis,
                WeakReferenceAnalysis = resolved.WeakReferenceAnalysis,
                ModuleAnalysis = resolved.ModuleAnalysis,
                DependentHandleAnalysis = resolved.DependentHandleAnalysis,
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
