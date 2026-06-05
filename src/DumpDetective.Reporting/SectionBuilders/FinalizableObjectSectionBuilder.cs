using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class FinalizableObjectSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypeRows = 20;
    private const int TopQueueRows = 10;

    public string AnalyzerName => "Finalizable Object Analysis";
    public string DisplayTitle => "Finalizable Objects";
    public int SortOrder => 600; // §B6 finalizable objects

    public bool CanHandle(AnalyzerDomainResult result) => result is FinalizableObjectDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (FinalizableObjectDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_finalizable_objects"] = new NumericMetricValue(d.TotalFinalizableObjects, MetricUnit.Count),
            ["total_finalizable_memory"] = new NumericMetricValue((double)d.TotalFinalizableBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalFinalizableBytes)),
            ["gen0_count"] = new NumericMetricValue(d.Gen0Count, MetricUnit.Count),
            ["gen1_count"] = new NumericMetricValue(d.Gen1Count, MetricUnit.Count),
            ["gen2_count"] = new NumericMetricValue(d.Gen2Count, MetricUnit.Count),
            ["finalizer_queue_objects"] = new NumericMetricValue(d.FinalizerQueueCount, MetricUnit.Count),
            ["finalizer_queue_retained"] = new NumericMetricValue((double)d.FinalizerQueueRetainedBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.FinalizerQueueRetainedBytes)),
            ["potential_resurrection"] = new TextMetricValue(d.PotentialResurrectionDetected ? "Yes" : "No"),
        };

        if (d.TopFinalizableTypesByGen2Count.Count > 0)
        {
            int limit = Math.Min(d.TopFinalizableTypesByGen2Count.Count, TopTypeRows);
            compactTables.Add(STCompact(
                "Top finalizable types by Gen2 object count",
                new[] { CH("Type Name"), CH("Gen 0","number"), CH("Gen 1","number"), CH("Gen 2","number"), CH("LOH","number"), CH("Total Bytes","bytes"), CH("Finalizable") },
                BuildTypeRows(d.TopFinalizableTypesByGen2Count, limit).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            if (d.TopFinalizableTypesByGen2Count.Count > limit)
                blocks.Add(T($"Showing top {limit} finalizable types by Gen2 count. {d.TopFinalizableTypesByGen2Count.Count - limit} additional type(s) omitted."));
        }

        if (d.TopQueueEntriesByRetainedSize.Count > 0)
        {
            int limit = Math.Min(d.TopQueueEntriesByRetainedSize.Count, TopQueueRows);
            compactTables.Add(STCompact(
                "Top finalizer queue entries by estimated retained size",
                new[] { CH("Address"), CH("Type Name"), CH("Shallow Size","bytes"), CH("Est. Retained","bytes"), CH("IDisposable"), CH("Disposed Field Found"), CH("Disposed") },
                BuildQueueRows(d.TopQueueEntriesByRetainedSize, limit).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            if (d.TopQueueEntriesByRetainedSize.Count > limit)
                blocks.Add(T($"Showing top {limit} finalizer queue entries. {d.TopQueueEntriesByRetainedSize.Count - limit} additional entries omitted."));
        }

        SectionLeadFinding? leadFinding = null;
        if (d.FinalizerQueueCount > 10_000)
            leadFinding = new SectionLeadFinding(
                Severity: "Critical",
                Title: $"Critical finalizer queue backlog \u2014 {d.FinalizerQueueCount:N0} objects queued",
                Summary: $"Finalizer queue holds {d.FinalizerQueueCount:N0} objects retaining ~{FormatHelper.FormatBytes(d.FinalizerQueueRetainedBytes)}. Finalizer thread may be blocked or unable to drain.",
                Recommendation: "Implement IDisposable + GC.SuppressFinalize in Dispose() to prevent queuing. Check whether the finalizer thread is blocked (see \u00A7D1 Thread Overview).",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.9,
                Caveats: []);
        else if (d.FinalizerQueueCount > 1_000)
            leadFinding = new SectionLeadFinding(
                Severity: "Warning",
                Title: $"Elevated finalizer queue \u2014 {d.FinalizerQueueCount:N0} objects pending finalization",
                Summary: $"Finalizer queue holds {d.FinalizerQueueCount:N0} objects retaining ~{FormatHelper.FormatBytes(d.FinalizerQueueRetainedBytes)}.",
                Recommendation: "Review finalizable types for IDisposable compliance and call GC.SuppressFinalize after Dispose().",
                ConfidenceSymbol: "\u25cf\u25cf\u25cf\u25cf",
                ConfidenceScore: 0.9,
                Caveats: []);

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            LeadFinding: leadFinding,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static List<TableRow> BuildTypeRows(IReadOnlyList<TypeGenerationProfile> types, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            TypeGenerationProfile t = types[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(t.TypeName, 70)),
                Cell($"{t.Gen0Count:N0}", t.Gen0Count),
                Cell($"{t.Gen1Count:N0}", t.Gen1Count),
                Cell($"{t.Gen2Count:N0}", t.Gen2Count),
                Cell($"{t.LohCount:N0}",  t.LohCount),
                Cell(t.TotalBytes > 0 ? FormatHelper.FormatBytes(t.TotalBytes) : "—", (long)Math.Min(t.TotalBytes, long.MaxValue)),
                Cell(t.IsFinalizable ? "Yes" : "No"),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildQueueRows(IReadOnlyList<FinalizerQueueEntry> entries, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            FinalizerQueueEntry e = entries[i];
            string disposedLabel = e.IsDisposableType
                ? (e.DisposedFieldFound ? (e.DisposedFieldValue ? "Yes" : "No (not disposed)") : "Unknown")
                : "N/A";
            rows.Add(new TableRow([
                Cell($"0x{e.Address:X}"),
                Cell(FormatHelper.TruncateString(e.TypeName, 70)),
                Cell(FormatHelper.FormatBytes(e.ShallowSize), (long)e.ShallowSize),
                Cell(FormatHelper.FormatBytes(e.EstimatedRetainedBytes), (long)e.EstimatedRetainedBytes),
                Cell(e.IsDisposableType ? "Yes" : "No"),
                Cell(e.IsDisposableType ? (e.DisposedFieldFound ? "Yes" : "No") : "N/A"),
                Cell(!e.IsDisposableType ? "N/A" : (!e.DisposedFieldFound ? "Unknown" : (e.DisposedFieldValue ? "Yes" : "No"))),
            ]));
        }
        return rows;
    }

    private static string GetSeverityBand(int queueCount)
    {
        if (queueCount > 10_000)
            return "Critical (> 10,000)";

        if (queueCount >= 1_000)
            return "Warning (1,000–10,000)";

        return "OK (< 1,000)";
    }
}
