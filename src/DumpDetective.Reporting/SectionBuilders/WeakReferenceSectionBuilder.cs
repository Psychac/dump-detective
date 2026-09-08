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
            ["stale_wrappers_m_handle_0"] = new NumericMetricValue(d.StaleWrapperCount, MetricUnit.Count,
                d.StaleWrapperCountIsExact ? $"{d.StaleWrapperCount:N0}" : $"{d.StaleWrapperCount:N0} (estimated)"),
            ["dependent_handles_dead_primary_key"] = new NumericMetricValue(d.DependentHandleDeadKeyCount, MetricUnit.Count),
        };

        if (d.HeldOnlyViaWeakReferenceDetectionAvailable)
            keyMetrics["held_only_via_weak_reference"] = new NumericMetricValue(d.HeldOnlyViaWeakReferenceCount, MetricUnit.Count);

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

        if (d.WeakHandleKindLiveness is { Count: > 0 } kindLiveness)
        {
            var rows = new List<TableRow>(kindLiveness.Count);
            foreach (HandleKindLivenessEntry e in kindLiveness)
            {
                double deadRatio = e.Total == 0 ? 0.0 : (double)e.Dead / e.Total;
                rows.Add(new TableRow([
                    Cell(e.Kind),
                    Cell($"{e.Alive:N0}", e.Alive),
                    Cell($"{e.Dead:N0}", e.Dead),
                    Cell($"{e.Total:N0}", e.Total),
                    Cell($"{deadRatio:P1}", deadRatio)]));
            }
            compactTables.Add(STCompact("Weak handle kind alive/dead breakdown",
                new[] { CH("Kind"), CH("Alive","number"), CH("Dead","number"), CH("Total","number"), CH("Dead %","number") },
                rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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

        if (!d.StaleWrapperCountIsExact && d.StaleWrapperCount > 0)
            blocks.Add(T("⚠ Stale wrapper count (estimated): the disk-backed object index was unavailable for this run, so the count above is extrapolated from one sampled instance per WeakReference<T> type rather than a full scan — it can be up to 100% off per type group."));

        if (d.DependentDeadKeyValueTypes is { Count: > 0 } deadKeyValueTypes)
        {
            var rows = new List<TableRow>(deadKeyValueTypes.Count);
            foreach (NameCountEntry e in deadKeyValueTypes)
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            compactTables.Add(STCompact("Dependent dead-key value types", new[] { CH("Value type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            if (d.DependentDeadKeyValueTypesUnresolvedCount > 0)
                blocks.Add(T($"ℹ {d.DependentDeadKeyValueTypesUnresolvedCount:N0} dead-key dependent handle(s) had an unresolvable or already-collected value object and are not reflected in the value-type breakdown above."));
        }

        if (d.AliveWeakTargetGenerationDistribution is { Count: > 0 } genDistribution)
        {
            var rows = new List<TableRow>(genDistribution.Count);
            foreach (NameCountEntry e in genDistribution)
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            compactTables.Add(STCompact("Alive weak target GC generation distribution", new[] { CH("Generation"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            blocks.Add(T("Dead weak targets are excluded: their handle address is already cleared or may point at memory since reused by an unrelated object, so a generation can't be attributed reliably."));

            if (d.AliveWeakTargetGenerationUnresolvedCount > 0)
                blocks.Add(T($"ℹ {d.AliveWeakTargetGenerationUnresolvedCount:N0} alive weak target(s) resolved to an object but their GC segment/generation could not be determined."));
        }

        if (d.HeldOnlyViaWeakReferenceDetectionAvailable && d.HeldOnlyViaWeakReferenceCount > 0)
        {
            blocks.Add(T($"{d.HeldOnlyViaWeakReferenceCount:N0} alive weak target(s) are unreachable from any GC root — the weak handle is currently the only known reference to them, and they will be collected on the next GC."));

            if (d.HeldOnlyViaWeakReferenceTopTypes is { Count: > 0 } heldOnlyTypes)
            {
                var rows = new List<TableRow>(heldOnlyTypes.Count);
                foreach (NameCountEntry e in heldOnlyTypes)
                    rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
                compactTables.Add(STCompact("Held only via weak reference — top types", new[] { CH("Type"), CH("Count","number") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
            }
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
