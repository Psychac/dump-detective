using DumpDetective.Cli.Output;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class WriteOutputStage(ReportOutputWriter outputWriter) : IAnalysisStage
{
    public string Name => "Write output";
    private readonly ReportOutputWriter _outputWriter = outputWriter;

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReportArtifact> artifacts = state.Runs.SelectMany(r => r.Artifacts ?? Array.Empty<ReportArtifact>()).ToList();
        await _outputWriter.WriteAsync(state.Resolved, state.ReportDocument, state.RenderedReport, artifacts, cancellationToken);
    }
}
