using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class FinalizableObjectSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
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
            ["loh_count"] = new NumericMetricValue(d.LohCount, MetricUnit.Count),
            ["finalizer_queue_objects"] = new NumericMetricValue(d.FinalizerQueueCount, MetricUnit.Count),
            ["finalizer_queue_retained"] = new NumericMetricValue((double)d.FinalizerQueueRetainedBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.FinalizerQueueRetainedBytes)),
            ["retained_estimate_partial"] = new TextMetricValue(d.IsRetainedEstimatePartial ? "Yes (dominator tree unavailable for some entries)" : "No"),
            ["has_undisposed_disposable"] = new TextMetricValue(d.HasUndisposedDisposableInQueue ? "Yes" : "No"),
            ["queue_pressure_ratio"] = new TextMetricValue($"{d.QueuePressureRatio * 100:F1}%"),
            ["critical_finalizer_queue_objects"] = new NumericMetricValue(d.CriticalFinalizerQueueCount, MetricUnit.Count),
            ["critical_finalizer_queue_bytes"] = new NumericMetricValue((double)d.CriticalFinalizerQueueBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.CriticalFinalizerQueueBytes)),
        };

        if (d.TopFinalizableTypesByGen2Count.Count > 0)
        {
            compactTables.Add(STCompact(
                "Top finalizable types by Gen2 object count",
                new[] { CH("Type Name"), CH("Gen 0","number"), CH("Gen 1","number"), CH("Gen 2","number"), CH("LOH","number"), CH("Total Bytes","bytes"), CH("Finalizable") },
                BuildTypeRows(d.TopFinalizableTypesByGen2Count).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopQueueTypesByCount.Count > 0)
        {
            compactTables.Add(STCompact(
                "Top types in finalizer queue by object count",
                new[] { CH("Type Name"), CH("Queue Count","number") },
                BuildQueueTypeRows(d.TopQueueTypesByCount).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopCriticalFinalizerTypesByCount.Count > 0)
        {
            compactTables.Add(STCompact(
                "CriticalFinalizerObject / SafeHandle accumulation by type",
                new[] { CH("Type Name"), CH("Queue Count","number") },
                BuildQueueTypeRows(d.TopCriticalFinalizerTypesByCount).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopQueueEntriesByRetainedSize.Count > 0)
        {
            compactTables.Add(STCompact(
                "Top finalizer queue entries by estimated retained size",
                new[] { CH("Address"), CH("Type Name"), CH("Generation"), CH("Shallow Size","bytes"), CH("Est. Retained","bytes"), CH("Exact?"), CH("IDisposable"), CH("Disposed Field Found"), CH("Disposed"), CH("Critical Finalizer") },
                BuildQueueRows(d.TopQueueEntriesByRetainedSize).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        bool hasTruncatedRootPathSearch = false;
        bool hasRootPathEvidence = false;
        for (int i = 0; i < d.TopQueueEntriesByRetainedSize.Count; i++)
        {
            FinalizerQueueEntry e = d.TopQueueEntriesByRetainedSize[i];
            if (e.RootPathSearchTruncated) hasTruncatedRootPathSearch = true;
            if (e.SampleRootPath != null) hasRootPathEvidence = true;
        }

        if (hasTruncatedRootPathSearch)
            blocks.Add(T("⚠ Some root path searches were truncated by search limits. Evidence confidence may be partial."));

        if (hasRootPathEvidence)
        {
            blocks.Add(H("Retention evidence"));
            for (int i = 0; i < d.TopQueueEntriesByRetainedSize.Count; i++)
            {
                FinalizerQueueEntry e = d.TopQueueEntriesByRetainedSize[i];
                if (e.SampleRootPath is null)
                    continue;

                blocks.Add(T($"**{FormatHelper.TruncateString(e.TypeName, 70)}@0x{e.Address:X}**", 1));
                blocks.Add(T(e.SampleRootPath, 2));
                blocks.Add(Blank());
            }
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

    private static List<TableRow> BuildTypeRows(IReadOnlyList<TypeGenerationProfile> types)
    {
        var rows = new List<TableRow>(types.Count);
        for (int i = 0; i < types.Count; i++)
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

    private static List<TableRow> BuildQueueRows(IReadOnlyList<FinalizerQueueEntry> entries)
    {
        var rows = new List<TableRow>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            FinalizerQueueEntry e = entries[i];
            string disposedLabel = e.IsDisposableType
                ? (e.DisposedFieldFound ? (e.DisposedFieldValue ? "Yes" : "No (not disposed)") : "Unknown")
                : "N/A";
            rows.Add(new TableRow([
                Cell($"0x{e.Address:X}"),
                Cell(FormatHelper.TruncateString(e.TypeName, 70)),
                Cell(FormatGeneration(e.Generation)),
                Cell(FormatHelper.FormatBytes(e.ShallowSize), (long)e.ShallowSize),
                Cell(FormatHelper.FormatBytes(e.EstimatedRetainedBytes), (long)e.EstimatedRetainedBytes),
                Cell(e.RetainedBytesIsExact ? "Yes" : "No"),
                Cell(e.IsDisposableType ? "Yes" : "No"),
                Cell(e.IsDisposableType ? (e.DisposedFieldFound ? "Yes" : "No") : "N/A"),
                Cell(!e.IsDisposableType ? "N/A" : (!e.DisposedFieldFound ? "Unknown" : (e.DisposedFieldValue ? "Yes" : "No"))),
                Cell(e.IsCriticalFinalizer ? "Yes" : "No"),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildQueueTypeRows(IReadOnlyList<QueueTypeStatistic> typeStats)
    {
        var rows = new List<TableRow>(typeStats.Count);
        for (int i = 0; i < typeStats.Count; i++)
        {
            QueueTypeStatistic stat = typeStats[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(stat.TypeName, 80)),
                Cell($"{stat.QueueCount:N0}", stat.QueueCount),
            ]));
        }
        return rows;
    }

    private static string FormatGeneration(int generation) => generation switch
    {
        0 => "Gen 0",
        1 => "Gen 1",
        2 => "Gen 2",
        >= 3 => "LOH",
        _ => "Unknown",
    };

    private static string GetSeverityBand(int queueCount)
    {
        if (queueCount > 10_000)
            return "Critical (> 10,000)";

        if (queueCount >= 1_000)
            return "Warning (1,000–10,000)";

        return "OK (< 1,000)";
    }
}
