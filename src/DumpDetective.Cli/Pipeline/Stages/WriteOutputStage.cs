using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class WriteOutputStage(ReportOutputWriter outputWriter) : IAnalysisStage
{
    public string Name => "Write output";
    private readonly ReportOutputWriter _outputWriter = outputWriter;

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        await _outputWriter.WriteAsync(state.Resolved, state.ReportDocument, state.RenderedReport, cancellationToken);
    }
}
