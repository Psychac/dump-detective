using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline;

/// <summary>
/// Executes a list of <see cref="IAnalysisStage"/> objects sequentially, automatically tracking
/// stage index and total so callers never hand-roll stage counters.
/// </summary>
internal sealed class StagedPipelineRunner
{
    private static readonly Process _currentProcess = Process.GetCurrentProcess();

    public async Task RunAsync(
        IReadOnlyList<IAnalysisStage> stages,
        SingleDumpPipelineState state,
        CancellationToken cancellationToken)
    {
        bool trackMemory = state.Resolved.Diagnostics.EnableMemoryDiagnostics;
        int total = stages.Count;
        for (int i = 0; i < stages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IAnalysisStage stage = stages[i];
            Stopwatch sw = Stopwatch.StartNew();
            ConsoleUx.StageStart(i + 1, total, stage.Name);

            long wsBefore = 0, managedBefore = 0;
            if (trackMemory)
            {
                _currentProcess.Refresh();
                wsBefore = _currentProcess.WorkingSet64;
                managedBefore = GC.GetTotalMemory(false);
            }

            await stage.ExecuteAsync(state, cancellationToken);
            sw.Stop();

            if (trackMemory && !(state.HasDetailedStageMemoryStats && i == 0))
            {
                _currentProcess.Refresh();
                state.StageMemoryStats.Add((stage.Name, new AnalyzerMemoryStats(
                    WorkingSetBefore: wsBefore,
                    WorkingSetAfter: _currentProcess.WorkingSet64,
                    ManagedHeapBefore: managedBefore,
                    ManagedHeapAfter: GC.GetTotalMemory(false))));
            }

            ConsoleUx.StageComplete(i + 1, total, stage.Name, sw.Elapsed);
        }
    }
}
