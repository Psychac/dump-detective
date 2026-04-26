using DumpDetective.Cli.Console;
using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline;

/// <summary>
/// Executes a list of <see cref="IAnalysisStage"/> objects sequentially, automatically tracking
/// stage index and total so callers never hand-roll stage counters.
/// </summary>
internal sealed class StagedPipelineRunner
{
    public async Task RunAsync(
        IReadOnlyList<IAnalysisStage> stages,
        SingleDumpPipelineState state,
        CancellationToken cancellationToken)
    {
        int total = stages.Count;
        for (int i = 0; i < stages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IAnalysisStage stage = stages[i];
            Stopwatch sw = Stopwatch.StartNew();
            ConsoleUx.StageStart(i + 1, total, stage.Name);
            await stage.ExecuteAsync(state, cancellationToken);
            sw.Stop();
            ConsoleUx.StageComplete(i + 1, total, stage.Name, sw.Elapsed);
        }
    }
}
