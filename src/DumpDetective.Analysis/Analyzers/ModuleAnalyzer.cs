using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Utilities;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class ModuleAnalyzer : IAnalyzer
    {
        public string Name => "Module Analysis";
        public string Category => "Modules";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleAnalysisOptions options = context.GetOption<ModuleAnalysisOptions>();
            var modules = AnalyzeModules(context.Runtime);
            var heapStats = BuildModuleHeapStats(context.Cache, options);
            return ValueTask.FromResult(BuildDomainResult(modules, options, heapStats).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime)
        {
            return Analyze(runtime, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "analyzing modules"));
            var modules = AnalyzeModules(runtime);
            var domainResult = BuildDomainResult(modules, new ModuleAnalysisOptions(), heapStats: null);
            return domainResult;
        }

        private static ModuleDomainResult BuildDomainResult(ModuleAnalysis analysis, ModuleAnalysisOptions options, (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies)? heapStats)
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

            var topModules = new List<LoadedModuleSnapshot>(options.TopLoadedAssembliesCount);
            for (int i = 0; i < candidates.Count && i < options.TopLoadedAssembliesCount; i++)
            {
                var m = candidates[i];
                topModules.Add(new LoadedModuleSnapshot(m.Name, m.AssemblyName, m.FullPath, m.Address, m.Size, m.IsDynamic, m.IsPEFile));
            }

            // Conflict groups — preserve full per-instance detail for the printer.
            var conflictDetails = new List<ModuleConflictGroup>(analysis.VersionConflicts.Count);
            foreach (var kvp in analysis.VersionConflicts)
            {
                var instances = new List<LoadedModuleSnapshot>(kvp.Value.Count);
                foreach (var m in kvp.Value)
                    instances.Add(new LoadedModuleSnapshot(m.Name, m.AssemblyName, m.FullPath, m.Address, m.Size, m.IsDynamic, m.IsPEFile));
                conflictDetails.Add(new ModuleConflictGroup(kvp.Key, instances));
            }

            return new ModuleDomainResult(
                analysis.TotalModules,
                analysis.DynamicModules,
                analysis.ModulesByName.Count,
                analysis.VersionConflicts.Count,
                analysis.VersionConflicts.Keys.ToList(),
                topModules,
                conflictDetails,
                heapStats?.TopByMemory,
                heapStats?.DensityAnomalies);
        }

        private static (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies)?
            BuildModuleHeapStats(IHeapAnalysisCache cache, ModuleAnalysisOptions options)
        {
            // Requires a prebuilt heap index with module registry data.
            if (cache is not IHeapIndexBuilder builder || !builder.TryGetHeapIndex(out var index))
                return null;
            if (index.Modules is not { Count: > 0 } modules)
                return null;

            // Delegate aggregation to ModuleAggregator
            return ModuleAggregator.Aggregate(index.TypeAggregates, index.Modules, options);
        }

        private ModuleAnalysis AnalyzeModules(ClrRuntime runtime)
        {
            var analysis = new ModuleAnalysis();
            var modulesByName = new Dictionary<string, List<ModuleInfo>>();
            var processedModuleAddresses = new HashSet<ulong>();
            var moduleByAddress = new Dictionary<ulong, ClrModule>();

            // Scope-local string pool: deduplicates repeated path/name strings within this analysis pass.
            // Unlike string.Intern() this is GC'd when AnalyzeModules returns — safe for multi-dump scenarios.
            var stringPool = new Dictionary<string, string>(StringComparer.Ordinal);
            string Pool(string s) => stringPool.TryGetValue(s, out var c) ? c : (stringPool[s] = s);

            var scanCounter = new ObjectScanCounter("Module scan");

            foreach (var module in runtime.EnumerateModules())
            {
                scanCounter.Tick();

                if (module.Name == null)
                    continue;

                if (module.Address == 0 || !processedModuleAddresses.Add(module.Address))
                    continue;

                // keep a reference to the ClrModule for later in-memory manifest probing
                moduleByAddress[module.Address] = module;

                // Pool filename and assembly name — directory prefix segments repeat heavily across modules.
                string moduleName = Pool(Path.GetFileName(module.Name));
                string assemblyName = Pool(module.AssemblyName ?? "Unknown");

                var moduleInfo = new ModuleInfo
                {
                    Name = moduleName,
                    FullPath = Pool(module.Name),   // pool the full path: same dir prefix shared by many modules
                    AssemblyName = assemblyName,
                    Address = module.Address,
                    Size = module.Size,
                    IsDynamic = module.IsDynamic,
                    IsPEFile = module.IsPEFile
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
                var identities = new Dictionary<AssemblyIdentity, List<ModuleInfo>>(new AssemblyIdentityComparer());
                foreach (var moduleInfo in kvp.Value)
                {
                    moduleByAddress.TryGetValue(moduleInfo.Address, out var clrModule);
                    var identity = ModuleProbe.ProbeAssemblyIdentity(clrModule, moduleInfo.AssemblyName);
                    if (!identities.TryGetValue(identity, out var list))
                    {
                        list = new List<ModuleInfo>();
                        identities[identity] = list;
                    }
                    list.Add(moduleInfo);
                }

                // Partition into known vs unknown identities. Unknown = no version, no public-key-token, no file-hash.
                int knownCount = 0;
                foreach (var kv in identities)
                {
                    var id = kv.Key;
                    bool isUnknown = string.IsNullOrEmpty(id.Version) && string.IsNullOrEmpty(id.PublicKeyToken) && string.IsNullOrEmpty(id.FileHash);
                    if (!isUnknown) knownCount++;
                }

                if (knownCount > 1)
                {
                    // Multiple distinct, well-known identities -> real conflict
                    versionConflicts[kvp.Key] = kvp.Value;
                }
                else
                {
                    // No conflicting known identities; mark any unknowns for reporting but do NOT treat as conflicts
                    foreach (var kv in identities)
                    {
                        var id = kv.Key;
                        bool isUnknown = string.IsNullOrEmpty(id.Version) && string.IsNullOrEmpty(id.PublicKeyToken) && string.IsNullOrEmpty(id.FileHash);
                        if (isUnknown)
                        {
                            foreach (var mi in kv.Value)
                            {
                                if (!mi.AssemblyName.Contains("(Unknown identity)", StringComparison.Ordinal))
                                    mi.AssemblyName = mi.AssemblyName + " (Unknown identity)";
                            }
                        }
                    }
                }
            }

            // Clear any transient ClrModule references to avoid accidental long-lived retention.
            // moduleByAddress is a local cache for in-memory probing only; explicitly clear it before returning.
            moduleByAddress.Clear();

            // String pool is scope-local and GC'd naturally; clear it explicitly to release references promptly.
            stringPool.Clear();

            analysis.VersionConflicts = versionConflicts;

            return analysis;
        }


        public void Dispose() { }
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
        public bool IsPEFile { get; set; }
    }
}


