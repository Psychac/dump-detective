using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class SegmentReservationTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Segment Reservation Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not SegmentReservationDomainResult r) return [];
            return
            [
                new("segment.committed.bytes",       null, r.TotalCommittedBytes,      "bytes",    MetricTrendDirection.HigherIsWorse),
                new("segment.reserved.bytes",        null, r.TotalReservedBytes,       "bytes",    MetricTrendDirection.HigherIsWorse),
                new("segment.reservation.gap",       null, r.ReservationGapBytes,      "bytes",    MetricTrendDirection.HigherIsWorse),
                new("segment.reserved.ratio",        null, r.ReservedToCommittedRatio, "ratio",    MetricTrendDirection.HigherIsWorse),
                new("segment.ephemeral.fill.pct",    null, r.AvgEphemeralFillPct,      "%",        MetricTrendDirection.HigherIsWorse),
                new("segment.noephemeral.soh.count", null, r.NonEphemeralSohSegmentCount, "segments", MetricTrendDirection.HigherIsWorse),
                new("segment.regions.nearempty.count", null, r.NearEmptyRegionCount, "regions", MetricTrendDirection.HigherIsWorse),
                new("segment.regions.nearempty.committed.bytes", null, r.NearEmptyRegionCommittedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not SegmentReservationDomainResult b || current is not SegmentReservationDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("segment.committed.bytes",    null, b.TotalCommittedBytes,       c.TotalCommittedBytes,       "bytes",    MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.reserved.bytes",     null, b.TotalReservedBytes,        c.TotalReservedBytes,        "bytes",    MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.reservation.gap",    null, b.ReservationGapBytes,       c.ReservationGapBytes,       "bytes",    MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.reserved.ratio",     null, b.ReservedToCommittedRatio,  c.ReservedToCommittedRatio,  "ratio",    MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.ephemeral.fill.pct", null, b.AvgEphemeralFillPct,       c.AvgEphemeralFillPct,       "%",        MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.regions.nearempty.count", null, b.NearEmptyRegionCount, c.NearEmptyRegionCount, "regions", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.regions.nearempty.committed.bytes", null, b.NearEmptyRegionCommittedBytes, c.NearEmptyRegionCommittedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


