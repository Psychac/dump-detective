using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal static class MetricDeltaHelper
    {
        public static MetricDelta Compute(
            string key, string? scope,
            double baseline, double current,
            string unit, MetricTrendDirection direction)
        {
            double delta = current - baseline;
            double? deltaPercent = Math.Abs(baseline) > double.Epsilon
                ? delta * 100.0 / baseline
                : null;
            return new MetricDelta(key, scope, baseline, current, delta, deltaPercent, unit, direction);
        }
    }
}


