using DumpDetective.Orchestration.Graph;
using DumpDetective.Sources.NetTrace;

namespace DumpDetective.Cli.Pipeline.Nodes;

/// <summary>
/// Builds a trace's own container file and opens it for reading — the trace-side analog of
/// <see cref="BuildHeapIndexNode"/>, and the dump-side equivalent of what
/// <c>TraceAnalysisRunner.RunAsync</c> used to do procedurally before migration step 5f folded it
/// into the shared <see cref="PipelineGraph"/> so a trace artifact's nodes can sit alongside a dump
/// artifact's in one combined-session run. All the actual container/section logic lives in
/// <see cref="TraceContainer"/> (<c>DumpDetective.Sources.NetTrace</c>) — this node just owns where
/// in the graph that call happens.
/// </summary>
internal sealed class BuildTraceIndexNode(string id) : IPipelineNode
{
    public string Id { get; } = id;
    public IReadOnlyCollection<string> Produces { get; } = [TracePipelineKeys.Container];
    public IReadOnlyCollection<string> Consumes { get; } = [];

    public ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken)
    {
        TracePipelineState state = context.Get<TracePipelineState>(TracePipelineKeys.State);

        TraceContainer container = TraceContainer.Build(state.TracePath, cancellationToken);
        state.Container = container;
        context.Set(TracePipelineKeys.Container, container);

        return ValueTask.CompletedTask;
    }
}
