using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCHandleSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "GC Handle Analysis";
    public string DisplayTitle => "GC Handles";
    public int SortOrder => 710;

    public bool CanHandle(AnalyzerDomainResult result) => result is GCHandleDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (GCHandleDomainResult)result;
        var tables = new List<SectionTable>();

        double strongPct = d.TotalHandles == 0 ? 0 : d.StrongLikeHandles * 100.0 / d.TotalHandles;
        double weakPct = d.TotalHandles == 0 ? 0 : d.WeakLikeHandles * 100.0 / d.TotalHandles;
        double pinnedPct = d.TotalHandles == 0 ? 0 : d.PinnedHandleTargets * 100.0 / d.TotalHandles;

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_handles"] = new NumericMetricValue(d.TotalHandles, MetricUnit.Count),
            ["strong_like_handles"] = new NumericMetricValue(d.StrongLikeHandles, MetricUnit.Count),
            ["strong_like_handles_pct"] = new NumericMetricValue(strongPct, MetricUnit.Percent),
            ["weak_like_handles"] = new NumericMetricValue(d.WeakLikeHandles, MetricUnit.Count),
            ["weak_like_handles_pct"] = new NumericMetricValue(weakPct, MetricUnit.Percent),
            ["pinned_handle_targets"] = new NumericMetricValue(d.PinnedHandleTargets, MetricUnit.Count),
            ["pinned_handle_targets_pct"] = new NumericMetricValue(pinnedPct, MetricUnit.Percent),
        };
        if (d.PinnedRetainedBytes > 0)
            keyMetrics["pinned_retained_bytes"] = new NumericMetricValue((double)d.PinnedRetainedBytes, MetricUnit.Bytes);

        var byKind = d.HandlesByKind ?? [];
        if (byKind.Count > 0)
        {
            var kindRows = new List<TableRow>(byKind.Count);
            for (int i = 0; i < byKind.Count; i++)
            {
                var entry = byKind[i];
                double pct = d.TotalHandles == 0 ? 0 : entry.Count * 100.0 / d.TotalHandles;
                kindRows.Add(new TableRow([
                    Cell(entry.Name),
                    Cell($"{entry.Count:N0}", entry.Count),
                    Cell($"{pct:F1}%")]));
            }
            tables.Add(ST("Handles by kind", ["Kind", "Count", "% Total"], kindRows));
        }

        var topTargets = d.TopTargetTypes ?? [];
        if (topTargets.Count > 0)
        {
            var ttRows = new List<TableRow>(topTargets.Count);
            for (int i = 0; i < topTargets.Count; i++)
                ttRows.Add(new TableRow([Cell(topTargets[i].Name), Cell($"{topTargets[i].Count:N0}", topTargets[i].Count)]));
            tables.Add(ST("Top handle target types", ["Type", "Count"], ttRows));
        }

        var pinnedTypes = d.TopPinnedTargetTypes ?? [];
        if (pinnedTypes.Count > 0)
        {
            var ptRows = new List<TableRow>(pinnedTypes.Count);
            for (int i = 0; i < pinnedTypes.Count; i++)
                ptRows.Add(new TableRow([Cell(pinnedTypes[i].Name), Cell($"{pinnedTypes[i].Count:N0}", pinnedTypes[i].Count)]));
            tables.Add(ST("Pinned handle target types", ["Type", "Count"], ptRows));
        }

        var pinnedBySizeList = d.TopPinnedObjectsBySize ?? [];
        if (pinnedBySizeList.Count > 0)
        {
            ulong totalPinned = d.PinnedRetainedBytes;
            var pbRows = new List<TableRow>(pinnedBySizeList.Count);
            for (int i = 0; i < pinnedBySizeList.Count; i++)
            {
                var entry = pinnedBySizeList[i];
                double pct = totalPinned == 0 ? 0 : entry.Bytes * 100.0 / totalPinned;
                pbRows.Add(new TableRow([
                    Cell(entry.Name),
                    Cell(FormatHelper.FormatBytes(entry.Bytes), (long)entry.Bytes),
                    Cell($"{pct:F1}%")]));
            }
            tables.Add(ST("Pinned types by retained bytes", ["Type", "Retained Bytes", "% Pinned"], pbRows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, AnalyzerName, SortOrder, [],
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
