using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class ModulePrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Module Analysis";
        public string DisplayTitle => "Loaded Modules";
        public int SortOrder => 160;

        public bool CanHandle(AnalyzerDomainResult result) => result is ModuleDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not ModuleDomainResult domain)
                return;

            writer.WriteHeader("MODULE/ASSEMBLY ANALYSIS:");

            writer.WriteSubHeading("MODULE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Modules Loaded", $"{domain.TotalModules:N0}");
            writer.WriteMetric("Unique Module Names", $"{domain.UniqueModuleNames:N0}");
            writer.WriteMetric("Dynamic Modules", $"{domain.DynamicModules:N0}");

            if (domain.VersionConflictGroups > 0)
            {
                writer.WriteDetailBlank();
                writer.WriteDetailText($"⚠️  VERSION CONFLICTS: {domain.VersionConflictGroups:N0} module(s) loaded multiple times!");
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("LOADED ASSEMBLIES (Top 30):");
            writer.WriteSeparator();
            writer.WriteDetailTable(new DetailedAnalyzerTableData(
                Caption: "Loaded assemblies (top 30 by size)",
                Headers: ["Module Name", "Assembly Name", "Size", "Dynamic"],
                Rows: domain.TopModulesBySize.Select(module => new DetailedAnalyzerTableRow([
                    new DetailedAnalyzerTableCell(module.Name),
                    new DetailedAnalyzerTableCell(module.AssemblyName),
                    new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(module.Size), (long)module.Size),
                    new DetailedAnalyzerTableCell(module.IsDynamic ? "Yes" : "No")]))
                .ToList()));

            if (domain.TopModulesByHeapMemory is { Count: > 0 } heapModules)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("TOP MODULES BY HEAP MEMORY:");
                writer.WriteSeparator();
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "Modules ranked by live heap memory (from index)",
                    Headers: ["Module Name", "Assembly Name", "Heap Memory", "Objects", "Unique Types"],
                    Rows: heapModules.Select(m => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(m.ModuleName),
                        new DetailedAnalyzerTableCell(m.AssemblyName),
                        new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(m.TotalBytes), (long)m.TotalBytes),
                        new DetailedAnalyzerTableCell($"{m.ObjectCount:N0}", m.ObjectCount),
                        new DetailedAnalyzerTableCell($"{m.UniqueTypeCount:N0}", m.UniqueTypeCount)]))
                    .ToList()));
            }

            if (domain.HeavyTypeDensityModules is { Count: > 0 } densityModules)
            {
                writer.WriteDetailBlank();
                writer.WriteSubHeading("⚠️  TYPE DENSITY ANOMALIES:");
                writer.WriteSeparator();
                writer.WriteDetailText("Modules with very few types consuming large amounts of heap memory:");
                writer.WriteDetailBlank();
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "High memory concentration — few types, large footprint",
                    Headers: ["Module Name", "Assembly Name", "Types", "Objects", "Heap Memory", "Bytes / Type"],
                    Rows: densityModules.Select(m => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(m.ModuleName),
                        new DetailedAnalyzerTableCell(m.AssemblyName),
                        new DetailedAnalyzerTableCell($"{m.UniqueTypeCount:N0}", m.UniqueTypeCount),
                        new DetailedAnalyzerTableCell($"{m.ObjectCount:N0}", m.ObjectCount),
                        new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(m.TotalBytes), (long)m.TotalBytes),
                        new DetailedAnalyzerTableCell(FormatHelper.FormatBytes(m.BytesPerType), (long)m.BytesPerType)]))
                    .ToList()));
            }

            if (domain.ConflictDetails.Count == 0)
            {
                writer.WriteDetailBlank();
                writer.WriteDetailBlank();
                writer.WriteDetailText("✅ No version conflicts detected.");
                writer.WriteDetailDivider();
                return;
            }

            writer.WriteDetailBlank();
            writer.WriteDetailBlank();
            writer.WriteSubHeading("⚠️  VERSION CONFLICTS DETECTED:");
            writer.WriteSeparator();
            writer.WriteDetailText("The following modules are loaded multiple times with different versions:");
            writer.WriteDetailBlank();

            foreach (var conflict in domain.ConflictDetails)
            {
                writer.WriteMetric("Module", conflict.ModuleName);
                foreach (var module in conflict.Instances)
                {
                    writer.WriteDetailBullet($"Version: {module.AssemblyName}", indentLevel: 1);
                    writer.WritePathMetric("Path", FormatHelper.TruncateString(module.FullPath, 70), indentLevel: 2);
                    writer.WriteMetric("Address", $"0x{module.Address:X}, Size: {FormatHelper.FormatBytes(module.Size)}", indentLevel: 2);
                }
                writer.WriteDetailBlank();
                writer.WriteSubHeading("💡 RECOMMENDATION:", indentLevel: 1);
                writer.WriteDetailText("Ensure binding redirects are configured correctly.", indentLevel: 2);
                writer.WriteDetailText("Check for dependency conflicts in your project.", indentLevel: 2);
                writer.WriteDetailBlank();
            }

            writer.WriteDetailDivider();
        }
    }
}



