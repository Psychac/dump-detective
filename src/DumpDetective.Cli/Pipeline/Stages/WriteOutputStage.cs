using DumpDetective.Cli.Console;
using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class WriteOutputStage : IAnalysisStage
{
    public string Name => "Write output";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!string.IsNullOrWhiteSpace(state.Resolved.OutputPath))
            {
                await File.WriteAllTextAsync(state.Resolved.OutputPath, state.RenderedReport, cancellationToken);
                ConsoleUx.ReportWritten(state.Resolved.OutputPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OutputWriteException("Failed while writing analysis output.", ex);
        }
    }
}
