using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ModuleSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopModulesToShow = 30;

    public string AnalyzerName => "Module Analysis";
    public int SortOrder => 40;

    public bool CanHandle(AnalyzerDomainResult result) => result is ModuleDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ModuleDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("MODULE SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Modules Loaded", $"{d.TotalModules:N0}",       d.TotalModules));
        blocks.Add(M("Unique Module Names",  $"{d.UniqueModuleNames:N0}",   d.UniqueModuleNames));
        blocks.Add(M("Dynamic Modules",      $"{d.DynamicModules:N0}",      d.DynamicModules));

        if (d.VersionConflictGroups > 0)
        {
            blocks.Add(Blank());
            blocks.Add(T($"VERSION CONFLICTS: {d.VersionConflictGroups:N0} module(s) loaded multiple times!"));
        }

        blocks.Add(Blank());
        blocks.Add(H($"LOADED ASSEMBLIES (Top {TopModulesToShow})"));
        blocks.Add(Divider());

        var modRows = new List<TableRow>(Math.Min(d.TopModulesBySize.Count, TopModulesToShow));
        int modLimit = Math.Min(d.TopModulesBySize.Count, TopModulesToShow);
        for (int i = 0; i < modLimit; i++)
        {
            var m = d.TopModulesBySize[i];
            modRows.Add(new TableRow([
                Cell(m.Name),
                Cell(m.AssemblyName),
                Cell(FormatHelper.FormatBytes(m.Size), (long)m.Size),
                Cell(m.IsDynamic ? "Yes" : "No")]));
        }
        blocks.Add(new TableBlock("Loaded assemblies (top 30 by size)", ["Module Name", "Assembly Name", "Size", "Dynamic"], modRows));

        var heapModules = d.TopModulesByHeapMemory ?? [];
        if (heapModules.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP MODULES BY HEAP MEMORY"));
            blocks.Add(Divider());

            var hmRows = new List<TableRow>(heapModules.Count);
            for (int i = 0; i < heapModules.Count; i++)
            {
                var m = heapModules[i];
                hmRows.Add(new TableRow([
                    Cell(m.ModuleName),
                    Cell(m.AssemblyName),
                    Cell(FormatHelper.FormatBytes(m.TotalBytes), (long)m.TotalBytes),
                    Cell($"{m.ObjectCount:N0}", m.ObjectCount),
                    Cell($"{m.UniqueTypeCount:N0}", m.UniqueTypeCount)]));
            }
            blocks.Add(new TableBlock("Modules ranked by live heap memory", ["Module Name", "Assembly Name", "Heap Memory", "Objects", "Unique Types"], hmRows));
        }

        var densityModules = d.HeavyTypeDensityModules ?? [];
        if (densityModules.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TYPE DENSITY ANOMALIES"));
            blocks.Add(T("Modules with very few types consuming large amounts of heap memory:"));
            blocks.Add(Blank());

            var dmRows = new List<TableRow>(densityModules.Count);
            for (int i = 0; i < densityModules.Count; i++)
            {
                var m = densityModules[i];
                dmRows.Add(new TableRow([
                    Cell(m.ModuleName),
                    Cell(m.AssemblyName),
                    Cell($"{m.UniqueTypeCount:N0}", m.UniqueTypeCount),
                    Cell($"{m.ObjectCount:N0}",     m.ObjectCount),
                    Cell(FormatHelper.FormatBytes(m.TotalBytes),   (long)m.TotalBytes),
                    Cell(FormatHelper.FormatBytes(m.BytesPerType), (long)m.BytesPerType)]));
            }
            blocks.Add(new TableBlock("High memory concentration", ["Module Name", "Assembly Name", "Types", "Objects", "Heap Memory", "Bytes / Type"], dmRows));
        }

        if (d.ConflictDetails.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("VERSION CONFLICT DETAILS"));
            blocks.Add(Divider());

            foreach (var group in d.ConflictDetails)
            {
                blocks.Add(CollapseBegin($"Conflict: {group.ModuleName} ({group.Instances.Count} copies)"));

                var conflictRows = new List<TableRow>(group.Instances.Count);
                for (int i = 0; i < group.Instances.Count; i++)
                {
                    var inst = group.Instances[i];
                    conflictRows.Add(new TableRow([
                        Cell(inst.AssemblyName),
                        Cell(inst.FullPath),
                        Cell(FormatHelper.FormatBytes(inst.Size), (long)inst.Size),
                        Cell(inst.IsDynamic ? "Yes" : "No")]));
                }
                blocks.Add(new TableBlock(null, ["Assembly Name", "Path", "Size", "Dynamic"], conflictRows));
                blocks.Add(CollapseEnd());
            }
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
