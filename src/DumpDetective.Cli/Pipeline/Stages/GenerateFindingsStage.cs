using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
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
            // Pipeline-level failure (not per-generator) — surface as a visible warning and continue.
            ConsoleUx.Warning($"Finding generation pipeline failed: {ex.Message}");
            return;
        }

        // Per-generator errors are captured in FindingGeneratorError on each run result.
        // Warn immediately so the user sees them in the console, not just buried in the report.
        foreach (AnalyzerRunResult run in state.Runs)
        {
            if (!string.IsNullOrWhiteSpace(run.FindingGeneratorError))
                ConsoleUx.Warning($"Finding generator failed for '{run.AnalyzerName}': {run.FindingGeneratorError}");
        }
    }
}
