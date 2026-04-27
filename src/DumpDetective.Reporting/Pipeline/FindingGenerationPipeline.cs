using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Pipeline;

internal sealed class FindingGenerationPipeline(IEnumerable<IFindingGenerator> generators)
{
    private readonly IReadOnlyDictionary<string, IFindingGenerator> _generators =
        generators.ToDictionary(g => g.AnalyzerName, StringComparer.Ordinal);

    public IReadOnlyList<AnalyzerRunResult> Generate(IReadOnlyList<AnalyzerRunResult> runs, CancellationToken cancellationToken)
    {
        List<AnalyzerRunResult> updated = new(runs.Count);

        foreach (AnalyzerRunResult run in runs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                updated.Add(run);
                continue;
            }

            if (run.Result is null)
            {
                updated.Add(run);
                continue;
            }

            if (_generators.TryGetValue(run.AnalyzerName, out IFindingGenerator? gen) && gen.CanGenerate(run.Result))
            {
                try
                {
                    IReadOnlyList<InsightFinding> findings = gen.Generate(run.Result);
                    AnalyzerRunResult enriched = run with { Findings = findings, FindingCount = findings?.Count ?? 0 };
                    updated.Add(enriched);
                }
                catch (Exception ex)
                {
                    // Do not abort the pipeline — record the error on the run result so it is
                    // visible in both the report (Warning section) and the console summary.
                    updated.Add(run with { FindingGeneratorError = $"{ex.GetType().Name}: {ex.Message}" });
                }
            }
            else
            {
                updated.Add(run);
            }
        }

        return updated;
    }
}
