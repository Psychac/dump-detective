using DumpDetective.Cli.Console;
using DumpDetective.Cli.Services;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class ExecutePerDumpPipelineStage(PerDumpExecutionService perDumpExecutionService) : IAnalysisStage
{
    public string Name => "Load + scan + analyze";
    private readonly PerDumpExecutionService _perDumpExecutionService = perDumpExecutionService;

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        PerDumpExecutionResult result = await _perDumpExecutionService.ExecuteAsync(
            "Single",
            state.Resolved,
            state.AllAnalyzers,
            state.ActiveAnalyzers,
            state.Resolved.DumpPath,
            new Progress<AnalyzerProgressReport>(r => ConsoleUx.ObjectScanProgress(Name, r.ScannedCount, r.Elapsed ?? TimeSpan.Zero, "streaming objects to index")),
            cancellationToken);

        state.HeapIndex = result.HeapIndex;
        state.Runs = result.Runs;
        state.IncidentContext = result.IncidentContext;
        state.HeapCache = result.HeapCache;
        state.HeapIndexBuilder = result.HeapCache;
        state.AnalysisElapsed = result.Elapsed;

        string indexTarget = result.HeapIndex.StorageKind == DumpDetective.Analysis.Indexing.HeapIndexStorageKind.Memory
            ? "in-memory"
            : Path.GetFileName(result.HeapIndex.IndexPath);
        ConsoleUx.ObjectScanComplete(Name, result.HeapIndex.ObjectCount, result.HeapIndex.Elapsed, indexTarget);

        if (result.HeapIndex.SatelliteWarnings is { Count: > 0 } satelliteWarnings)
        {
            foreach (string warning in satelliteWarnings)
                ConsoleUx.Warning($"Satellite index write failed — dependent analyzers may produce incomplete results: {warning}");
        }

        if (state.Resolved.DiagnosticMode)
        {
            ConsoleUx.Info($"Index built: requested={state.Resolved.IndexPrebuildMode}, selected={result.HeapIndex.StorageKind}, objects={result.HeapIndex.ObjectCount:N0}, elapsed={result.HeapIndex.Elapsed.TotalSeconds:F1}s");
        }
    }
}