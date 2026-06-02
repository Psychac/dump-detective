namespace DumpDetective.Reporting.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;

internal static class CorrelationBuilder
{
    // Heuristic co-occurrence builder: find snapshots with findings across multiple analyzers
    public static IReadOnlyList<CorrelationEventRecord> BuildFrom(TrendReportData trendData, int cap = 10)
    {
        if (trendData is null)
            return Array.Empty<CorrelationEventRecord>();

        const double MinConfidenceThreshold = 0.6; // filter low-confidence findings
        const double TimelinePctThreshold = 20.0; // percent change considered a boost

        var events = new List<CorrelationEventRecord>();

        // Helper to find analyzer timeline
        static AnalyzerMetricTimeline? FindTimelineFor(IReadOnlyList<AnalyzerMetricTimeline> timelines, string analyzer)
        {
            if (timelines is null) return null;
            for (int i = 0; i < timelines.Count; i++)
            {
                if (string.Equals(timelines[i].AnalyzerName, analyzer, StringComparison.OrdinalIgnoreCase))
                    return timelines[i];
            }
            return null;
        }

        for (int si = 0; si < trendData.Snapshots.Count; si++)
        {
            var snapshot = trendData.Snapshots[si];

            // Candidate findings: regression-tagged OR warning/critical, and not too-low confidence
            var candidates = snapshot.Findings
                .Where(f => (f.Tags?.Contains("regression") == true || f.Severity == FindingSeverity.Warning || f.Severity == FindingSeverity.Critical)
                            && (f.ConfidenceScore ?? f.EffectiveConfidenceScore) >= MinConfidenceThreshold)
                .ToArray();

            var distinctAnalyzers = candidates.Select(f => f.Analyzer).Distinct(StringComparer.Ordinal).ToArray();
            if (distinctAnalyzers.Length < 2)
                continue;

            // Include adjacent snapshots if coupling persists
            var snapshotIndices = new HashSet<int>();
            snapshotIndices.Add(snapshot.Index);
            for (int adj = -1; adj <= 1; adj += 2)
            {
                int idx = si + adj;
                if (idx < 0 || idx >= trendData.Snapshots.Count) continue;
                var adjSnap = trendData.Snapshots[idx];
                var adjCandidates = adjSnap.Findings
                    .Where(f => (f.Tags?.Contains("regression") == true || f.Severity == FindingSeverity.Warning || f.Severity == FindingSeverity.Critical)
                                && (f.ConfidenceScore ?? f.EffectiveConfidenceScore) >= MinConfidenceThreshold)
                    .Select(f => f.Analyzer)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                int shared = distinctAnalyzers.Intersect(adjCandidates, StringComparer.Ordinal).Count();
                if (shared >= 2)
                    snapshotIndices.Add(adjSnap.Index);
            }

            var domains = distinctAnalyzers;
            var signalKeys = candidates.Select(f => f.Category).Distinct(StringComparer.Ordinal).ToArray();
            var sourceFingerprints = candidates.Select(f => f.EffectiveFingerprint).Distinct(StringComparer.Ordinal).ToArray();

            double avgConfidence = candidates.Select(f => f.EffectiveConfidenceScore).DefaultIfEmpty(0.5).Average();

            // Severity weighting: Critical=1.2, Warning=1.0, default=0.9
            double avgSeverityWeight = candidates.Select(f => f.Severity == FindingSeverity.Critical ? 1.2 : (f.Severity == FindingSeverity.Warning ? 1.0 : 0.9)).DefaultIfEmpty(1.0).Average();

            // Timeline boost: check trendData.Timeline for large per-analyzer deltas at this snapshot
            int boostCount = 0;
            foreach (var analyzer in domains)
            {
                var tl = FindTimelineFor(trendData.Timeline, analyzer);
                if (tl is null) continue;

                for (int p = 0; p < tl.Points.Count; p++)
                {
                    var point = tl.Points[p];
                    if (point.Values == null) continue;
                    if (snapshot.Index < 1 || snapshot.Index >= point.Values.Count) continue;
                    double prev = point.Values[snapshot.Index - 1];
                    double cur = point.Values[snapshot.Index];
                    if (double.IsNaN(prev) || double.IsNaN(cur)) continue;
                    double denom = Math.Abs(prev) < 1e-9 ? 1.0 : Math.Abs(prev);
                    double pct = Math.Abs((cur - prev) / denom) * 100.0;
                    if (pct >= TimelinePctThreshold)
                    {
                        boostCount++;
                        break;
                    }
                }
            }

            double boostFactor = 1.0 + 0.25 * boostCount; // each boost adds 25% to score
            double domainFactor = 1.0 + 0.20 * Math.Max(0, domains.Length - 1);

            double computed = avgConfidence * avgSeverityWeight * boostFactor * domainFactor;
            double finalConfidence = Math.Min(1.0, Math.Round(computed, 3));

            string rationale = "Co-occurring regressions/warnings across analyzers" + (boostCount > 0 ? "; temporal metric deltas observed" : ".");

            var evt = new CorrelationEventRecord(
                EventId: Guid.NewGuid().ToString("D"),
                EventType: "CoOccurrence",
                Title: $"Cross-domain coupling @ snapshot {snapshot.Index}",
                Rationale: rationale,
                Confidence: finalConfidence,
                Domains: domains,
                SnapshotIndices: snapshotIndices.OrderBy(i => i).ToArray(),
                SignalKeys: signalKeys,
                SourceFingerprints: sourceFingerprints,
                PrimarySnapshotIndex: snapshot.Index
            );

            events.Add(evt);
        }

        var ordered = events
            .OrderByDescending(e => e.Confidence)
            .ThenBy(e => e.PrimarySnapshotIndex ?? int.MaxValue)
            .Take(cap)
            .ToArray();

        return ordered;
    }
}
