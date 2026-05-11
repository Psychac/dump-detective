using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AppDomainAssemblySectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.appdomain-assembly";
    public string DisplayTitle => "AppDomain & Assembly";
    public int SortOrder => 1800;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<AppDomainDomainResult>() is not null
        || results.Get<ModuleDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        AppDomainDomainResult? domains = results.Get<AppDomainDomainResult>();
        ModuleDomainResult? modules = results.Get<ModuleDomainResult>();

        var blocks = new List<SectionBlock>
        {
            H("APPDOMAIN INVENTORY"),
            T("AppDomain and assembly/module inventory summarised from the available results."),
        };

        if (domains is not null)
        {
            blocks.Add(new TableBlock(
                Caption: "AppDomain inventory",
                Headers: ["Domain Name", "ID", "Address", "Module Count", "Estimated Managed Bytes"],
                Rows: BuildDomainRows(domains.Domains)));

            blocks.Add(Blank());
            blocks.Add(M("Total domains", domains.TotalDomains.ToString("N0"), domains.TotalDomains));
            blocks.Add(M("Anonymous modules", domains.AnonymousModuleCount.ToString("N0"), domains.AnonymousModuleCount));
            blocks.Add(M("Dynamic modules", domains.TotalDynamicModules.ToString("N0"), domains.TotalDynamicModules));
        }

        if (modules is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("ASSEMBLY VERSION CONFLICTS"));
            blocks.Add(M("Conflict groups", modules.VersionConflictGroups.ToString("N0"), modules.VersionConflictGroups));
            blocks.Add(M("Dynamic module count", modules.DynamicModules.ToString("N0"), modules.DynamicModules));
            blocks.Add(T(modules.ConflictingAssemblyNames.Count > 0
                ? $"Conflict groups include: {string.Join(", ", modules.ConflictingAssemblyNames.Take(6))}."
                : "No version conflict groups were reported."));

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

                blocks.Add(new TableBlock("Conflict details", ["Module", "Instances", "Assemblies"], rows));
            }

            if (modules.TopModulesByHeapMemory is { Count: > 0 })
            {
                blocks.Add(Blank());
                blocks.Add(H("TYPE DENSITY PER MODULE"));
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

                blocks.Add(new TableBlock("Modules by heap footprint", ["Module", "Assembly", "Types", "Objects", "Bytes", "Objects/Type"], rows));
            }

            if (modules.HeavyTypeDensityModules is { Count: > 0 })
            {
                blocks.Add(Blank());
                blocks.Add(H("HEAVY TYPE DENSITY MODULES"));
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

                blocks.Add(new TableBlock("Type density", ["Module", "Assembly", "Types", "Objects", "Bytes", "Bytes/Type"], rows));
            }
        }

        if (domains is null && modules is null)
            blocks.Add(T("No appdomain or module result was available."));

        return new AnalyzerDetailSection("AppDomain & Assembly", DisplayTitle, SortOrder, blocks);
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