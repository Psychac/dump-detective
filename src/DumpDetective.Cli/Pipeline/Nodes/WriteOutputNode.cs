using DumpDetective.Cli.Output;
using DumpDetective.Core.Models;
using DumpDetective.Orchestration.Graph;

namespace DumpDetective.Cli.Pipeline.Nodes;

internal sealed class WriteOutputNode(ReportOutputWriter outputWriter) : IPipelineNode
{
    public string Id => "Write output";
    public IReadOnlyCollection<string> Produces { get; } = [SingleDumpPipelineKeys.Output];
    public IReadOnlyCollection<string> Consumes { get; } = [SingleDumpPipelineKeys.ReportDocument];

    public async ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken)
    {
        SingleDumpPipelineState state = context.Get<SingleDumpPipelineState>(SingleDumpPipelineKeys.State);

        IReadOnlyList<ReportArtifact> artifacts = state.Runs.SelectMany(r => r.Artifacts ?? Array.Empty<ReportArtifact>()).ToArray();
        await outputWriter.WriteAsync(state.Resolved, state.ReportDocument, artifacts, cancellationToken);

        context.Set(SingleDumpPipelineKeys.Output, artifacts);
    }
}
