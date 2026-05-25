using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var tables = new List<SectionTable>();
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

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Highly referenced objects", d.HighlyReferencedObjectCount.ToString("N0"), d.HighlyReferencedObjectCount),
            KM("Top retained total",        FormatBytes(d.TopHighlyReferencedTotalBytes), (double)Math.Min(d.TopHighlyReferencedTotalBytes, long.MaxValue)),
            KM("Finalizer queue",           d.FinalizerQueueCount.ToString("N0"),         d.FinalizerQueueCount),
            KM("Skipped ref addresses",     d.SkippedReferenceAddresses.ToString("N0"),   d.SkippedReferenceAddresses),
        };

        if (d.TopHighlyReferencedObjects is { Count: > 0 })
        {
            tables.Add(ST(
                "Highly referenced objects",
                ["Address", "Type", "Size", "Incoming Refs", "Est. Retained"],
                d.TopHighlyReferencedObjects.Take(20).Select(o => Row(
                    Cell($"0x{o.Address:X}"),
                    Cell(o.TypeName),
                    Cell(FormatBytes(o.Size), (long)Math.Min(o.Size, long.MaxValue)),
                    Cell(o.IncomingReferences.ToString("N0"), o.IncomingReferences),
                    Cell(o.EstimatedRetainedBytes > 0 ? FormatBytes(o.EstimatedRetainedBytes) : "—", (long)Math.Min(o.EstimatedRetainedBytes, long.MaxValue)))).ToList()));
        }

        if (d.TopRetentionTypes is { Count: > 0 })
        {
            var sorted = new List<RetentionTypeSnapshot>(d.TopRetentionTypes);
            sorted.Sort((a, b) => CompareRatio(b, a));

            tables.Add(ST(
                "Top retention types",
                ["Type", "Objects", "Footprint", "Total Incoming Refs", "Max Incoming Refs", "Est. Retained", "Ratio"],
                d.TopRetentionTypes.Take(20).Select(t => Row(
                    Cell(t.TypeName),
                    Cell(t.ObjectCount.ToString("N0"), t.ObjectCount),
                    Cell(FormatBytes(t.TotalBytes), (long)Math.Min(t.TotalBytes, long.MaxValue)),
                    Cell(t.TotalIncomingReferences.ToString("N0"), t.TotalIncomingReferences),
                    Cell(t.MaxIncomingReferences.ToString("N0"), (long)t.MaxIncomingReferences),
                    Cell(t.EstimatedRetainedBytes > 0 ? FormatBytes(t.EstimatedRetainedBytes) : "—", (long)Math.Min(t.EstimatedRetainedBytes, long.MaxValue)),
                    Cell(FormatRatio(t.EstimatedRetainedBytes, t.TotalBytes), (long)Math.Round(Ratio(t.EstimatedRetainedBytes, t.TotalBytes) * 1000)))).ToList()));

            tables.Add(ST(
                "Top retention types by ratio",
                ["Type", "Objects", "Footprint", "Total Incoming Refs", "Max Incoming Refs", "Est. Retained", "Ratio"],
                sorted.Take(20).Select(t => Row(
                    Cell(t.TypeName),
                    Cell(t.ObjectCount.ToString("N0"), t.ObjectCount),
                    Cell(FormatBytes(t.TotalBytes), (long)Math.Min(t.TotalBytes, long.MaxValue)),
                    Cell(t.TotalIncomingReferences.ToString("N0"), t.TotalIncomingReferences),
                    Cell(t.MaxIncomingReferences.ToString("N0"), (long)t.MaxIncomingReferences),
                    Cell(t.EstimatedRetainedBytes > 0 ? FormatBytes(t.EstimatedRetainedBytes) : "—", (long)Math.Min(t.EstimatedRetainedBytes, long.MaxValue)),
                    Cell(FormatRatio(t.EstimatedRetainedBytes, t.TotalBytes), (long)Math.Round(Ratio(t.EstimatedRetainedBytes, t.TotalBytes) * 1000)))).ToList()));
        }

        if (caveats.Count > 0)
            blocks.Add(T("Caveats: " + string.Join(" ", caveats)));

        return new AnalyzerDetailSection(
            AnalyzerName: "Retention Analysis",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static string FormatRatio(ulong retained, ulong shallow)
        => shallow == 0 ? "—" : $"{(double)retained / shallow:F2}x";

    private static double Ratio(ulong retained, ulong shallow)
        => shallow == 0 ? 0.0 : (double)retained / shallow;

    private static int CompareRatio(RetentionTypeSnapshot a, RetentionTypeSnapshot b)
    {
        double ra = Ratio(a.EstimatedRetainedBytes, a.TotalBytes);
        double rb = Ratio(b.EstimatedRetainedBytes, b.TotalBytes);
        int cmp = rb.CompareTo(ra);
        return cmp != 0 ? cmp : b.TotalBytes.CompareTo(a.TotalBytes);
    }
}
