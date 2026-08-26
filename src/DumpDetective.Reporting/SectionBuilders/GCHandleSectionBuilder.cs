using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

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
        var compactTables = new List<CompactTable>();

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
            ["dependent_handles"] = new NumericMetricValue(d.DependentHandleCount, MetricUnit.Count),
            ["dependent_resolved_edges"] = new NumericMetricValue(d.DependentResolvedEdgeCount, MetricUnit.Count),
            ["dependent_unresolved_targets"] = new NumericMetricValue(d.DependentUnresolvedTargetCount, MetricUnit.Count),
            ["dependent_unresolved_targets_pct"] = new NumericMetricValue(d.DependentUnresolvedPercent, MetricUnit.Percent),
        };
        if (d.PinnedRetainedBytes > 0)
            keyMetrics["pinned_retained_bytes"] = new NumericMetricValue((double)d.PinnedRetainedBytes, MetricUnit.Bytes);
        if (d.AsyncPinnedRetainedBytes > 0)
            keyMetrics["async_pinned_retained_bytes"] = new NumericMetricValue((double)d.AsyncPinnedRetainedBytes, MetricUnit.Bytes);

        // P2-3: Per-kind pinned bytes breakdown — surfaces existing PinnedRetainedBytes/
        // AsyncPinnedRetainedBytes/*IsExact model fields (P1-2, §9) that had no report-side table.
        if (d.PinnedRetainedBytes > 0 || d.AsyncPinnedRetainedBytes > 0)
        {
            compactTables.Add(STCompact("Pinned retained bytes by kind",
                new[] { CH("Handle Kind"), CH("Retained Bytes", "bytes"), CH("Basis") },
                new[]
                {
                    R(new object?[] { "Pinned", (long)d.PinnedRetainedBytes, d.PinnedRetainedBytesIsExact ? "Exact (dominator tree)" : "Estimated (shallow size)" }),
                    R(new object?[] { "AsyncPinned", (long)d.AsyncPinnedRetainedBytes, d.AsyncPinnedRetainedBytesIsExact ? "Exact (dominator tree)" : "Estimated (shallow size)" }),
                }));
        }

        // P2-1: SOH-pinned targets block GC compaction; LOH/POH/Frozen-pinned targets don't.
        int sohPinnedTargets = d.PinnedSohObjectCount + d.AsyncPinnedSohObjectCount;
        int nonSohPinnedTargets = d.PinnedNonSohObjectCount + d.AsyncPinnedNonSohObjectCount;
        if (sohPinnedTargets + nonSohPinnedTargets > 0)
        {
            keyMetrics["pinned_soh_targets"] = new NumericMetricValue(sohPinnedTargets, MetricUnit.Count);
            keyMetrics["pinned_non_soh_targets"] = new NumericMetricValue(nonSohPinnedTargets, MetricUnit.Count);

            compactTables.Add(STCompact("Pinned targets by heap segment",
                new[] { CH("Handle Kind"), CH("SOH (compaction barrier)", "number"), CH("Non-SOH: LOH/POH/Frozen (no compaction impact)", "number") },
                new[]
                {
                    R(new object?[] { "Pinned", d.PinnedSohObjectCount, d.PinnedNonSohObjectCount }),
                    R(new object?[] { "AsyncPinned", d.AsyncPinnedSohObjectCount, d.AsyncPinnedNonSohObjectCount }),
                }));
        }

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
            compactTables.Add(STCompact("Handles by kind", new[] { CH("Kind"), CH("Count","number"), CH("% Total", "number", "percent") }, kindRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var topTargets = d.TopTargetTypes ?? [];
        if (topTargets.Count > 0)
        {
            var ttRows = new List<TableRow>(topTargets.Count);
            for (int i = 0; i < topTargets.Count; i++)
                ttRows.Add(new TableRow([Cell(topTargets[i].Name), Cell($"{topTargets[i].Count:N0}", topTargets[i].Count)]));
            compactTables.Add(STCompact("Top handle target types", new[] { CH("Type"), CH("Count","number") }, ttRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var pinnedTypes = d.TopPinnedTargetTypes ?? [];
        if (pinnedTypes.Count > 0)
        {
            var ptRows = new List<TableRow>(pinnedTypes.Count);
            for (int i = 0; i < pinnedTypes.Count; i++)
                ptRows.Add(new TableRow([Cell(pinnedTypes[i].Name), Cell($"{pinnedTypes[i].Count:N0}", pinnedTypes[i].Count)]));
            compactTables.Add(STCompact("Pinned handle target types", new[] { CH("Type"), CH("Count","number") }, ptRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Pinned types by retained bytes", new[] { CH("Type"), CH("Retained Bytes","bytes"), CH("% Pinned", "number", "percent") }, pbRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // P2-3: AsyncPinned types by retained bytes — mirrors the Pinned table above, surfacing
        // the existing TopAsyncPinnedObjectsBySize field (P1-2) that had no report-side table.
        var asyncPinnedBySizeList = d.TopAsyncPinnedObjectsBySize ?? [];
        if (asyncPinnedBySizeList.Count > 0)
        {
            ulong totalAsyncPinned = d.AsyncPinnedRetainedBytes;
            var apbRows = new List<TableRow>(asyncPinnedBySizeList.Count);
            for (int i = 0; i < asyncPinnedBySizeList.Count; i++)
            {
                var entry = asyncPinnedBySizeList[i];
                double pct = totalAsyncPinned == 0 ? 0 : entry.Bytes * 100.0 / totalAsyncPinned;
                apbRows.Add(new TableRow([
                    Cell(entry.Name),
                    Cell(FormatHelper.FormatBytes(entry.Bytes), (long)entry.Bytes),
                    Cell($"{pct:F1}%")]));
            }
            compactTables.Add(STCompact("AsyncPinned types by retained bytes", new[] { CH("Type"), CH("Retained Bytes","bytes"), CH("% AsyncPinned", "number", "percent") }, apbRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // P2-2: RefCounted (COM interop RCW) handle concentration by target type
        if (d.RefCountedHandleCount > 0)
            keyMetrics["refcounted_handles"] = new NumericMetricValue(d.RefCountedHandleCount, MetricUnit.Count);

        var refCountedTypes = d.TopRefCountedTargetTypes ?? [];
        if (refCountedTypes.Count > 0)
        {
            var rcRows = new List<TableRow>(refCountedTypes.Count);
            for (int i = 0; i < refCountedTypes.Count; i++)
                rcRows.Add(new TableRow([Cell(refCountedTypes[i].Name), Cell($"{refCountedTypes[i].Count:N0}", refCountedTypes[i].Count)]));
            compactTables.Add(STCompact("RefCounted (COM) handle target types", new[] { CH("Type"), CH("Count","number") }, rcRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // P2-4: individual pinned/asyncpinned handle addresses, ranked by retained bytes — bridges
        // to debugger follow-up (e.g. !gcroot) without needing WinDbg to re-enumerate handles.
        var pinnedAddresses = d.TopPinnedHandleAddresses ?? [];
        if (pinnedAddresses.Count > 0)
        {
            compactTables.Add(STCompact("Top pinned handle addresses by retained bytes",
                new[] { CH("Address"), CH("Type"), CH("Retained Bytes", "bytes"), CH("Handle Kind") },
                pinnedAddresses.Select(e => R(new object?[] { $"0x{e.Address:X}", e.TypeName, (long)e.Bytes, e.HandleKind })).ToArray()));
        }

        // P3-2: WeakShort clears when the target becomes unreachable, even mid-finalization;
        // WeakLong clears only after finalization completes, so a WeakLong population
        // concentrated in Gen2/LOH can indicate a finalization backlog.
        bool hasWeakGenBreakdown = d.WeakShortGen0Count + d.WeakShortGen1Count + d.WeakShortGen2Count + d.WeakShortLohCount
            + d.WeakLongGen0Count + d.WeakLongGen1Count + d.WeakLongGen2Count + d.WeakLongLohCount > 0;
        if (hasWeakGenBreakdown)
        {
            compactTables.Add(STCompact("Weak handle targets by generation",
                new[] { CH("Handle Kind"), CH("Gen0", "number"), CH("Gen1", "number"), CH("Gen2", "number"), CH("LOH", "number") },
                new[]
                {
                    R(new object?[] { "WeakShort", d.WeakShortGen0Count, d.WeakShortGen1Count, d.WeakShortGen2Count, d.WeakShortLohCount }),
                    R(new object?[] { "WeakLong", d.WeakLongGen0Count, d.WeakLongGen1Count, d.WeakLongGen2Count, d.WeakLongLohCount }),
                }));
        }

        var dependentSourceTypes = d.DependentTopSourceTypes ?? [];
        if (dependentSourceTypes.Count > 0)
        {
            var stRows = new List<TableRow>(dependentSourceTypes.Count);
            for (int i = 0; i < dependentSourceTypes.Count; i++)
                stRows.Add(new TableRow([Cell(dependentSourceTypes[i].Name), Cell($"{dependentSourceTypes[i].Count:N0}", dependentSourceTypes[i].Count)]));
            compactTables.Add(STCompact("Dependent handle source type distribution", new[] { CH("Type"), CH("Count","number") }, stRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var dependentTargetTypes = d.DependentTopTargetTypes ?? [];
        if (dependentTargetTypes.Count > 0)
        {
            var dttRows = new List<TableRow>(dependentTargetTypes.Count);
            for (int i = 0; i < dependentTargetTypes.Count; i++)
                dttRows.Add(new TableRow([Cell(dependentTargetTypes[i].Name), Cell($"{dependentTargetTypes[i].Count:N0}", dependentTargetTypes[i].Count)]));
            compactTables.Add(STCompact("Dependent handle target type distribution", new[] { CH("Type"), CH("Count","number") }, dttRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var dependentEdges = d.DependentTopSourceTargetEdges;
        if (dependentEdges != null && dependentEdges.Count > 0)
        {
            var edgeRows = new List<TableRow>(dependentEdges.Count);
            for (int i = 0; i < dependentEdges.Count; i++)
                edgeRows.Add(new TableRow(new[] { Cell(dependentEdges[i].Name), Cell(dependentEdges[i].Count.ToString("N0"), dependentEdges[i].Count) }));
            compactTables.Add(STCompact("Dependent handle source to target pairs", new[] { CH("Pair"), CH("Count","number") }, edgeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, AnalyzerName, SortOrder, [],
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
