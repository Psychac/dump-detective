namespace DumpDetective.Models
{
    internal enum FindingTrendState
    {
        New,
        Persistent,
        Resolved
    }

    internal sealed record FindingTrendDelta(
        string Fingerprint,
        FindingTrendState State,
        InsightFinding? Current,
        InsightFinding? Baseline,
        bool HasMetricComparison,
        double? MetricDelta,
        double? MetricDeltaPercent,
        string? MetricUnit);

    internal sealed record TrendComparisonResult(
        IReadOnlyList<FindingTrendDelta> Deltas,
        int CurrentCriticalCount,
        int BaselineCriticalCount,
        int CurrentWarningCount,
        int BaselineWarningCount)
    {
        public IReadOnlyList<FindingTrendDelta> NewFindings =>
            Deltas.Where(d => d.State == FindingTrendState.New).ToList();

        public IReadOnlyList<FindingTrendDelta> PersistentFindings =>
            Deltas.Where(d => d.State == FindingTrendState.Persistent).ToList();

        public IReadOnlyList<FindingTrendDelta> ResolvedFindings =>
            Deltas.Where(d => d.State == FindingTrendState.Resolved).ToList();

        public IReadOnlyList<FindingTrendDelta> MetricComparedFindings =>
            Deltas.Where(d => d.HasMetricComparison).ToList();
    }

    internal sealed record TrendStepComparison(
        AnalysisSnapshot Baseline,
        AnalysisSnapshot Current,
        TrendComparisonResult Comparison);
}
