using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ModuleAnalyzer
    {
        private const int TopLoadedAssembliesCount = 30;

        private readonly OutputWriter _writer;

        public ModuleAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerOutput Analyze(ClrRuntime runtime)
        {
            _writer.WriteHeader("MODULE/ASSEMBLY ANALYSIS:");

            var modules = AnalyzeModules(runtime);

            PrintModuleSummary(modules);
            PrintLoadedAssemblies(modules);
            PrintVersionConflicts(modules);

            _writer.WriteLine(StringConstants.Equals80);
            return new AnalyzerOutput(
                [CreateFinding(modules)],
                new ModuleDomainResult(
                    modules.TotalModules,
                    modules.DynamicModules,
                    modules.VersionConflicts.Count,
                    modules.VersionConflicts.Keys.ToList()));
        }

        private static InsightFinding CreateFinding(ModuleAnalysis analysis)
        {
            int conflicts = analysis.VersionConflicts.Count;
            FindingSeverity severity = conflicts >= 3
                ? FindingSeverity.Critical
                : conflicts > 0
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(ModuleAnalyzer),
                Category: "Dependency",
                Severity: severity,
                Title: conflicts > 0 ? "Module identity conflicts detected" : "Module dependency snapshot",
                Evidence: $"{analysis.TotalModules:N0} modules loaded, {analysis.DynamicModules:N0} dynamic, {conflicts:N0} version conflict group(s).",
                Recommendation: conflicts > 0
                    ? "Align dependency versions and verify binding redirects/deployment consistency."
                    : "No immediate module-version conflict action required.",
                Tags: ["modules", "assemblies", "dependency"],
                MetricValue: conflicts,
                MetricUnit: "conflict-groups");
        }

        private ModuleAnalysis AnalyzeModules(ClrRuntime runtime)
        {
            var analysis = new ModuleAnalysis();
            var modulesByName = new Dictionary<string, List<ModuleInfo>>();
            var scanCounter = new ObjectScanCounter("Module scan");

            foreach (var module in runtime.EnumerateModules())
            {
                scanCounter.Tick();

                if (module.Name == null)
                    continue;

                string moduleName = Path.GetFileName(module.Name);
                string assemblyName = module.AssemblyName ?? "Unknown";

                var moduleInfo = new ModuleInfo
                {
                    Name = moduleName,
                    FullPath = module.Name,
                    AssemblyName = assemblyName,
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

            scanCounter.Complete();

            analysis.ModulesByName = modulesByName;

            var versionConflicts = new Dictionary<string, List<ModuleInfo>>();
            foreach (var kvp in modulesByName)
            {
                if (kvp.Value.Count <= 1)
                    continue;

                // Conflict means same module file-name appears with different assembly identities.
                var assemblyNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var moduleInfo in kvp.Value)
                {
                    assemblyNames.Add(moduleInfo.AssemblyName);
                    if (assemblyNames.Count > 1)
                    {
                        versionConflicts[kvp.Key] = kvp.Value;
                        break;
                    }
                }
            }

            analysis.VersionConflicts = versionConflicts;

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
            _writer.WriteLine($"\n\nLOADED ASSEMBLIES (Top {TopLoadedAssembliesCount}):");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Module Name",-40} {"Version/Assembly Name",-45} {"Size",12}");
            _writer.WriteSeparator();

            var topModules = new List<ModuleInfo>(analysis.ModulesByName.Count);
            foreach (var moduleGroup in analysis.ModulesByName.Values)
            {
                if (moduleGroup.Count == 0)
                    continue;

                // Pick the largest instance for this module name.
                ModuleInfo selected = moduleGroup[0];
                for (int i = 1; i < moduleGroup.Count; i++)
                {
                    if (moduleGroup[i].Size > selected.Size)
                        selected = moduleGroup[i];
                }

                topModules.Add(selected);
            }

            topModules.Sort((a, b) => b.Size.CompareTo(a.Size));

            int count = 0;
            foreach (var module in topModules)
            {
                if (count >= TopLoadedAssembliesCount)
                    break;

                string dynamicMarker = module.IsDynamic ? " [Dynamic]" : "";
                _writer.WriteLine($"{FormatHelper.TruncateString(module.Name, 40),-40} {FormatHelper.TruncateString(module.AssemblyName, 45),-45} {FormatHelper.FormatBytes(module.Size),12}{dynamicMarker}");
                count++;
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
