using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
    public int SortOrder => 1480;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<GCHandleDomainResult>() is not null
        || results.Get<WeakReferenceDomainResult>() is not null
        || results.Get<DependentHandleDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        GCHandleDomainResult? handles = results.Get<GCHandleDomainResult>();
        WeakReferenceDomainResult? weakRefs = results.Get<WeakReferenceDomainResult>();
        DependentHandleDomainResult? dependent = results.Get<DependentHandleDomainResult>();

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>();

        // ── GC Handles ────────────────────────────────────────────────────────
        if (handles is not null)
        {
            double strongPct  = handles.TotalHandles == 0 ? 0 : handles.StrongLikeHandles   * 100.0 / handles.TotalHandles;
            double weakPct    = handles.TotalHandles == 0 ? 0 : handles.WeakLikeHandles      * 100.0 / handles.TotalHandles;
            double pinnedPct  = handles.TotalHandles == 0 ? 0 : handles.PinnedHandleTargets  * 100.0 / handles.TotalHandles;

            keyMetrics.Add(KM("Total handles",        $"{handles.TotalHandles:N0}",                             handles.TotalHandles));
            keyMetrics.Add(KM("Strong-like handles",  $"{handles.StrongLikeHandles:N0} ({strongPct:F1}%)",      handles.StrongLikeHandles));
            keyMetrics.Add(KM("Weak-like handles",    $"{handles.WeakLikeHandles:N0} ({weakPct:F1}%)",          handles.WeakLikeHandles));
            keyMetrics.Add(KM("Pinned targets",       $"{handles.PinnedHandleTargets:N0} ({pinnedPct:F1}%)",    handles.PinnedHandleTargets));
            if (handles.PinnedRetainedBytes > 0)
                keyMetrics.Add(KM("Pinned retained", FormatHelper.FormatBytes(handles.PinnedRetainedBytes),     (long)handles.PinnedRetainedBytes));

            var byKind = handles.HandlesByKind ?? [];
            if (byKind.Count > 0)
            {
                var rows = new List<TableRow>(byKind.Count);
                for (int i = 0; i < byKind.Count; i++)
                {
                    double pct = handles.TotalHandles == 0 ? 0 : byKind[i].Count * 100.0 / handles.TotalHandles;
                    rows.Add(Row(Cell(byKind[i].Name), Cell($"{byKind[i].Count:N0}", byKind[i].Count), Cell($"{pct:F1}%")));
                }
                tables.Add(ST("Handle kind breakdown", ["Kind", "Count", "% Total"], rows));
            }

            if ((handles.TopPinnedTargetTypes ?? []).Count > 0)
            {
                var rows = new List<TableRow>(handles.TopPinnedTargetTypes!.Count);
                foreach (var e in handles.TopPinnedTargetTypes!)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                tables.Add(ST("Top pinned target types", ["Type", "Count"], rows));
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
                tables.Add(ST("Top pinned objects by size", ["Type", "Size", "% Pinned"], rows));
            }
        }

        // ── Weak References ───────────────────────────────────────────────────
        if (weakRefs is not null)
        {
            keyMetrics.Add(KM("Total weak handles",     $"{weakRefs.TotalWeakHandles:N0}",             weakRefs.TotalWeakHandles));
            keyMetrics.Add(KM("Alive weak targets",     $"{weakRefs.AliveWeakTargets:N0}",             weakRefs.AliveWeakTargets));
            keyMetrics.Add(KM("Dead weak targets",      $"{weakRefs.DeadWeakTargets:N0}",              weakRefs.DeadWeakTargets));
            keyMetrics.Add(KM("Dead target ratio",      $"{weakRefs.DeadTargetRatio:P1}",              weakRefs.DeadTargetRatio));
            keyMetrics.Add(KM("WeakReference objects",  $"{weakRefs.WeakReferenceObjectCount:N0}",     weakRefs.WeakReferenceObjectCount));
            keyMetrics.Add(KM("Stale wrappers",         $"{weakRefs.StaleWrapperCount:N0}",            weakRefs.StaleWrapperCount));

            if (weakRefs.ScanCapped)
                blocks.Add(T("⚠ Handle scan was capped — totals may be underestimated."));

            if (weakRefs.WeakHandleKinds.Count > 0)
            {
                var rows = new List<TableRow>(weakRefs.WeakHandleKinds.Count);
                foreach (var e in weakRefs.WeakHandleKinds.Take(15))
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                tables.Add(ST("Weak handle kinds", ["Kind", "Count"], rows));
            }

            if (weakRefs.TopWeakTargetTypes.Count > 0)
            {
                var rows = new List<TableRow>(weakRefs.TopWeakTargetTypes.Count);
                foreach (var e in weakRefs.TopWeakTargetTypes.Take(15))
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                tables.Add(ST("Top alive weak target types", ["Type", "Count"], rows));
            }

            if (weakRefs.TopStaleWrapperHolderTypes.Count > 0)
            {
                var rows = new List<TableRow>(weakRefs.TopStaleWrapperHolderTypes.Count);
                foreach (var e in weakRefs.TopStaleWrapperHolderTypes.Take(15))
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                tables.Add(ST("Stale wrapper holder types", ["Type", "Count"], rows));
            }
        }

        // ── Dependent Handles ─────────────────────────────────────────────────
        if (dependent is not null)
        {
            keyMetrics.Add(KM("Dependent handles",    $"{dependent.DependentHandleCount:N0}",          dependent.DependentHandleCount));
            keyMetrics.Add(KM("Resolved edges",       $"{dependent.ResolvedEdgeCount:N0}",             dependent.ResolvedEdgeCount));
            keyMetrics.Add(KM("Unresolved targets",   $"{dependent.UnresolvedTargetCount:N0} ({dependent.UnresolvedPercent:F1}%)", dependent.UnresolvedTargetCount));

            if ((dependent.TopSourceTypes ?? []).Count > 0)
            {
                var rows = new List<TableRow>(dependent.TopSourceTypes!.Count);
                foreach (var e in dependent.TopSourceTypes!)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                tables.Add(ST("Dependent handle source types", ["Type", "Count"], rows));
            }

            if ((dependent.TopTargetTypes ?? []).Count > 0)
            {
                var rows = new List<TableRow>(dependent.TopTargetTypes!.Count);
                foreach (var e in dependent.TopTargetTypes!)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                tables.Add(ST("Dependent handle target types", ["Type", "Count"], rows));
            }

            if (dependent.TopSourceTargetEdges is { Count: > 0 })
            {
                var rows = new List<TableRow>(dependent.TopSourceTargetEdges.Count);
                foreach (var e in dependent.TopSourceTargetEdges)
                    rows.Add(Row(Cell(e.Name), Cell($"{e.Count:N0}", e.Count)));
                tables.Add(ST("Source → target pairs", ["Pair", "Count"], rows));
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "GC Handles, Weak References & Dependent Handles",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics.Count > 0 ? keyMetrics : null,
            Tables: tables.Count > 0 ? tables : null);
    }
}
