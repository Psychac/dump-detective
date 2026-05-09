using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class AppDomainSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopDomainRows = 10;
    private const int TopModuleRows = 20;

    public string AnalyzerName => "AppDomain Analysis";
    public int SortOrder => 41; // §18.1/18.3 — right after Module Analysis (§18, SortOrder 40)

    public bool CanHandle(AnalyzerDomainResult result) => result is AppDomainDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (AppDomainDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ──────────────────────────────────────────────────────────
        blocks.Add(H("APPDOMAIN SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total AppDomains", $"{d.TotalDomains:N0}", d.TotalDomains));
        blocks.Add(M("Dynamic Modules", $"{d.TotalDynamicModules:N0}", d.TotalDynamicModules));
        blocks.Add(M("Anonymous Modules", $"{d.AnonymousModuleCount:N0}", d.AnonymousModuleCount));

        // Optional: render warnings and excluded-module summary when present in the domain result
        if (d.Warnings is not null && d.Warnings.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("NOTES"));
            foreach (string w in d.Warnings)
                blocks.Add(T(w));
        }

        if (d.Metrics is not null && d.Metrics.TryGetValue("ExcludedModuleCount", out var excl) && excl is int exclCount && exclCount > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("EXCLUDED MODULES"));
            blocks.Add(T($"{exclCount:N0} modules were excluded from type enumeration due to configuration or budget."));
            blocks.Add(M("Excluded Modules", $"{exclCount:N0}", exclCount));
        }

        // ── AppDomain inventory ───────────────────────────────────────────────
        if (d.Domains.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("APPDOMAIN INVENTORY"));
            blocks.Add(T("Per-domain module count and estimated managed memory from types loaded into each domain. " +
                          "EstimatedManagedBytes is derived from TypeAggregates for the top 50 modules by size " +
                          "and may undercount domains with many small modules."));
            int limit = Math.Min(d.Domains.Count, TopDomainRows);
            blocks.Add(new TableBlock(
                Caption: "AppDomain inventory",
                Headers: ["Domain Name", "ID", "Address", "Module Count", "Estimated Managed Bytes"],
                Rows: BuildDomainRows(d.Domains, limit)));
        }

        // ── Type density per module ───────────────────────────────────────────
        if (d.TopModulesByTypeCount.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TYPE DENSITY PER MODULE (TOP MODULES)"));
            blocks.Add(T("Modules ranked by number of defined types, with live type count and heap footprint " +
                          "from the TypeAggregates index. A high TypeCount with low LiveTypeCount may indicate " +
                          "a large framework module where most types are never instantiated."));
            int limit = Math.Min(d.TopModulesByTypeCount.Count, TopModuleRows);
            blocks.Add(new TableBlock(
                Caption: "Top modules by defined type count",
                Headers: ["Module", "Assembly", "Defined Types", "Live Types", "Object Count", "Total Bytes"],
                Rows: BuildModuleRows(d.TopModulesByTypeCount, limit)));
        }

        return new AnalyzerDetailSection(AnalyzerName, "AppDomain Analysis", SortOrder, blocks);
    }

    private static List<TableRow> BuildDomainRows(IReadOnlyList<AppDomainSnapshot> domains, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            AppDomainSnapshot d = domains[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(d.Name, 60)),
                Cell($"{d.DomainId:N0}",                        d.DomainId),
                Cell($"0x{d.Address:X}"),
                Cell($"{d.ModuleCount:N0}",                     d.ModuleCount),
                Cell(FormatHelper.FormatBytes(d.EstimatedManagedBytes)),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildModuleRows(IReadOnlyList<ModuleTypeCountEntry> modules, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            ModuleTypeCountEntry m = modules[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(m.ModuleName,   50)),
                Cell(FormatHelper.TruncateString(m.AssemblyName, 50)),
                Cell($"{m.TypeCount:N0}",                        m.TypeCount),
                Cell($"{m.LiveTypeCount:N0}",                    m.LiveTypeCount),
                Cell($"{m.ObjectCount:N0}",                      m.ObjectCount),
                Cell(FormatHelper.FormatBytes(m.TotalBytes)),
            ]));
        }
        return rows;
    }
}
