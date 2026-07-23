using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class GCRootIntelligenceSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "GC Root Analysis";
    public string DisplayTitle => "GC Root Intelligence";
    public int SortOrder => 500;

    public bool CanHandle(AnalyzerDomainResult result) => result is GCRootDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var roots = (GCRootDomainResult)result;

        var (confidenceScore, capCaveats) = ConfidenceScoring.Compute(0.75,
            ConfidenceScoring.F(roots.PathSearchCapped, 0.20, $"Root path search was capped ({roots.PathSearchCappedCount:N0} path(s) truncated)."));

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            BuildConfidenceBand(confidenceScore,
                new[] { "Average retained bytes are heuristic estimates." }.Concat(capCaveats).ToArray()),
            T("Average retained bytes are heuristic estimates unless a targeted retained-size pass is available."),
        };

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_roots"] = new NumericMetricValue(roots.TotalRoots, MetricUnit.Count),
            ["path_search_capped"] = new TextMetricValue(roots.PathSearchCapped ? $"Yes ({roots.PathSearchCappedCount:N0} capped)" : "No"),
        };

        compactTables.Add(STCompact(
            "GC root kinds",
            new[] { CH("Root Kind"), CH("Count","number"), CH("Estimated Retained","bytes"), CH("% of Heap", "number", "percent") },
            BuildKindRows(roots.ByKind).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        compactTables.Add(STCompact(
            "Top GC roots by severity",
            new[] { CH("Root Kind"), CH("Root Addr"), CH("Field"), CH("Target Type"), CH("Target Addr"), CH("Est. Retained","bytes"), CH("Severity","number") },
            BuildSeverityRows(roots.TopRootsBySeverity).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        var finalizerRoots = roots.TopRootsBySeverity.Where(root => string.Equals(root.RootKind, "FinalizerQueue", StringComparison.Ordinal)).ToArray();
        if (finalizerRoots.Length > 0)
        {
            compactTables.Add(STCompact(
                "Finalizer roots",
                new[] { CH("Target Type"), CH("Field"), CH("Est. Retained","bytes"), CH("Severity","number"), CH("Root Addr") },
                finalizerRoots.Take(10).Select(root => R(new object?[] { root.TargetTypeName, root.FieldDescription ?? "—", root.EstimatedRetainedBytes, root.SeverityScore, $"0x{root.RootAddress:X}" })).ToArray()));
        }

        // ── Root paths: typed RootPathGroups slot ─────────────────────────
        var rootPathGroups = new List<RootPathGroup>();

        if (roots.RootPaths.Count > 0)
        {
            var pathGroupings = roots.RootPaths
                .GroupBy(p => p.TargetTypeName, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key);

            foreach (var group in pathGroupings)
            {
                var pathsInGroup = group.OrderBy(p => p.PathLength).Take(3).ToArray();
                bool anyGroupCapped = false;
                for (int pi = 0; pi < pathsInGroup.Length; pi++)
                    if (pathsInGroup[pi].WasCapped) { anyGroupCapped = true; break; }

                var typedPaths = new List<RootPath>(pathsInGroup.Length);
                for (int pi = 0; pi < pathsInGroup.Length; pi++)
                {
                    var p = pathsInGroup[pi];
                    typedPaths.Add(new RootPath(
                        RootKind:      p.RootKind,
                        TargetAddress: $"0x{p.TargetAddress:X8}",
                        PathLength:    p.PathLength,
                        WasCapped:     p.WasCapped,
                        Hops:          p.PathTypeNames));
                }

                rootPathGroups.Add(new RootPathGroup(
                    TargetType:      group.Key,
                    TargetTypeShort: TrimTypeName(group.Key),
                    TotalPathCount:  group.Count(),
                    AnyCapped:       anyGroupCapped,
                    Paths:           typedPaths));
            }

            if (roots.PathSearchCapped)
                blocks.Add(T($"Root path search was capped ({roots.PathSearchCappedCount:N0} path(s) truncated) — some types may have incomplete chains."));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            RootPathGroups: rootPathGroups.Count > 0 ? rootPathGroups : null);
    }

    private static List<TableRow> BuildKindRows(IReadOnlyList<RootKindSummary> kinds)
    {
        var rows = new List<TableRow>(kinds.Count);
        for (int i = 0; i < kinds.Count; i++)
        {
            RootKindSummary kind = kinds[i];
            rows.Add(Row(
                Cell(kind.Kind),
                Cell(kind.Count.ToString("N0"), kind.Count),
                Cell(FormatBytes(kind.EstimatedRetainedBytes), (long)Math.Min(kind.EstimatedRetainedBytes, long.MaxValue)),
                Cell(kind.PctOfManagedHeap.ToString("F1") + "%")));
        }

        return rows;
    }

    private static List<TableRow> BuildSeverityRows(IReadOnlyList<RootFinding> roots)
    {
        var rows = new List<TableRow>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            RootFinding root = roots[i];
            rows.Add(Row(
                Cell(root.RootKind),
                Cell($"0x{root.RootAddress:X}"),
                Cell(root.FieldDescription ?? "—"),
                Cell(root.TargetTypeName),
                Cell($"0x{root.TargetAddress:X}"),
                Cell(FormatBytes(root.EstimatedRetainedBytes), (long)Math.Min(root.EstimatedRetainedBytes, long.MaxValue)),
                Cell(root.SeverityScore.ToString("N0"), root.SeverityScore)));
        }

        return rows;
    }

    private static string TrimTypeName(string typeName)
    {
        int dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}