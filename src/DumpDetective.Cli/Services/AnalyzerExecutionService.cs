using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using System.Security.Cryptography;
using System.Text;

namespace DumpDetective.Cli.Services;

internal sealed class AnalyzerExecutionService
{
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
            Diagnostics = resolved.Diagnostics,
            ExecutionPolicy = resolved.ExecutionPolicy,
            Options = new Dictionary<Type, object?>
            {
                [typeof(RetentionOptions)] = resolved.MemoryLeak,
                [typeof(ReferenceChainOptions)] = resolved.ReferenceChain,
                [typeof(EventLeakOptions)] = resolved.EventLeak,
                [typeof(DiagnosticsOptions)] = resolved.Diagnostics,
                [typeof(ExecutionPolicy)] = resolved.ExecutionPolicy,
                [typeof(CrashAnalysisOptions)] = resolved.Crash,
                [typeof(AsyncTaskAnalysisOptions)] = resolved.AsyncTaskAnalysis,
                [typeof(AsyncStateMachineAnalysisOptions)] = resolved.AsyncStateMachineAnalysis,
                [typeof(ArrayAnalysisOptions)] = resolved.ArrayAnalysis,
                [typeof(BoxingAnalysisOptions)] = resolved.BoxingAnalysis,
                [typeof(CollectionAnalysisOptions)] = resolved.Collection,
                [typeof(StringAnalysisOptions)] = resolved.StringAnalysis,
                [typeof(SegmentAnalysisOptions)] = resolved.SegmentAnalysis,
                [typeof(AppDomainAnalysisOptions)] = resolved.AppDomainAnalysis,
                [typeof(AllocationPatternAnalysisOptions)] = resolved.AllocationPatternAnalysis,
                [typeof(ThreadStackClusterAnalysisOptions)] = resolved.ThreadStackClusterAnalysis,
                [typeof(LockGraphAnalysisOptions)] = resolved.LockGraphAnalysis,
                [typeof(FinalizableObjectAnalysisOptions)] = resolved.FinalizableObjectAnalysis,
                [typeof(GCGenerationAnalysisOptions)] = resolved.GCGenerationAnalysis,
                [typeof(GCRootAnalysisOptions)] = resolved.GCRootAnalysis,
                [typeof(LohFragmentationAnalysisOptions)] = resolved.LohFragmentationAnalysis,
                [typeof(SegmentReservationAnalysisOptions)] = resolved.SegmentReservationAnalysis,
                [typeof(ThreadAnalysisOptions)] = threadOptions,
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
            DiagnosticsSink = new ConsoleDiagnosticsSink(resolved.DiagnosticMode, activeAnalyzers)
        };
    }

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(
        RuntimeAnalysisContext context,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        CancellationToken cancellationToken)
    {
        AnalysisPipeline pipeline = new(activeAnalyzers);
        return await pipeline.ExecuteAsync(context, cancellationToken);
    }
}