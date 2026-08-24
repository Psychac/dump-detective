using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class WeakReferenceSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Weak Reference Analysis";
    public string DisplayTitle => "Weak References";
    public int SortOrder => 720;

    public bool CanHandle(AnalyzerDomainResult result) => result is WeakReferenceDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (WeakReferenceDomainResult)result;
        var blocks = new List<SectionBlock>();
        var compactTables = new List<CompactTable>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_weak_handles"] = new NumericMetricValue(d.TotalWeakHandles, MetricUnit.Count),
            ["alive_targets"] = new NumericMetricValue(d.AliveWeakTargets, MetricUnit.Count),
            ["dead_targets"] = new NumericMetricValue(d.DeadWeakTargets, MetricUnit.Count),
            ["dead_target_ratio"] = new NumericMetricValue(d.DeadTargetRatio, MetricUnit.Percent, $"{d.DeadTargetRatio:P1}"),
            ["weakreference_objects"] = new NumericMetricValue(d.WeakReferenceObjectCount, MetricUnit.Count),
            ["weakreference_object_bytes"] = new NumericMetricValue((double)d.WeakReferenceObjectBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.WeakReferenceObjectBytes)),
            ["stale_wrappers_m_handle_0"] = new NumericMetricValue(d.StaleWrapperCount, MetricUnit.Count),
            ["dependent_handles_dead_primary_key"] = new NumericMetricValue(d.DependentHandleDeadKeyCount, MetricUnit.Count),
        };

        if (d.PhaseBSkipped)
            blocks.Add(T("⚠ Phase B (WeakReference Analysis) was skipped — heap object enumeration unavailable. No WeakReference object data available."));
        else if (d.PhaseBFallbackUsed)
            blocks.Add(T("ℹ Phase B (WeakReference Analysis) used fallback heap scan (heap index unavailable). Results are accurate but computation may be slower on large heaps."));

        if (d.WeakHandleKinds.Count > 0)
        {
            var rows = new List<TableRow>(d.WeakHandleKinds.Count);
            foreach (NameCountEntry e in d.WeakHandleKinds)
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            compactTables.Add(STCompact("Weak handle kinds", new[] { CH("Kind"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopWeakTargetTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopWeakTargetTypes.Count);
            foreach (NameCountEntry e in d.TopWeakTargetTypes)
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            compactTables.Add(STCompact("Top alive weak target types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopStaleWrapperHolderTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopStaleWrapperHolderTypes.Count);
            foreach (NameCountEntry e in d.TopStaleWrapperHolderTypes)
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            compactTables.Add(STCompact("Top stale wrapper holder types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        // Typed Artifacts slot
        var artifacts = new List<AnalyzerArtifact>();
        foreach (var a in d.Artifacts ?? [])
        {
            string instructions = a.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? "Pretty JSON — open in VS Code or any JSON viewer."
                : a.FileName.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase)
                    ? $"NDJSON + gzip (streamable). Inspect with: gzip -cd {a.FileName} | jq -C '.' or open in 7-Zip/VS Code after extraction."
                    : "Analyzer export file.";
            artifacts.Add(new AnalyzerArtifact(a.FileName, instructions));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, "Weak Reference Analysis", SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            Artifacts: artifacts.Count > 0 ? artifacts : null);
    }
}
