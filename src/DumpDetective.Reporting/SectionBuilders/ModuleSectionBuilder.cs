using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ModuleSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName  => "Module Analysis";
    public string DisplayTitle  => "Modules & Assemblies";
    public int SortOrder        => 120;

    public bool CanHandle(AnalyzerDomainResult result) => result is ModuleDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ModuleDomainResult)result;

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_modules"] = new NumericMetricValue(d.TotalModules, MetricUnit.Count),
            ["unique_module_names"] = new NumericMetricValue(d.UniqueModuleNames, MetricUnit.Count),
            ["conflict_groups"] = new NumericMetricValue(d.VersionConflictGroups, MetricUnit.Count),
            ["dynamic_modules"] = new NumericMetricValue(d.DynamicModules, MetricUnit.Count),
            ["total_domains"] = new NumericMetricValue(d.TotalDomains, MetricUnit.Count),
            ["anonymous_modules"] = new NumericMetricValue(d.AnonymousModuleCount, MetricUnit.Count),
            ["appdomain_dynamic_modules"] = new NumericMetricValue(d.TotalDynamicModules, MetricUnit.Count),
        };
        if (d.DynamicModuleBytes > 0)
            keyMetrics["dynamic_module_bytes"] = new NumericMetricValue((double)d.DynamicModuleBytes, MetricUnit.Bytes, FormatBytes(d.DynamicModuleBytes));
        if (d.ExcludedModuleCount > 0)
            keyMetrics["excluded_modules"] = new NumericMetricValue(d.ExcludedModuleCount, MetricUnit.Count);

        // Build summary narrative
        var summaryParts = new List<string>();
        summaryParts.Add($"{d.TotalModules:N0} modules loaded across {d.TotalDomains:N0} AppDomain(s)");

        if (d.DynamicModules > 0)
            summaryParts.Add($"{d.DynamicModules:N0} native dynamic modules");

        if (d.TotalDynamicModules > 0)
            summaryParts.Add($"{d.TotalDynamicModules:N0} runtime-generated dynamic modules");

        if (d.VersionConflictGroups > 0)
            summaryParts.Add($"{d.VersionConflictGroups:N0} version conflict group(s)");

        if (d.UnknownIdentityDuplicateModules.Count > 0)
            summaryParts.Add($"{d.UnknownIdentityDuplicateModules.Count:N0} unknown-identity duplicate(s)");

        if (d.ExcludedModuleCount > 0)
            summaryParts.Add($"{d.ExcludedModuleCount:N0} modules excluded from deep analysis");

        if (d.AnonymousModuleCount > 0)
            summaryParts.Add($"{d.AnonymousModuleCount:N0} anonymous/in-memory modules");

        var blocks = new List<SectionBlock>
        {
            T($"Runtime inventory: {string.Join(", ", summaryParts)}."),
            T(d.ConflictingAssemblyNames.Count > 0
                ? $"Conflict groups include: {string.Join(", ", d.ConflictingAssemblyNames.Take(6))}."
                : "No version conflict groups were reported."),
        };

        var compactTables = new List<CompactTable>();

        if (d.TopModulesBySize is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopModulesBySize.Count);
            for (int i = 0; i < d.TopModulesBySize.Count; i++)
            {
                LoadedModuleSnapshot m = d.TopModulesBySize[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(m.Name, 55)),
                    Cell(FormatHelper.TruncateString(m.AssemblyName, 55)),
                    Cell(FormatHelper.TruncateString(m.FullPath, 80)),
                    Cell($"0x{m.Address:X}"),
                    Cell(FormatBytes(m.Size),  (long)Math.Min(m.Size,  long.MaxValue)),
                    Cell(m.IsDynamic  ? "Yes" : "No"),
                    Cell(m.IsPEFile   ? "Yes" : "No")));
            }
            compactTables.Add(STCompact("Top modules by size", new[] { CH("Name"), CH("Assembly"), CH("Full Path"), CH("Address"), CH("Size","bytes"), CH("Dynamic"), CH("PE File") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.ConflictDetails.Count > 0)
        {
            var rows = new List<TableRow>(d.ConflictDetails.Count);
            for (int i = 0; i < d.ConflictDetails.Count; i++)
            {
                ModuleConflictGroup conflict = d.ConflictDetails[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(conflict.ModuleName, 60)),
                    Cell(conflict.Instances.Count.ToString("N0"), conflict.Instances.Count),
                    Cell(string.Join("; ", conflict.Instances.Take(3).Select(m => m.AssemblyName)))));
            }
            compactTables.Add(STCompact("Conflict details", new[] { CH("Module"), CH("Instances","number"), CH("Assemblies") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopModulesByHeapMemory is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.TopModulesByHeapMemory.Count);
            for (int i = 0; i < d.TopModulesByHeapMemory.Count; i++)
            {
                ModuleHeapStats stats = d.TopModulesByHeapMemory[i];
                double objectsPerType = stats.UniqueTypeCount > 0 ? stats.ObjectCount / (double)stats.UniqueTypeCount : 0.0;
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(stats.ModuleName, 55)),
                    Cell(FormatHelper.TruncateString(stats.AssemblyName, 55)),
                    Cell(stats.UniqueTypeCount.ToString("N0"),  stats.UniqueTypeCount),
                    Cell(stats.ObjectCount.ToString("N0"),      stats.ObjectCount),
                    Cell(FormatBytes(stats.TotalBytes),         (long)Math.Min(stats.TotalBytes, long.MaxValue)),
                    Cell(stats.Gen2ObjectCount.ToString("N0"),  stats.Gen2ObjectCount),
                    Cell(FormatBytes(stats.LohBytes),           (long)Math.Min(stats.LohBytes, long.MaxValue))));
            }
            compactTables.Add(STCompact("Modules by heap footprint", new[] { CH("Module"), CH("Assembly"), CH("Types","number"), CH("Objects","number"), CH("Total Bytes","bytes"), CH("Gen2 Objects","number"), CH("LOH Bytes","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.HeavyTypeDensityModules is { Count: > 0 })
        {
            var rows = new List<TableRow>(d.HeavyTypeDensityModules.Count);
            for (int i = 0; i < d.HeavyTypeDensityModules.Count; i++)
            {
                ModuleTypeDensity density = d.HeavyTypeDensityModules[i];
                rows.Add(Row(
                    Cell(FormatHelper.TruncateString(density.ModuleName, 55)),
                    Cell(FormatHelper.TruncateString(density.AssemblyName, 55)),
                    Cell(density.UniqueTypeCount.ToString("N0"),  density.UniqueTypeCount),
                    Cell(density.ObjectCount.ToString("N0"),      density.ObjectCount),
                    Cell(FormatBytes(density.TotalBytes),         (long)Math.Min(density.TotalBytes, long.MaxValue)),
                    Cell(FormatBytes(density.BytesPerType),       (long)Math.Min(density.BytesPerType, long.MaxValue))));
            }
            compactTables.Add(STCompact("Type density", new[] { CH("Module"), CH("Assembly"), CH("Types","number"), CH("Objects","number"), CH("Bytes","bytes"), CH("Bytes/Type","bytes") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.Domains is { Count: > 0 })
        {
            compactTables.Add(STCompact(
                "AppDomain inventory",
                new[] { CH("Domain Name"), CH("ID", "number"), CH("Address"), CH("Module Count", "number"), CH("Estimated Managed Bytes", "bytes") },
                BuildDomainRows(d.Domains).Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopModulesByTypeCount is { Count: > 0 })
        {
            var typeCountRows = new List<TableRow>(d.TopModulesByTypeCount.Count);
            for (int i = 0; i < d.TopModulesByTypeCount.Count; i++)
            {
                ModuleTypeCountEntry entry = d.TopModulesByTypeCount[i];
                typeCountRows.Add(Row(
                    Cell(FormatHelper.TruncateString(entry.ModuleName, 55)),
                    Cell(FormatHelper.TruncateString(entry.AssemblyName, 55)),
                    Cell(entry.TypeCount.ToString("N0"),     entry.TypeCount),
                    Cell(entry.LiveTypeCount.ToString("N0"), entry.LiveTypeCount),
                    Cell(entry.ObjectCount.ToString("N0"),   entry.ObjectCount),
                    Cell(FormatBytes(entry.TotalBytes),      (long)Math.Min(entry.TotalBytes, long.MaxValue))));
            }
            compactTables.Add(STCompact("Top modules by type count",
                new[] { CH("Module"), CH("Assembly"), CH("Types", "number"), CH("Live Types", "number"), CH("Objects", "number"), CH("Bytes", "bytes") },
                typeCountRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder,
            Blocks: blocks.Count > 0 ? blocks : [],
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
