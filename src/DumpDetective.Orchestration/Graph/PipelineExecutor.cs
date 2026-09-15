using System.Diagnostics;

namespace DumpDetective.Orchestration.Graph;

/// <summary>
/// Runs a <see cref="PipelineGraph"/>'s nodes via <see cref="NodeScheduler"/>. A failed node fails
/// only its dependents by propagating the exception and stopping the run — step 1's graphs are a
/// single linear chain, so "its dependents" is simply "everything after it".
/// </summary>
public sealed class PipelineExecutor
{
    public async Task RunAsync(
        PipelineGraph graph,
        PipelineNodeContext context,
        CancellationToken cancellationToken,
        IPipelineExecutionObserver? observer = null)
    {
        IReadOnlyList<IPipelineNode> nodes = NodeScheduler.ScheduleSequential(graph);
        int total = nodes.Count;

        for (int i = 0; i < nodes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IPipelineNode node = nodes[i];

            observer?.NodeStarted(node, i, total);
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                await node.ExecuteAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                observer?.NodeFailed(node, i, total, stopwatch.Elapsed, ex);
                throw;
            }

            stopwatch.Stop();
            observer?.NodeCompleted(node, i, total, stopwatch.Elapsed);
        }
    }
}
