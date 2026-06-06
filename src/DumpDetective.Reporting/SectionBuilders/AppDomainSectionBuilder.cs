using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AppDomainSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName  => "AppDomain Analysis";
    public string DisplayTitle  => "AppDomains";
    public int SortOrder        => 120;

    public bool CanHandle(AnalyzerDomainResult result) => result is AppDomainDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (AppDomainDomainResult)result;

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_domains"] = new NumericMetricValue(d.TotalDomains, MetricUnit.Count),
            ["anonymous_modules"] = new NumericMetricValue(d.AnonymousModuleCount, MetricUnit.Count),
            ["dynamic_modules"] = new NumericMetricValue(d.TotalDynamicModules, MetricUnit.Count),
        };
        if (d.DynamicModuleBytes > 0)
            keyMetrics["dynamic_module_bytes"] = new NumericMetricValue((double)d.DynamicModuleBytes, MetricUnit.Bytes, FormatBytes(d.DynamicModuleBytes));
        if (d.ExcludedModuleCount > 0)
            keyMetrics["excluded_modules"] = new NumericMetricValue(d.ExcludedModuleCount, MetricUnit.Count);

        var compactTables = new List<CompactTable>();

        compactTables.Add(STCompact(
            "AppDomain inventory",
            new[] { CH("Domain Name"), CH("ID", "number"), CH("Address"), CH("Module Count", "number"), CH("Estimated Managed Bytes", "bytes") },
            BuildDomainRows(d.Domains).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        if (d.TopModulesByTypeCount is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopModulesByTypeCount.Count);
            for (int i = 0; i < d.TopModulesByTypeCount.Count; i++)
            {
                ModuleTypeCountEntry entry = d.TopModulesByTypeCount[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(entry.ModuleName, 55)),
                    Cell(FormatHelper.TruncateString(entry.AssemblyName, 55)),
                    Cell(entry.TypeCount.ToString("N0"),     entry.TypeCount),
                    Cell(entry.LiveTypeCount.ToString("N0"), entry.LiveTypeCount),
                    Cell(entry.ObjectCount.ToString("N0"),   entry.ObjectCount),
                    Cell(FormatBytes(entry.TotalBytes),      (long)Math.Min(entry.TotalBytes, long.MaxValue))));
            }
            compactTables.Add(STCompact("Top modules by type count",
                new[] { CH("Module"), CH("Assembly"), CH("Types", "number"), CH("Live Types", "number"), CH("Objects", "number"), CH("Bytes", "bytes") },
                rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder,
            Blocks: [],
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static List<TableRow> BuildDomainRows(IReadOnlyList<AppDomainSnapshot> domains)
    {
        var rows = new List<TableRow>(domains.Count);
        for (int i = 0; i < domains.Count; i++)
        {
            AppDomainSnapshot domain = domains[i];
            rows.Add(Row(
                Cell(FormatHelper.TruncateString(domain.Name, 60)),
                Cell(domain.DomainId.ToString("N0"),                                                    domain.DomainId),
                Cell($"0x{domain.Address:X}"),
                Cell(domain.ModuleCount.ToString("N0"),                                                 domain.ModuleCount),
                Cell(FormatBytes(domain.EstimatedManagedBytes), (long)Math.Min(domain.EstimatedManagedBytes, long.MaxValue))));
        }
        return rows;
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1) { bytes /= 1024; unitIndex++; }
        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}
