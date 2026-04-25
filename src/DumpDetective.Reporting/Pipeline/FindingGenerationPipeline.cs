using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Pipeline;

internal sealed class FindingGenerationPipeline(IEnumerable<IFindingGenerator> generators)
{
    private readonly IReadOnlyDictionary<string, IFindingGenerator> _generators =
        generators.ToDictionary(g => g.AnalyzerName, StringComparer.Ordinal);

    public Task<IReadOnlyList<AnalyzerRunResult>> GenerateAsync(IReadOnlyList<AnalyzerRunResult> runs, CancellationToken cancellationToken)
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
                catch
                {
                    // swallows errors from finding generation to avoid failing reporting; diagnostics can be emitted from caller
                    updated.Add(run);
                }
            }
            else
            {
                updated.Add(run);
            }
        }

        return Task.FromResult((IReadOnlyList<AnalyzerRunResult>)updated);
    }
}
