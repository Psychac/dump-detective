using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using DumpDetective.Orchestration.Graph;

using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline;

/// <summary>
/// Reproduces <c>StagedPipelineRunner</c>'s console/memory instrumentation as an
/// <see cref="IPipelineExecutionObserver"/> so the generic <c>PipelineExecutor</c> (which must not
/// know about consoles or process/GC counters) can drive the same stage-start/stage-complete
/// output and per-stage <see cref="AnalyzerMemoryStats"/> recording as before.
/// </summary>
internal sealed class ConsoleMemoryPipelineObserver(SingleDumpPipelineState state) : IPipelineExecutionObserver
{
    private static readonly Process _currentProcess = Process.GetCurrentProcess();

    private readonly bool _trackMemory = state.Resolved.Diagnostics.EnableMemoryDiagnostics;
    private long _wsBefore;
    private long _managedBefore;
    private long _allocatedBefore;

    public void NodeStarted(IPipelineNode node, int index, int total)
    {
        ConsoleUx.StageStart(index + 1, total, node.Id);

        if (!_trackMemory)
            return;

        _currentProcess.Refresh();
        _wsBefore = _currentProcess.WorkingSet64;
        _managedBefore = GC.GetTotalMemory(false);
        _allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
    }

    public void NodeCompleted(IPipelineNode node, int index, int total, TimeSpan elapsed)
    {
        if (_trackMemory && !(state.HasDetailedStageMemoryStats && index == 0))
        {
            _currentProcess.Refresh();
            state.StageMemoryStats.Add((node.Id, new AnalyzerMemoryStats(
                WorkingSetBefore: _wsBefore,
                WorkingSetAfter: _currentProcess.WorkingSet64,
                ManagedHeapBefore: _managedBefore,
                ManagedHeapAfter: GC.GetTotalMemory(false),
                AllocatedBefore: _allocatedBefore,
                AllocatedAfter: GC.GetTotalAllocatedBytes(precise: false))));
        }

        ConsoleUx.StageComplete(index + 1, total, node.Id, elapsed);
    }

    public void NodeFailed(IPipelineNode node, int index, int total, TimeSpan elapsed, Exception exception)
    {
        // No dedicated failure rendering existed in StagedPipelineRunner either — the exception
        // propagates to the caller, which reports it. Nothing to do here.
    }
}
