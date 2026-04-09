using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ModuleAnalyzer
    {
        private readonly OutputWriter _writer;

        public ModuleAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrRuntime runtime)
        {
            _writer.WriteHeader("MODULE/ASSEMBLY ANALYSIS:");
            _writer.WriteLine("Analyzing loaded modules and assemblies...\n");

            var modules = AnalyzeModules(runtime);

            PrintModuleSummary(modules);
            PrintLoadedAssemblies(modules);
            PrintVersionConflicts(modules);

            _writer.WriteLine(StringConstants.Equals80);
        }

        private ModuleAnalysis AnalyzeModules(ClrRuntime runtime)
        {
            var analysis = new ModuleAnalysis();
            var modulesByName = new Dictionary<string, List<ModuleInfo>>();

            foreach (var module in runtime.EnumerateModules())
            {
                if (module.Name == null)
                    continue;

                string moduleName = Path.GetFileName(module.Name);
                string? version = module.AssemblyName ?? "Unknown";

                var moduleInfo = new ModuleInfo
                {
                    Name = moduleName,
                    FullPath = module.Name,
                    AssemblyName = version,
                    Address = module.Address,
                    Size = module.Size,
                    IsDynamic = module.IsDynamic
                };

                if (!modulesByName.TryGetValue(moduleName, out var list))
                {
                    list = new List<ModuleInfo>();
                    modulesByName[moduleName] = list;
                }
                list.Add(moduleInfo);

                analysis.TotalModules++;
                
                if (module.IsDynamic)
                    analysis.DynamicModules++;
            }

            analysis.ModulesByName = modulesByName;
            analysis.VersionConflicts = modulesByName
                .Where(kvp => kvp.Value.Count > 1)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return analysis;
        }

        private void PrintModuleSummary(ModuleAnalysis analysis)
        {
            _writer.WriteLine("MODULE SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Total Modules Loaded: {analysis.TotalModules:N0}");
            _writer.WriteLine($"Unique Module Names: {analysis.ModulesByName.Count:N0}");
            _writer.WriteLine($"Dynamic Modules: {analysis.DynamicModules:N0}");
            
            if (analysis.VersionConflicts.Count > 0)
            {
                _writer.WriteLine($"\n⚠️  VERSION CONFLICTS: {analysis.VersionConflicts.Count} module(s) loaded multiple times!");
            }
        }

        private void PrintLoadedAssemblies(ModuleAnalysis analysis)
        {
            _writer.WriteLine("\n\nLOADED ASSEMBLIES (Top 30):");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Module Name",-40} {"Version/Assembly Name",-45} {"Size",12}");
            _writer.WriteSeparator();

            var topModules = analysis.ModulesByName.Values
                .Select(list => list.First())
                .OrderByDescending(m => m.Size)
                .Take(30);

            foreach (var module in topModules)
            {
                string dynamicMarker = module.IsDynamic ? " [Dynamic]" : "";
                _writer.WriteLine($"{FormatHelper.TruncateString(module.Name, 40),-40} {FormatHelper.TruncateString(module.AssemblyName, 45),-45} {FormatHelper.FormatBytes(module.Size),12}{dynamicMarker}");
            }
        }

        private void PrintVersionConflicts(ModuleAnalysis analysis)
        {
            if (analysis.VersionConflicts.Count == 0)
            {
                _writer.WriteLine("\n\n✅ No version conflicts detected.");
                return;
            }

            _writer.WriteLine("\n\n⚠️  VERSION CONFLICTS DETECTED:");
            _writer.WriteSeparator();
            _writer.WriteLine("The following modules are loaded multiple times with different versions:\n");

            foreach (var kvp in analysis.VersionConflicts)
            {
                _writer.WriteLine($"Module: {kvp.Key}");
                foreach (var module in kvp.Value)
                {
                    _writer.WriteLine($"  - Version: {module.AssemblyName}");
                    _writer.WriteLine($"    Path: {FormatHelper.TruncateString(module.FullPath, 70)}");
                    _writer.WriteLine($"    Address: 0x{module.Address:X}, Size: {FormatHelper.FormatBytes(module.Size)}");
                }
                _writer.WriteLine($"\n  💡 RECOMMENDATION:");
                _writer.WriteLine($"     Ensure binding redirects are configured correctly.");
                _writer.WriteLine($"     Check for dependency conflicts in your project.\n");
            }
        }
    }

    internal class ModuleAnalysis
    {
        public int TotalModules { get; set; }
        public int DynamicModules { get; set; }
        public Dictionary<string, List<ModuleInfo>> ModulesByName { get; set; } = new();
        public Dictionary<string, List<ModuleInfo>> VersionConflicts { get; set; } = new();
    }

    internal class ModuleInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string AssemblyName { get; set; } = string.Empty;
        public ulong Address { get; set; }
        public ulong Size { get; set; }
        public bool IsDynamic { get; set; }
    }
}
