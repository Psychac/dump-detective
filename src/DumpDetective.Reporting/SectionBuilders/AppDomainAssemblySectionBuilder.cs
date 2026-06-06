using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AppDomainAssemblySectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public IReadOnlyList<string> SourceAnalyzers => ["AppDomainAnalyzer", "ModuleAnalyzer"];

    public string SectionId => "G1";
    public string DisplayTitle => "Modules & Assemblies";
    public int SortOrder => 100;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<AppDomainDomainResult>() is not null
        || results.Get<ModuleDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        AppDomainDomainResult? domains = results.Get<AppDomainDomainResult>();
        ModuleDomainResult? modules = results.Get<ModuleDomainResult>();

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();
        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>();

        if (domains is not null)
        {
            keyMetrics["total_domains"] = new NumericMetricValue(domains.TotalDomains, MetricUnit.Count);
            keyMetrics["anonymous_modules"] = new NumericMetricValue(domains.AnonymousModuleCount, MetricUnit.Count);
            keyMetrics["dynamic_modules"] = new NumericMetricValue(domains.TotalDynamicModules, MetricUnit.Count);
            if (domains.DynamicModuleBytes > 0)
                keyMetrics["dynamic_module_bytes"] = new NumericMetricValue((double)domains.DynamicModuleBytes, MetricUnit.Bytes, FormatBytes(domains.DynamicModuleBytes));
            if (domains.ExcludedModuleCount > 0)
                keyMetrics["excluded_modules"] = new NumericMetricValue(domains.ExcludedModuleCount, MetricUnit.Count);
            compactTables.Add(STCompact(
                "AppDomain inventory",
                new[] { CH("Domain Name"), CH("ID", "number"), CH("Address"), CH("Module Count", "number"), CH("Estimated Managed Bytes", "bytes") },
                BuildDomainRows(domains.Domains).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (modules is not null)
        {
            keyMetrics["total_modules"] = new NumericMetricValue(modules.TotalModules, MetricUnit.Count);
            keyMetrics["unique_module_names"] = new NumericMetricValue(modules.UniqueModuleNames, MetricUnit.Count);
            keyMetrics["conflict_groups"] = new NumericMetricValue(modules.VersionConflictGroups, MetricUnit.Count);
            keyMetrics["dynamic_module_count"] = new NumericMetricValue(modules.DynamicModules, MetricUnit.Count);

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
                        Cell(FormatHelper.TruncateString(m.FullPath, 80)),
                        Cell($"0x{m.Address:X}"),
                        Cell(FormatBytes(m.Size), (long)Math.Min(m.Size, long.MaxValue)),
                        Cell(m.IsDynamic ? "Yes" : "No"),
                        Cell(m.IsPEFile ? "Yes" : "No")));
                }
                compactTables.Add(STCompact("Top modules by size", new[] { CH("Name"), CH("Assembly"), CH("Full Path"), CH("Address"), CH("Size","bytes"), CH("Dynamic"), CH("PE File") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
                compactTables.Add(STCompact("Conflict details", new[] { CH("Module"), CH("Instances","number"), CH("Assemblies") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
                compactTables.Add(STCompact("Modules by heap footprint",
                    new[] { CH("Module"), CH("Assembly"), CH("Types","number"), CH("Objects","number"), CH("Bytes","bytes"), CH("Objects/Type") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
                compactTables.Add(STCompact("Type density",
                    new[] { CH("Module"), CH("Assembly"), CH("Types","number"), CH("Objects","number"), CH("Bytes","bytes"), CH("Bytes/Type","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
            compactTables.Add(STCompact("Top modules by type count",
                new[] { CH("Module"), CH("Assembly"), CH("Types","number"), CH("Live Types","number"), CH("Objects","number"), CH("Bytes","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (domains is null && modules is null)
            blocks.Add(T("No appdomain or module result was available."));

            return new AnalyzerDetailSection(
            "Modules & Assemblies", DisplayTitle, SortOrder, blocks,
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