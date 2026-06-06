using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>A4 — Retention Hotspots. Source: <see cref="RetentionDomainResult"/>.</summary>
internal sealed class RetentionSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Retention Analysis";
    public string DisplayTitle => "Retention Hotspots";
    public int SortOrder => 400;

    public bool CanHandle(AnalyzerDomainResult result) => result is RetentionDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (RetentionDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(
                !d.ReferenceCountingSkipped ? 0.85 : 0.55,
                !d.ReferenceCountingSkipped
                    ? ["Retention counts are available for the scanned subset."]
                    : ["Retention counts are approximate because reference counting was skipped or unavailable."]),
        };

        var caveats = new List<string>();
        if (d.ObjectScanCapped)   caveats.Add("Object scan was capped; retention counts may be partial.");
        if (d.ReferenceCountingSkipped) caveats.Add("Reference counting was skipped; results are estimated.");
        if (d.SkippedReferenceAddresses > 0)
            caveats.Add($"{d.SkippedReferenceAddresses:N0} reference addresses were skipped.");

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["highly_referenced_objects"] = new NumericMetricValue(d.HighlyReferencedObjectCount, MetricUnit.Count),
            ["top_retained_total"] = new NumericMetricValue((double)Math.Min(d.TopHighlyReferencedTotalBytes, long.MaxValue), MetricUnit.Bytes, FormatBytes(d.TopHighlyReferencedTotalBytes)),
            ["finalizer_queue"] = new NumericMetricValue(d.FinalizerQueueCount, MetricUnit.Count),
            ["skipped_ref_addresses"] = new NumericMetricValue(d.SkippedReferenceAddresses, MetricUnit.Count),
        };

        if (d.TopHighlyReferencedObjects is { Count: > 0 })
        {
            compactTables.Add(STCompact(
                "Highly referenced objects",
                new[] { CH("Address"), CH("Type"), CH("Size","bytes"), CH("Incoming Refs","number"), CH("Est. Retained","bytes") },
                d.TopHighlyReferencedObjects.Take(20).Select(o => R(new object?[] { $"0x{o.Address:X}", o.TypeName, FormatBytes(o.Size), o.IncomingReferences, o.EstimatedRetainedBytes > 0 ? FormatBytes(o.EstimatedRetainedBytes) : "—" })).ToArray()));
        }

        if (d.TopRetentionTypes is { Count: > 0 })
        {
            compactTables.Add(STCompact(
                "Top retention types",
                new[] { CH("Type"), CH("Objects","number"), CH("Footprint","bytes"), CH("Total Incoming Refs","number"), CH("Max Incoming Refs","number"), CH("Est. Retained","bytes"), CH("Ratio") },
                d.TopRetentionTypes.Take(20).Select(t => R(new object?[] { t.TypeName, t.ObjectCount, FormatBytes(t.TotalBytes), t.TotalIncomingReferences, t.MaxIncomingReferences, t.EstimatedRetainedBytes > 0 ? FormatBytes(t.EstimatedRetainedBytes) : "—", FormatRatio(t.EstimatedRetainedBytes, t.TotalBytes) })).ToArray()));
        }

        if (caveats.Count > 0)
            blocks.Add(T("Caveats: " + string.Join(" ", caveats)));

        return new AnalyzerDetailSection(
            AnalyzerName: "Retention Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static string FormatRatio(ulong retained, ulong shallow)
        => shallow == 0 ? "—" : $"{(double)retained / shallow:F2}x";

    private static double Ratio(ulong retained, ulong shallow)
        => shallow == 0 ? 0.0 : (double)retained / shallow;

}
