using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalyzerResultPostProcessor(FindingGenerationPipeline findingGenerationPipeline)
{
    private readonly FindingGenerationPipeline _findingGenerationPipeline = findingGenerationPipeline;

    public IReadOnlyList<AnalyzerRunResult> Enrich(IReadOnlyList<AnalyzerRunResult> runResults, CancellationToken cancellationToken)
    {
        try
        {
            return _findingGenerationPipeline.Generate(runResults, cancellationToken);
        }
        catch
        {
            // Best effort only. Analyzer findings are additive and report generation still proceeds.
            return runResults;
        }
    }
}