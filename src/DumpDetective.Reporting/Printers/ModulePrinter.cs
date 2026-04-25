using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
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
            writer.WriteDetailBlank();
            writer.WriteSubHeading("LOADED ASSEMBLIES (Top 30):");
            writer.WriteSeparator();
            writer.WriteDetailText($"{"Module Name",-40} {"Version/Assembly Name",-45} {"Size",12}");
            writer.WriteSeparator();

            foreach (var module in domain.TopModulesBySize)
            {
                string dynamicMarker = module.IsDynamic ? " [Dynamic]" : "";
                writer.WriteDetailText($"{FormatHelper.TruncateString(module.Name, 40),-40} {FormatHelper.TruncateString(module.AssemblyName, 45),-45} {FormatHelper.FormatBytes(module.Size),12}{dynamicMarker}");
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



