using System;
using System.Collections.Generic;
using System.Linq;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class CollectionPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Collection Analysis";
        public string DisplayTitle => "Collection Analysis";
        public int SortOrder => 110;

        public bool CanHandle(AnalyzerDomainResult result) => result is CollectionDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not CollectionDomainResult domain)
                return;

            writer.WriteHeader("COLLECTION EFFICIENCY ANALYSIS:");
            writer.WriteSubHeading("COLLECTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Collections", $"{domain.TotalCollections:N0}");
            writer.WriteMetric("Dictionaries", $"{domain.Dictionaries:N0}", indentLevel: 1);
            writer.WriteMetric("Lists", $"{domain.Lists:N0}", indentLevel: 1);
            writer.WriteMetric("HashSets", $"{domain.HashSets:N0}", indentLevel: 1);
            writer.WriteMetric("Queues", $"{domain.Queues:N0}", indentLevel: 1);
            writer.WriteDetailBlank();
            writer.WriteMetric("Total Wasted Memory", FormatHelper.FormatBytes(domain.TotalWastedMemory));

            var topWasteful = domain.TopWastefulCollections ?? [];
            if (topWasteful.Count > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("MOST WASTEFUL COLLECTIONS (Top 15):");
                writer.WriteDetailText($"{"Type",-50} {"Count/Capacity",15} {"Fill Rate",10} {"Wasted",12}");
                foreach (var entry in topWasteful)
                {
                    writer.WriteDetailText($"{FormatHelper.TruncateString(entry.Type, 50),-50} {($"{entry.Count}/{entry.Capacity}"),15} {($"{entry.FillRate:F1}%"),10} {FormatHelper.FormatBytes(entry.WastedMemory),12}");
                    writer.WriteMetric("Address", $"0x{entry.Address:X}", indentLevel: 1);

                    // Developer / diagnostic fields
                    if (!string.IsNullOrEmpty(entry.ElementType) || entry.ElementSize > 0 || !string.IsNullOrEmpty(entry.SizeEstimateConfidence) || !string.IsNullOrEmpty(entry.DetectionMethod) || !string.IsNullOrEmpty(entry.RootDescription))
                    {
                        if (!string.IsNullOrEmpty(entry.ElementType))
                            writer.WriteMetric("Element type", entry.ElementType, indentLevel: 2);
                        if (entry.ElementSize > 0)
                            writer.WriteMetric("Element size", FormatHelper.FormatBytes(entry.ElementSize), indentLevel: 2);
                        if (!string.IsNullOrEmpty(entry.SizeEstimateConfidence))
                            writer.WriteMetric("Size confidence", entry.SizeEstimateConfidence, indentLevel: 2);
                        if (!string.IsNullOrEmpty(entry.DetectionMethod))
                            writer.WriteMetric("Detection", entry.DetectionMethod, indentLevel: 2);
                        if (!string.IsNullOrEmpty(entry.RootDescription))
                            writer.WriteMetric("Root hint", entry.RootDescription, indentLevel: 2);
                    }

                    // Queue diagnostics
                    if (entry.Head.HasValue || entry.Tail.HasValue || entry.LargestContiguousFreeSegmentBytes.HasValue)
                    {
                        if (entry.Head.HasValue)
                            writer.WriteMetric("Head", entry.Head.Value.ToString(), indentLevel: 2);
                        if (entry.Tail.HasValue)
                            writer.WriteMetric("Tail", entry.Tail.Value.ToString(), indentLevel: 2);
                        if (entry.FreeSegmentCount.HasValue)
                            writer.WriteMetric("Free segments", entry.FreeSegmentCount.Value.ToString(), indentLevel: 2);
                        if (entry.LargestContiguousFreeSegmentBytes.HasValue)
                            writer.WriteMetric("Largest contiguous free", FormatHelper.FormatBytes(entry.LargestContiguousFreeSegmentBytes.Value), indentLevel: 2);
                    }
                }
            }
            
            writer.WriteDetailBlank();
            writer.WriteSubHeading("WASTE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteMetric("Wasteful collections", $"{domain.WastefulCollectionCount:N0}");
            writer.WriteMetric("Estimated unused capacity", FormatHelper.FormatBytes(domain.TotalWastedMemory));

            // Print aggregated metrics if available
            if (domain.Metrics is { } metrics && metrics.Count > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("AGGREGATED METRICS:");
                writer.WriteSeparator();
                if (metrics.TryGetValue("Waste.TotalBytes", out var total))
                    writer.WriteMetric("Total wasted bytes (metrics)", FormatHelper.FormatBytes(Convert.ToUInt64(total)), indentLevel: 1);
                if (metrics.TryGetValue("Waste.AvgBytes", out var avg))
                    writer.WriteMetric("Avg wasted per collection", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(avg))), indentLevel: 1);
                if (metrics.TryGetValue("Waste.MedianBytes", out var med))
                    writer.WriteMetric("Median wasted", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(med))), indentLevel: 1);
                if (metrics.TryGetValue("Waste.P75Bytes", out var p75))
                    writer.WriteMetric("P75 wasted", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(p75))), indentLevel: 1);
                if (metrics.TryGetValue("Waste.P90Bytes", out var p90))
                    writer.WriteMetric("P90 wasted", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(p90))), indentLevel: 1);

                // Histogram
                if (metrics.TryGetValue("Waste.Histogram", out var histObj) && histObj is IReadOnlyDictionary<string, int> hist)
                {
                    writer.WriteDetailText("Histogram (overall):");
                    foreach (var kv in hist)
                        writer.WriteDetailText($"  {kv.Key,-12} : {kv.Value}");
                }

                // Per-kind percentiles
                if (metrics.TryGetValue("Waste.Histogram.ByKind", out var byKindObj) && byKindObj is IReadOnlyDictionary<string, object?> byKind)
                {
                    writer.WriteDetailBlank();
                    writer.WriteSubHeading("PER-KIND METRICS:");
                    foreach (var kv in byKind)
                    {
                        writer.WriteDetailText($"{kv.Key}:");
                        if (kv.Value is IReadOnlyDictionary<string, object?> kindMetrics)
                        {
                            if (kindMetrics.TryGetValue("Count", out var c)) writer.WriteMetric("Count", c?.ToString() ?? "0", indentLevel: 1);
                            if (kindMetrics.TryGetValue("AvgBytes", out var ka)) writer.WriteMetric("Avg", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(ka ?? 0d))), indentLevel: 1);
                            if (kindMetrics.TryGetValue("MedianBytes", out var km)) writer.WriteMetric("Median", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(km ?? 0d))), indentLevel: 1);
                            if (kindMetrics.TryGetValue("P75Bytes", out var k75)) writer.WriteMetric("P75", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(k75 ?? 0d))), indentLevel: 1);
                            if (kindMetrics.TryGetValue("P90Bytes", out var k90)) writer.WriteMetric("P90", FormatHelper.FormatBytes(Convert.ToUInt64(Convert.ToDouble(k90 ?? 0d))), indentLevel: 1);
                        }
                    }
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("CAPACITY RECOMMENDATION:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.TotalWastedMemory >= 10UL * 1024 * 1024
                ? "⚠️  Consider trimming long-lived collections or setting more accurate initial capacities."
                : "✅ Collection sizing appears acceptable for this snapshot.");
            writer.WriteDetailDivider();
        }
    }
}
