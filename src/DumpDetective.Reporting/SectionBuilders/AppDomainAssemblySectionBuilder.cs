using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AppDomainAssemblySectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public IReadOnlyList<string> SourceAnalyzers => ["AppDomainAnalyzer", "ModuleAnalyzer"];

    public string SectionId => "G1";
    public string DisplayTitle => "Modules & Assemblies";
    public int SortOrder => 1800;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<AppDomainDomainResult>() is not null
        || results.Get<ModuleDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        AppDomainDomainResult? domains = results.Get<AppDomainDomainResult>();
        ModuleDomainResult? modules = results.Get<ModuleDomainResult>();

        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();
        var keyMetrics = new List<SectionKeyMetric>();

        if (domains is not null)
        {
            keyMetrics.Add(KM("Total domains",    domains.TotalDomains.ToString("N0"),          domains.TotalDomains));
            keyMetrics.Add(KM("Anonymous modules",domains.AnonymousModuleCount.ToString("N0"),   domains.AnonymousModuleCount));
            keyMetrics.Add(KM("Dynamic modules",  domains.TotalDynamicModules.ToString("N0"),    domains.TotalDynamicModules));
            tables.Add(ST(
                "AppDomain inventory",
                ["Domain Name", "ID", "Address", "Module Count", "Estimated Managed Bytes"],
                BuildDomainRows(domains.Domains)));
        }

        if (modules is not null)
        {
            keyMetrics.Add(KM("Conflict groups",     modules.VersionConflictGroups.ToString("N0"), modules.VersionConflictGroups));
            keyMetrics.Add(KM("Dynamic module count",modules.DynamicModules.ToString("N0"),         modules.DynamicModules));

            blocks.Add(T(modules.ConflictingAssemblyNames.Count > 0
                ? $"Conflict groups include: {string.Join(", ", modules.ConflictingAssemblyNames.Take(6))}."
                : "No version conflict groups were reported."));

            if (modules.TopModulesBySize is { Count: > 0 })
            {
                var rows = new List<TableRow>(modules.TopModulesBySize.Count);
                for (int i = 0; i < modules.TopModulesBySize.Count; i++)
                {
                    LoadedModuleSnapshot m = modules.TopModulesBySize[i];
                    rows.Add(Row(
                        Cell(FormatHelper.TruncateString(m.Name, 55)),
                        Cell(FormatHelper.TruncateString(m.AssemblyName, 55)),
                        Cell(FormatBytes(m.Size), (long)Math.Min(m.Size, long.MaxValue)),
                        Cell(m.IsDynamic ? "Dynamic" : (m.IsPEFile ? "PE" : "Other"))));
                }
                tables.Add(ST("Top modules by size", ["Name", "Assembly", "Size", "Kind"], rows));
            }

            if (modules.ConflictDetails.Count > 0)
            {
                var rows = new List<TableRow>(modules.ConflictDetails.Count);
                for (int i = 0; i < modules.ConflictDetails.Count; i++)
                {
                    ModuleConflictGroup conflict = modules.ConflictDetails[i];
                    rows.Add(Row(
                        Cell(FormatHelper.TruncateString(conflict.ModuleName, 60)),
                        Cell(conflict.Instances.Count.ToString("N0"), conflict.Instances.Count),
                        Cell(string.Join("; ", conflict.Instances.Take(3).Select(m => m.AssemblyName)))));
                }
                tables.Add(ST("Conflict details", ["Module", "Instances", "Assemblies"], rows));
            }

            if (modules.TopModulesByHeapMemory is { Count: > 0 })
            {
                var rows = new List<TableRow>(modules.TopModulesByHeapMemory.Count);
                for (int i = 0; i < modules.TopModulesByHeapMemory.Count; i++)
                {
                    ModuleHeapStats stats = modules.TopModulesByHeapMemory[i];
                    double objectsPerType = stats.UniqueTypeCount > 0 ? stats.ObjectCount / (double)stats.UniqueTypeCount : 0.0;
                    rows.Add(Row(
                        Cell(FormatHelper.TruncateString(stats.ModuleName, 55)),
                        Cell(FormatHelper.TruncateString(stats.AssemblyName, 55)),
                        Cell(stats.UniqueTypeCount.ToString("N0"), stats.UniqueTypeCount),
                        Cell(stats.ObjectCount.ToString("N0"), stats.ObjectCount),
                        Cell(FormatBytes(stats.TotalBytes), (long)Math.Min(stats.TotalBytes, long.MaxValue)),
                        Cell(objectsPerType.ToString("F1"))));
                }
                tables.Add(ST("Modules by heap footprint",
                    ["Module", "Assembly", "Types", "Objects", "Bytes", "Objects/Type"], rows));
            }

            if (modules.HeavyTypeDensityModules is { Count: > 0 })
            {
                var rows = new List<TableRow>(modules.HeavyTypeDensityModules.Count);
                for (int i = 0; i < modules.HeavyTypeDensityModules.Count; i++)
                {
                    ModuleTypeDensity density = modules.HeavyTypeDensityModules[i];
                    rows.Add(Row(
                        Cell(FormatHelper.TruncateString(density.ModuleName, 55)),
                        Cell(FormatHelper.TruncateString(density.AssemblyName, 55)),
                        Cell(density.UniqueTypeCount.ToString("N0"), density.UniqueTypeCount),
                        Cell(density.ObjectCount.ToString("N0"), density.ObjectCount),
                        Cell(FormatBytes(density.TotalBytes), (long)Math.Min(density.TotalBytes, long.MaxValue)),
                        Cell(FormatBytes(density.BytesPerType), (long)Math.Min(density.BytesPerType, long.MaxValue))));
                }
                tables.Add(ST("Type density",
                    ["Module", "Assembly", "Types", "Objects", "Bytes", "Bytes/Type"], rows));
            }
        }

        if (domains?.TopModulesByTypeCount is { Count: > 0 })
        {
            var rows = new List<TableRow>(domains.TopModulesByTypeCount.Count);
            for (int i = 0; i < domains.TopModulesByTypeCount.Count; i++)
            {
                ModuleTypeCountEntry entry = domains.TopModulesByTypeCount[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(entry.ModuleName, 55)),
                    Cell(FormatHelper.TruncateString(entry.AssemblyName, 55)),
                    Cell(entry.TypeCount.ToString("N0"), entry.TypeCount),
                    Cell(entry.LiveTypeCount.ToString("N0"), entry.LiveTypeCount),
                    Cell(entry.ObjectCount.ToString("N0"), entry.ObjectCount),
                    Cell(FormatBytes(entry.TotalBytes), (long)Math.Min(entry.TotalBytes, long.MaxValue))));
            }
            tables.Add(ST("Top modules by type count",
                ["Module", "Assembly", "Types", "Live Types", "Objects", "Bytes"], rows));
        }

        if (domains is null && modules is null)
            blocks.Add(T("No appdomain or module result was available."));

        return new AnalyzerDetailSection(
            "Modules & Assemblies", DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics.Count > 0 ? keyMetrics : null,
            Tables: tables.Count > 0 ? tables : null);
    }

    private static List<TableRow> BuildDomainRows(IReadOnlyList<AppDomainSnapshot> domains)
    {
        var rows = new List<TableRow>(domains.Count);
        for (int i = 0; i < domains.Count; i++)
        {
            AppDomainSnapshot domain = domains[i];
            rows.Add(Row(
                Cell(FormatHelper.TruncateString(domain.Name, 60)),
                Cell(domain.DomainId.ToString("N0"), domain.DomainId),
                Cell($"0x{domain.Address:X}"),
                Cell(domain.ModuleCount.ToString("N0"), domain.ModuleCount),
                Cell(FormatBytes(domain.EstimatedManagedBytes), (long)Math.Min(domain.EstimatedManagedBytes, long.MaxValue))));
        }

        return rows;
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