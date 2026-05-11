using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Models;

internal sealed class AnalyzerResultSet
{
    private readonly IReadOnlyList<AnalyzerRunResult> _runs;
    private readonly Dictionary<Type, AnalyzerDomainResult?> _cache = new();

    public AnalyzerResultSet(IReadOnlyList<AnalyzerRunResult> runs)
    {
        _runs = runs;
    }

    public T? Get<T>() where T : AnalyzerDomainResult
    {
        Type type = typeof(T);
        if (_cache.TryGetValue(type, out AnalyzerDomainResult? cached))
            return (T?)cached;

        T? found = null;
        foreach (AnalyzerRunResult run in _runs)
        {
            if (run.Status == AnalyzerExecutionStatus.Success && run.Result is T typed)
            {
                found = typed;
                break;
            }
        }

        _cache[type] = found;
        return found;
    }

    public IReadOnlyList<AnalyzerRunResult> AllRuns => _runs;

    public IReadOnlyList<InsightFinding> AllFindingsSorted()
    {
        List<InsightFinding> all = [];

        foreach (AnalyzerRunResult run in _runs)
        {
            if (run.Findings is { Count: > 0 })
                all.AddRange(run.Findings);
        }

        all.Sort(static (a, b) => b.Severity.CompareTo(a.Severity));
        return all;
    }
}