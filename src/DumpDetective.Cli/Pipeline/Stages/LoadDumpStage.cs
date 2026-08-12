using DumpDetective.Analysis.Dump;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Abstractions;
using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class LoadDumpStage(IDumpLoader dumpLoader) : IAnalysisStage
{
    private const int HeartbeatMs = 300;

    public string Name => "Load dump";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        Stopwatch wallClock = Stopwatch.StartNew();

        string lastPhase = "opening dump file";
        var progressLock = new object();
        var timeline = new PhaseTimeline();
        timeline.Record(lastPhase, wallClock.Elapsed);

        var progress = new Progress<AnalyzerProgressReport>(r =>
        {
            lock (progressLock)
                lastPhase = r.Phase;
            timeline.Record(r.Phase, wallClock.Elapsed);
        });

        Task<DumpLoadContext> loadTask = Task.Run(
            () => dumpLoader.LoadAsync(state.Resolved.DumpPath, cancellationToken, progress),
            cancellationToken);

        while (true)
        {
            Task done = await Task.WhenAny(loadTask, Task.Delay(HeartbeatMs, cancellationToken));
            if (done == loadTask)
                break;

            // Print any phase that finished since the last tick before rendering the live line for
            // the current one — otherwise the breakdown only ever appears once, all at once, after
            // the whole stage is done.
            ConsoleUx.PhaseBreakdown(timeline.DrainCompletedSegments());

            string phase;
            lock (progressLock)
                phase = lastPhase;
            ConsoleUx.ObjectScanProgress(Name, 0, wallClock.Elapsed, phase);
        }

        state.LoadContext = await loadTask;
        wallClock.Stop();
        ConsoleUx.PhaseBreakdown(timeline.GetRemainingSegments(wallClock.Elapsed));
    }
}
