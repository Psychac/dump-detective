using DumpDetective.Analysis.Models;
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

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total domains",       d.TotalDomains.ToString("N0"),           d.TotalDomains),
            KM("Anonymous modules",   d.AnonymousModuleCount.ToString("N0"),    d.AnonymousModuleCount),
            KM("Dynamic modules",     d.TotalDynamicModules.ToString("N0"),     d.TotalDynamicModules),
        };
        if (d.DynamicModuleBytes > 0)
            keyMetrics.Add(KM("Dynamic module bytes", FormatBytes(d.DynamicModuleBytes), (double)d.DynamicModuleBytes));
        if (d.ExcludedModuleCount > 0)
            keyMetrics.Add(KM("Excluded modules", d.ExcludedModuleCount.ToString("N0"), d.ExcludedModuleCount));

        var tables = new List<SectionTable>();

        tables.Add(ST(
            "AppDomain inventory",
            ["Domain Name", "ID", "Address", "Module Count", "Estimated Managed Bytes"],
            BuildDomainRows(d.Domains)));

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
            tables.Add(ST("Top modules by type count",
                ["Module", "Assembly", "Types", "Live Types", "Objects", "Bytes"], rows));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder,
            Blocks: [],
            KeyMetrics: keyMetrics,
            Tables: tables);
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
