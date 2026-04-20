using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    internal class ModuleAnalyzer : IAnalyzer
    {
        private const int TopLoadedAssembliesCount = 30;

        public string Name => "Module Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzerExecutionResult executionResult = Analyze(context.Runtime);
            return ValueTask.FromResult(AnalyzerDomainResultFactory.FromExecutionResult(this, executionResult));
        }

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime)
        {
            var modules = AnalyzeModules(runtime);
            var domainResult = BuildDomainResult(modules);
            return new AnalyzerExecutionResult([CreateFinding(domainResult)], domainResult);
        }

        private static InsightFinding CreateFinding(ModuleDomainResult result)
        {
            int conflicts = result.VersionConflictGroups;
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
                Evidence: $"{result.TotalModules:N0} modules loaded, {result.DynamicModules:N0} dynamic, {conflicts:N0} version conflict group(s).",
                Recommendation: conflicts > 0
                    ? "Align dependency versions and verify binding redirects/deployment consistency."
                    : "No immediate module-version conflict action required.",
                Tags: ["modules", "assemblies", "dependency"],
                MetricValue: conflicts,
                MetricUnit: "conflict-groups");
        }

        private static ModuleDomainResult BuildDomainResult(ModuleAnalysis analysis)
        {
            // Top modules by size — same selection logic as PrintLoadedAssemblies.
            var candidates = new List<ModuleInfo>(analysis.ModulesByName.Count);
            foreach (var group in analysis.ModulesByName.Values)
            {
                if (group.Count == 0) continue;
                ModuleInfo largest = group[0];
                for (int i = 1; i < group.Count; i++)
                {
                    if (group[i].Size > largest.Size)
                        largest = group[i];
                }
                candidates.Add(largest);
            }
            candidates.Sort((a, b) => b.Size.CompareTo(a.Size));

            var topModules = new List<LoadedModuleSnapshot>(TopLoadedAssembliesCount);
            for (int i = 0; i < candidates.Count && i < TopLoadedAssembliesCount; i++)
            {
                var m = candidates[i];
                topModules.Add(new LoadedModuleSnapshot(m.Name, m.AssemblyName, m.FullPath, m.Address, m.Size, m.IsDynamic));
            }

            // Conflict groups — preserve full per-instance detail for the printer.
            var conflictDetails = new List<ModuleConflictGroup>(analysis.VersionConflicts.Count);
            foreach (var kvp in analysis.VersionConflicts)
            {
                var instances = new List<LoadedModuleSnapshot>(kvp.Value.Count);
                foreach (var m in kvp.Value)
                    instances.Add(new LoadedModuleSnapshot(m.Name, m.AssemblyName, m.FullPath, m.Address, m.Size, m.IsDynamic));
                conflictDetails.Add(new ModuleConflictGroup(kvp.Key, instances));
            }

            return new ModuleDomainResult(
                analysis.TotalModules,
                analysis.DynamicModules,
                analysis.ModulesByName.Count,
                analysis.VersionConflicts.Count,
                analysis.VersionConflicts.Keys.ToList(),
                topModules,
                conflictDetails);
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


