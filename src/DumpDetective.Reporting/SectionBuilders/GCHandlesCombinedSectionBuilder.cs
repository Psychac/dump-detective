using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

/// <summary>
/// B7 — GC Handles, Weak References &amp; Dependent Handles.
/// Sources: <see cref="GCHandleDomainResult"/>, <see cref="WeakReferenceDomainResult"/>,
/// <see cref="DependentHandleDomainResult"/>.
/// </summary>
internal sealed class GCHandlesCombinedSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public IReadOnlyList<string> SourceAnalyzers => ["GCHandleAnalyzer", "WeakReferenceAnalyzer", "DependentHandleAnalyzer"];

    public string SectionId => "B7";
    public string DisplayTitle => "GC Handles, Weak References & Dependent Handles";
    public int SortOrder => 700;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<GCHandleDomainResult>() is not null
        || results.Get<WeakReferenceDomainResult>() is not null
        || results.Get<DependentHandleDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        GCHandleDomainResult? handles = results.Get<GCHandleDomainResult>();
        WeakReferenceDomainResult? weakRefs = results.Get<WeakReferenceDomainResult>();
        DependentHandleDomainResult? dependent = results.Get<DependentHandleDomainResult>();

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>();

        // ── GC Handles ────────────────────────────────────────────────────────
        if (handles is not null)
        {
            double strongPct  = handles.TotalHandles == 0 ? 0 : handles.StrongLikeHandles   * 100.0 / handles.TotalHandles;
            double weakPct    = handles.TotalHandles == 0 ? 0 : handles.WeakLikeHandles      * 100.0 / handles.TotalHandles;
            double pinnedPct  = handles.TotalHandles == 0 ? 0 : handles.PinnedHandleTargets  * 100.0 / handles.TotalHandles;

            keyMetrics["strong_like_handles"] = new NumericMetricValue(handles.StrongLikeHandles, MetricUnit.Count);
            keyMetrics["strong_like_handles_pct"] = new NumericMetricValue(strongPct, MetricUnit.Percent);
            keyMetrics["weak_like_handles"] = new NumericMetricValue(handles.WeakLikeHandles, MetricUnit.Count);
            keyMetrics["weak_like_handles_pct"] = new NumericMetricValue(weakPct, MetricUnit.Percent);
            keyMetrics["pinned_targets"] = new NumericMetricValue(handles.PinnedHandleTargets, MetricUnit.Count);
            keyMetrics["pinned_targets_pct"] = new NumericMetricValue(pinnedPct, MetricUnit.Percent);
            if (handles.PinnedRetainedBytes > 0)
            {
                keyMetrics["pinned_retained"] = new NumericMetricValue((double)handles.PinnedRetainedBytes, MetricUnit.Bytes);
            }

            var byKind = handles.HandlesByKind ?? [];
            if (byKind.Count > 0)
            {
                var rows = new List<TableRow>(byKind.Count);
                for (int i = 0; i < byKind.Count; i++)
                {
                    double pct = handles.TotalHandles == 0 ? 0 : byKind[i].Count * 100.0 / handles.TotalHandles;
                    rows.Add(Row(Cell(byKind[i].Name), Cell($"{byKind[i].Count:N0}", byKind[i].Count), Cell($"{pct:F1}%")));
                }
                compactTables.Add(STCompact("Handle kind breakdown", new[] { CH("Kind"), CH("Count","number"), CH("% Total", "number", "percent") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            if ((handles.TopPinnedTargetTypes ?? []).Count > 0)
            {
                var rows = new List<TableRow>(handles.TopPinnedTargetTypes!.Count);
                foreach (var e in handles.TopPinnedTargetTypes!)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                compactTables.Add(STCompact("Top pinned target types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            if ((handles.TopPinnedObjectsBySize ?? []).Count > 0)
            {
                ulong total = handles.PinnedRetainedBytes;
                var rows = new List<TableRow>(handles.TopPinnedObjectsBySize!.Count);
                foreach (var e in handles.TopPinnedObjectsBySize!)
                {
                    double pct = total == 0 ? 0 : e.Bytes * 100.0 / total;
                    rows.Add(Row(Cell(e.Name), Cell(FormatHelper.FormatBytes(e.Bytes), (long)e.Bytes), Cell($"{pct:F1}%")));
                }
                compactTables.Add(STCompact("Top pinned objects by size", new[] { CH("Type"), CH("Size","bytes"), CH("% Pinned", "number", "percent") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }
        }

        // ── Weak References ───────────────────────────────────────────────────
        if (weakRefs is not null)
        {
            keyMetrics["total_weak_handles"] = new NumericMetricValue(weakRefs.TotalWeakHandles, MetricUnit.Count);
            keyMetrics["alive_weak_targets"] = new NumericMetricValue(weakRefs.AliveWeakTargets, MetricUnit.Count);
            keyMetrics["dead_weak_targets"] = new NumericMetricValue(weakRefs.DeadWeakTargets, MetricUnit.Count);
            keyMetrics["dead_target_ratio"] = new NumericMetricValue(weakRefs.DeadTargetRatio, MetricUnit.Percent);
            keyMetrics["weakreference_objects"] = new NumericMetricValue(weakRefs.WeakReferenceObjectCount, MetricUnit.Count);
            keyMetrics["stale_wrappers"] = new NumericMetricValue(weakRefs.StaleWrapperCount, MetricUnit.Count);

            if (weakRefs.ScanCapped)
                blocks.Add(T("⚠ Handle scan was capped — totals may be underestimated."));

            if (weakRefs.WeakHandleKinds.Count > 0)
            {
                var rows = new List<TableRow>(weakRefs.WeakHandleKinds.Count);
                foreach (var e in weakRefs.WeakHandleKinds.Take(15))
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                compactTables.Add(STCompact("Weak handle kinds", new[] { CH("Kind"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            if (weakRefs.TopWeakTargetTypes.Count > 0)
            {
                var rows = new List<TableRow>(weakRefs.TopWeakTargetTypes.Count);
                foreach (var e in weakRefs.TopWeakTargetTypes.Take(15))
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                compactTables.Add(STCompact("Top alive weak target types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            if (weakRefs.TopStaleWrapperHolderTypes.Count > 0)
            {
                var rows = new List<TableRow>(weakRefs.TopStaleWrapperHolderTypes.Count);
                foreach (var e in weakRefs.TopStaleWrapperHolderTypes.Take(15))
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                compactTables.Add(STCompact("Stale wrapper holder types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }
        }

        // ── Dependent Handles ─────────────────────────────────────────────────
        if (dependent is not null)
        {
            keyMetrics["unresolved_targets"] = new NumericMetricValue(dependent.UnresolvedTargetCount, MetricUnit.Count);
            keyMetrics["unresolved_targets_pct"] = new NumericMetricValue(dependent.UnresolvedPercent, MetricUnit.Percent);

            if ((dependent.TopSourceTypes ?? []).Count > 0)
            {
                var rows = new List<TableRow>(dependent.TopSourceTypes!.Count);
                foreach (var e in dependent.TopSourceTypes!)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                compactTables.Add(STCompact("Dependent handle source types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            if ((dependent.TopTargetTypes ?? []).Count > 0)
            {
                var rows = new List<TableRow>(dependent.TopTargetTypes!.Count);
                foreach (var e in dependent.TopTargetTypes!)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                compactTables.Add(STCompact("Dependent handle target types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }

            if (dependent.TopSourceTargetEdges is { Count: > 0 })
            {
                var rows = new List<TableRow>(dependent.TopSourceTargetEdges.Count);
                foreach (var e in dependent.TopSourceTargetEdges)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                compactTables.Add(STCompact("Source → target pairs", new[] { CH("Pair"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "GC Handles, Weak References & Dependent Handles",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
