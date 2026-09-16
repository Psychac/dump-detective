using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Output;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Cli.Pipeline.Nodes;

/// <summary>
/// Prints the trace console summary and writes the trace's own <c>report.json</c> — the trace-side
/// analog of <see cref="BuildReportNode"/>/<see cref="WriteOutputNode"/> combined into one node,
/// since a <see cref="TraceSessionReport"/> is a single JSON document with no separate
/// render/write split the way the dump side's HTML/Markdown/Text renderer has.
/// </summary>
internal sealed class WriteTraceReportNode(string id) : IPipelineNode
{
    public string Id { get; } = id;
    public IReadOnlyCollection<string> Produces { get; } = [TracePipelineKeys.Output];
    public IReadOnlyCollection<string> Consumes { get; } = [TracePipelineKeys.Observations];

    public async ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken)
    {
        TracePipelineState state = context.Get<TracePipelineState>(TracePipelineKeys.State);

        TraceOrchestrationService.PrintSummary(state.Observations);

        var report = new TraceSessionReport(state.TracePath, DateTime.UtcNow, state.Observations);
        string resolvedOutputPath = TraceReportWriter.ResolveOutputPath(state.TracePath, state.OutputPath);
        await TraceReportWriter.WriteAsync(report, resolvedOutputPath, cancellationToken);

        context.Set(TracePipelineKeys.Output, resolvedOutputPath);
    }
}
