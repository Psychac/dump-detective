namespace DumpDetective.Orchestration.Graph;

/// <summary>
/// Optional callbacks the executor fires around each node so hosts can render progress and
/// capture per-node diagnostics without the orchestration engine knowing about consoles or
/// process/GC counters.
/// </summary>
public interface IPipelineExecutionObserver
{
    void NodeStarted(IPipelineNode node, int index, int total);
    void NodeCompleted(IPipelineNode node, int index, int total, TimeSpan elapsed);
    void NodeFailed(IPipelineNode node, int index, int total, TimeSpan elapsed, Exception exception);
}
