using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class ModulePrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Module Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is ModuleDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not ModuleDomainResult domain)
                return;

            writer.WriteHeader("MODULE/ASSEMBLY ANALYSIS:");

            writer.WriteLine("MODULE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Total Modules Loaded: {domain.TotalModules:N0}");
            writer.WriteLine($"Unique Module Names: {domain.UniqueModuleNames:N0}");
            writer.WriteLine($"Dynamic Modules: {domain.DynamicModules:N0}");

            if (domain.VersionConflictGroups > 0)
                writer.WriteLine($"\n⚠️  VERSION CONFLICTS: {domain.VersionConflictGroups:N0} module(s) loaded multiple times!");

            writer.WriteLine("\n\nLOADED ASSEMBLIES (Top 30):");
            writer.WriteSeparator();
            writer.WriteLine($"{"Module Name",-40} {"Version/Assembly Name",-45} {"Size",12}");
            writer.WriteSeparator();

            foreach (var module in domain.TopModulesBySize)
            {
                string dynamicMarker = module.IsDynamic ? " [Dynamic]" : "";
                writer.WriteLine($"{FormatHelper.TruncateString(module.Name, 40),-40} {FormatHelper.TruncateString(module.AssemblyName, 45),-45} {FormatHelper.FormatBytes(module.Size),12}{dynamicMarker}");
            }

            if (domain.ConflictDetails.Count == 0)
            {
                writer.WriteLine("\n\n✅ No version conflicts detected.");
                writer.WriteLine(StringConstants.Equals80);
                return;
            }

            writer.WriteLine("\n\n⚠️  VERSION CONFLICTS DETECTED:");
            writer.WriteSeparator();
            writer.WriteLine("The following modules are loaded multiple times with different versions:\n");

            foreach (var conflict in domain.ConflictDetails)
            {
                writer.WriteLine($"Module: {conflict.ModuleName}");
                foreach (var module in conflict.Instances)
                {
                    writer.WriteLine($"  - Version: {module.AssemblyName}");
                    writer.WriteLine($"    Path: {FormatHelper.TruncateString(module.FullPath, 70)}");
                    writer.WriteLine($"    Address: 0x{module.Address:X}, Size: {FormatHelper.FormatBytes(module.Size)}");
                }
                writer.WriteLine("\n  💡 RECOMMENDATION:");
                writer.WriteLine("     Ensure binding redirects are configured correctly.");
                writer.WriteLine("     Check for dependency conflicts in your project.\n");
            }

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
