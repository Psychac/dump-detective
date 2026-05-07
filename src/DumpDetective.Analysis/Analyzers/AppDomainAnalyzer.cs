using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer covering §18.1 (AppDomain inventory) and §18.3 (type density per module).
    ///
    /// All data comes from <c>ClrRuntime.AppDomains</c> + a <c>TypeAggregates</c> MT-lookup join.
    /// No heap enumeration is performed.
    ///
    /// Type enumeration via <c>ClrModule.EnumerateTypeDefToMethodTableMap()</c> is bounded to the top
    /// <see cref="ModuleEnumerationLimit"/> modules per domain (ranked by <c>ClrModule.Size</c>)
    /// to keep the analysis bounded on large runtimes with hundreds of small modules.
    /// </summary>
    public sealed class AppDomainAnalyzer : IAnalyzer
    {
        public string Name     => "AppDomain Analysis";
        public string Category => "Modules";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppDomainAnalysisOptions options = context.GetOption<AppDomainAnalysisOptions>();
            return ValueTask.FromResult(
                Analyze(context.Runtime, context.Cache, options, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrRuntime runtime,
            IHeapAnalysisCache? cache,
            AppDomainAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            // Resolve TypeAggregates once; may be null when no index was built.
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
                typeAggregates = heapIndex.TypeAggregates;

            IReadOnlyList<ClrAppDomain> appDomains = runtime.AppDomains;
            int totalDynamicModules   = 0;
            int anonymousModuleCount  = 0;

            var domainSnapshots = new List<AppDomainSnapshot>(appDomains.Count);
            var warnings = new List<string>();
            int totalExcludedModules = 0;

            if (options.PreferIndexOnly && typeAggregates is null)
            {
                warnings.Add("TypeAggregates index not found; enumeration will be skipped due to PreferIndexOnly setting.");
            }

            // Per-module type-count accumulator: module address → aggregate
            // Using a Dictionary keyed by module address so repeated module references
            // across domains are deduped (a module can appear in multiple domains).
            var moduleTypeData = new Dictionary<ulong, ModuleTypeAccumulator>(capacity: 256);

            foreach (ClrAppDomain domain in appDomains)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<ClrModule> modules = domain.Modules;

                int moduleCount        = modules.Count;
                ulong domainManagedBytes = 0;

                // Select modules according to selection mode. Default: TopBySize (existing behavior).
                ClrModule[]? sortedModules = null;
                if (options.ModuleSelectionMode == ModuleSelectionMode.TopBySize || options.ModuleSelectionMode == ModuleSelectionMode.TopByTypeCount)
                {
                    // Sort by size descending as a practical default ordering.
                    sortedModules = new ClrModule[modules.Count];
                    for (int i = 0; i < modules.Count; i++) sortedModules[i] = modules[i];
                    Array.Sort(sortedModules, static (a, b) => b.Size.CompareTo(a.Size));
                }

                int enumerationBound = Math.Min(modules.Count, options.ModuleEnumerationLimit);
                if (modules.Count > enumerationBound)
                {
                    totalExcludedModules += modules.Count - enumerationBound;
                    if (options.EmitTruncationNotice)
                        warnings.Add($"Domain '{domain.Name ?? "<unnamed>"}' module enumeration truncated to {enumerationBound} of {modules.Count} modules.");
                }

                for (int mi = 0; mi < enumerationBound; mi++)
                {
                    ClrModule module = sortedModules is not null ? sortedModules[mi] : modules[mi];

                    if (module.IsDynamic) totalDynamicModules++;
                    if (string.IsNullOrEmpty(module.Name)) anonymousModuleCount++;

                    if (module.Address == 0) continue;

                    if (!moduleTypeData.TryGetValue(module.Address, out ModuleTypeAccumulator? acc))
                    {
                        string moduleName   = Path.GetFileName(module.Name ?? string.Empty) ?? string.Empty;
                        string assemblyName = module.AssemblyName ?? "Unknown";
                        acc = new ModuleTypeAccumulator(moduleName, assemblyName);
                        moduleTypeData[module.Address] = acc;
                    }

                    // Determine whether to enumerate types for this module.
                    bool hasIndex = typeAggregates is not null;
                    bool shouldEnumerate = options.TypeEnumerationMode != TypeEnumerationMode.Skip && (hasIndex || !options.PreferIndexOnly);
                    if (!shouldEnumerate) continue;

                    // Enumerate (full or sampled) — sampling bounds are conservative to keep memory/CPU bounded.
                    const int SampleBudgetPerModule = 1024;
                    int seen = 0;

                    foreach ((ulong mt, int _) in module.EnumerateTypeDefToMethodTableMap())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (mt == 0) continue;

                        acc.TypeCount++;

                        if (hasIndex && typeAggregates!.TryGetValue(mt, out TypeAggregateIndexEntry entry) && entry.Count > 0)
                        {
                            acc.LiveTypeCount++;
                            acc.ObjectCount   += entry.Count;
                            acc.TotalBytes    += entry.TotalSize;
                            domainManagedBytes += entry.TotalSize;
                        }

                        seen++;
                        if (options.TypeEnumerationMode == TypeEnumerationMode.Sampled && seen >= SampleBudgetPerModule)
                            break;
                    }
                }

                domainSnapshots.Add(new AppDomainSnapshot(
                    Name:                  domain.Name ?? "<unnamed>",
                    Address:               domain.Address,
                    DomainId:              domain.Id,
                    ModuleCount:           moduleCount,
                    EstimatedManagedBytes: domainManagedBytes));
            }

            // Build TopModulesByTypeCount — sort by TypeCount descending, cap at limit
            var moduleEntries = new List<ModuleTypeCountEntry>(moduleTypeData.Count);
            foreach (ModuleTypeAccumulator acc in moduleTypeData.Values)
            {
                moduleEntries.Add(new ModuleTypeCountEntry(
                    ModuleName:    acc.ModuleName,
                    AssemblyName:  acc.AssemblyName,
                    TypeCount:     acc.TypeCount,
                    LiveTypeCount: acc.LiveTypeCount,
                    ObjectCount:   acc.ObjectCount,
                    TotalBytes:    acc.TotalBytes));
            }
            moduleEntries.Sort(static (a, b) => b.TypeCount.CompareTo(a.TypeCount));

            int topLimit = Math.Min(moduleEntries.Count, options.TopModuleTypeCountLimit);
            var topModules = new List<ModuleTypeCountEntry>(topLimit);
            for (int i = 0; i < topLimit; i++) topModules.Add(moduleEntries[i]);

            var result = new AppDomainDomainResult(
                TotalDomains:         appDomains.Count,
                Domains:              domainSnapshots,
                TotalDynamicModules:  totalDynamicModules,
                AnonymousModuleCount: anonymousModuleCount,
                TopModulesByTypeCount: topModules)
                with { Warnings = warnings, Metrics = new Dictionary<string, object?> { ["ExcludedModuleCount"] = totalExcludedModules } };

            return result;
        }

        // ── Mutable accumulator (local use only) ──────────────────────────────
        private sealed class ModuleTypeAccumulator
        {
            public readonly string ModuleName;
            public readonly string AssemblyName;
            public int   TypeCount;
            public int   LiveTypeCount;
            public long  ObjectCount;
            public ulong TotalBytes;

            public ModuleTypeAccumulator(string moduleName, string assemblyName)
            {
                ModuleName   = moduleName;
                AssemblyName = assemblyName;
            }
        }
        
        public void Dispose() { }
    }
}
