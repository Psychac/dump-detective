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
                AppDomainAnalysis = resolved.AppDomainAnalysis,
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
                ObjectShapeAnalysis = resolved.ObjectShapeAnalysis,
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

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(
        RuntimeAnalysisContext context,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        CancellationToken cancellationToken)
    {
        AnalysisPipeline pipeline = new(activeAnalyzers, _findingGenerationPipeline);
        return await pipeline.ExecuteAsync(context, cancellationToken);
    }
}
