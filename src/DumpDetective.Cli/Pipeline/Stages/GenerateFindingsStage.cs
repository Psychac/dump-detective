using DumpDetective.Cli.Console;
using DumpDetective.Reporting.Pipeline;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class GenerateFindingsStage(FindingGenerationPipeline findingGenerationPipeline) : IAnalysisStage
{
    public string Name => "Generate findings";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        try
        {
            state.Runs = (await findingGenerationPipeline.GenerateAsync(state.Runs, cancellationToken)).ToList();
        }
        catch (Exception ex)
        {
            // Finding generation errors must not abort the pipeline; surface as a visible warning.
            ConsoleUx.Warning($"Finding generation failed: {ex.Message}");
        }
    }
}
