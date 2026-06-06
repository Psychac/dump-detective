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
        };

        var blocks = new List<SectionBlock>
        {
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
                    Cell(objectsPerType.ToString("F1"))));
            }
            compactTables.Add(STCompact("Modules by heap footprint", new[] { CH("Module"), CH("Assembly"), CH("Types","number"), CH("Objects","number"), CH("Bytes","bytes"), CH("Objects/Type") }, rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder,
            Blocks: blocks.Count > 0 ? blocks : [],
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
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
