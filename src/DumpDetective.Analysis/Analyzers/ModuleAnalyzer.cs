using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Analyzers
{
public class ModuleAnalyzer : IAnalyzer
    {
        private const int TopLoadedAssembliesCount = 30;
        private DumpDetective.Core.Options.ModuleAnalysisOptions _options = DumpDetective.Core.Options.ModuleAnalysisOptions.Default;

        public string Name => "Module Analysis";
        public string Category => "Modules";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _options = context.GetOption<DumpDetective.Core.Options.ModuleAnalysisOptions>();
            var modules = AnalyzeModules(context.Runtime);
            var heapStats = BuildModuleHeapStats(context.Cache, _options);
            return ValueTask.FromResult(BuildDomainResult(modules, heapStats).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime)
        {
            return Analyze(runtime, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "analyzing modules"));
            var modules = AnalyzeModules(runtime);
            var domainResult = BuildDomainResult(modules, heapStats: null);
            return domainResult;
        }

        private static ModuleDomainResult BuildDomainResult(ModuleAnalysis analysis, (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies)? heapStats)
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
                conflictDetails,
                heapStats?.TopByMemory,
                heapStats?.DensityAnomalies);
        }

        private static (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies)?
            BuildModuleHeapStats(IHeapAnalysisCache cache, DumpDetective.Core.Options.ModuleAnalysisOptions options)
        {
            // Requires a prebuilt heap index with module registry data.
            if (cache is not IHeapIndexBuilder builder || !builder.TryGetHeapIndex(out var index))
                return null;
            if (index.Modules is not { Count: > 0 } modules)
                return null;

            // Aggregate per-module: total bytes, object count, unique type count.
            var statsById = new Dictionary<int, MutableModuleStats>(modules.Count);
            foreach (var aggregate in index.TypeAggregates.Values)
            {
                int id = aggregate.ModuleId;
                if (id < 0) continue;

                if (!statsById.TryGetValue(id, out var s))
                {
                    s = new MutableModuleStats();
                    statsById[id] = s;
                }
                s.UniqueTypeCount++;
                s.ObjectCount += aggregate.Count;
                s.TotalBytes += aggregate.TotalSize;
            }

            int TopN = options.TopModulesByHeapCount;
            ulong DensityAnomalyMinBytes = options.DensityAnomalyMinBytes;
            int DensityAnomalyMaxTypes = options.DensityAnomalyMaxTypes;

            var topByMemory = new List<(ulong Bytes, int Id)>(statsById.Count);
            var densityList = new List<ModuleTypeDensity>();

            foreach ((int id, MutableModuleStats s) in statsById)
            {
                topByMemory.Add((s.TotalBytes, id));

                if (s.UniqueTypeCount <= DensityAnomalyMaxTypes && s.TotalBytes >= DensityAnomalyMinBytes)
                {
                    var mod = modules[id];
                    ulong bytesPerType = s.UniqueTypeCount > 0 ? s.TotalBytes / (ulong)s.UniqueTypeCount : s.TotalBytes;
                    densityList.Add(new ModuleTypeDensity(
                        System.IO.Path.GetFileName(mod.Name ?? string.Empty),
                        mod.AssemblyName ?? string.Empty,
                        s.UniqueTypeCount,
                        s.ObjectCount,
                        s.TotalBytes,
                        bytesPerType));
                }
            }

            topByMemory.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            var topStats = new List<ModuleHeapStats>(Math.Min(TopN, topByMemory.Count));
            for (int i = 0; i < topByMemory.Count && i < TopN; i++)
            {
                int id = topByMemory[i].Id;
                var mod = modules[id];
                var s = statsById[id];
                topStats.Add(new ModuleHeapStats(
                    System.IO.Path.GetFileName(mod.Name ?? string.Empty),
                    mod.AssemblyName ?? string.Empty,
                    s.UniqueTypeCount,
                    s.ObjectCount,
                    s.TotalBytes));
            }

            densityList.Sort((a, b) => b.BytesPerType.CompareTo(a.BytesPerType));

            return (topStats, densityList);
        }

        private sealed class MutableModuleStats
        {
            public int UniqueTypeCount;
            public long ObjectCount;
            public ulong TotalBytes;
        }

        private ModuleAnalysis AnalyzeModules(ClrRuntime runtime)
        {
            var analysis = new ModuleAnalysis();
            var modulesByName = new Dictionary<string, List<ModuleInfo>>();
            var processedModuleAddresses = new HashSet<ulong>();
            var scanCounter = new ObjectScanCounter("Module scan");

            foreach (var module in runtime.EnumerateModules())
            {
                scanCounter.Tick();

                if (module.Name == null)
                    continue;

                if (module.Address == 0 || !processedModuleAddresses.Add(module.Address))
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


