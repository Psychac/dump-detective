using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Cli.Console;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class BuildHeapIndexStage : IAnalysisStage
{
    public string Name => "Scan + Index heap";

    public Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        HeapAnalysisCache heapCache = new();
        IHeapIndexBuilder heapBuilder = heapCache;

        HeapIndexBuildResult heapIndex = heapBuilder.PrebuildHeapIndex(
            state.LoadContext!.Heap,
            state.Resolved.DumpPath,
            cancellationToken,
            progress: new Progress<AnalyzerProgressReport>(r =>
                ConsoleUx.ObjectScanProgress(Name, r.ScannedCount, r.Elapsed ?? TimeSpan.Zero, "streaming objects to index")),
            mode: state.Resolved.IndexPrebuildMode);

        ConsoleUx.ObjectScanComplete(Name, heapIndex.ObjectCount, heapIndex.Elapsed, Path.GetFileName(heapIndex.IndexPath));

        if (state.Resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Index built: requested={state.Resolved.IndexPrebuildMode}, selected={heapIndex.StorageKind}, objects={heapIndex.ObjectCount:N0}, elapsed={heapIndex.Elapsed.TotalSeconds:F1}s");
        }

        // Both properties point to the same HeapAnalysisCache instance, typed through their respective interfaces.
        state.HeapIndexBuilder = heapBuilder;
        state.HeapCache = heapCache;
        state.HeapIndex = heapIndex;

        return Task.CompletedTask;
    }
}

