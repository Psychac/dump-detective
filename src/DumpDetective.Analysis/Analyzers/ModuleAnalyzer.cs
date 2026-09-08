using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class ModuleAnalyzer : IAnalyzer
    {
        public string Name => "Module Analysis";
        public string Category => "Modules";
        public IReadOnlyCollection<string> Tags => new[] { "modules", "runtime", "assemblies" };
        public int Order => 60;
        public bool IsThreadSafe => false;

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleAnalysisOptions options = context.AnalysisOptions.ModuleAnalysis;
            var modules = AnalyzeModules(context.Runtime, options, cancellationToken);
            var heapStats = BuildModuleHeapStats(context.Cache, context.Runtime, options);
            var appDomains = AnalyzeAppDomains(context.Runtime, context.Cache, cancellationToken);
            return ValueTask.FromResult(BuildDomainResult(modules, options, heapStats, appDomains).Stamp(this));
        }

        private static ModuleDomainResult BuildDomainResult(
            ModuleAnalysis analysis,
            ModuleAnalysisOptions options,
            (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies)? heapStats,
            AppDomainAnalysisResult appDomains)
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

            var topModules = new List<LoadedModuleSnapshot>(candidates.Count);
            foreach (ModuleInfo m in candidates)
                topModules.Add(new LoadedModuleSnapshot(m.Name, m.AssemblyName, m.FullPath, m.Address, m.Size, m.IsDynamic, m.IsPEFile, m.AssemblyLoadContextName, m.HasAssemblyLoadContext, m.IsCollectibleAssemblyLoadContext));

            // Conflict groups — preserve full per-instance detail for the printer, extract distinct versions.
            var conflictDetails = new List<ModuleConflictGroup>(analysis.VersionConflicts.Count);
            foreach (var kvp in analysis.VersionConflicts)
            {
                var instances = new List<LoadedModuleSnapshot>(kvp.Value.Count);
                var versionSet = new HashSet<string>();
                foreach (var m in kvp.Value)
                {
                    instances.Add(new LoadedModuleSnapshot(m.Name, m.AssemblyName, m.FullPath, m.Address, m.Size, m.IsDynamic, m.IsPEFile, m.AssemblyLoadContextName, m.HasAssemblyLoadContext, m.IsCollectibleAssemblyLoadContext));
                    ExtractVersionFromAssemblyName(m.AssemblyName, versionSet);
                }
                var versions = versionSet.Count > 0 ? versionSet.Order().ToList() : new List<string>();
                conflictDetails.Add(new ModuleConflictGroup(kvp.Key, instances, versions));
            }

            return new ModuleDomainResult(
                analysis.TotalModules,
                analysis.DynamicModules,
                analysis.ModulesByName.Count,
                analysis.VersionConflicts.Count,
                analysis.VersionConflicts.Keys.ToArray(),
                topModules,
                conflictDetails,
                options.HeavyModuleWarningThresholdBytes,
                analysis.UnknownIdentityDuplicates,
                heapStats?.TopByMemory,
                heapStats?.DensityAnomalies,
                TotalDomains: appDomains.TotalDomains,
                Domains: appDomains.Domains,
                TotalDynamicModules: appDomains.TotalDynamicModules,
                DynamicModuleBytes: appDomains.DynamicModuleBytes,
                AnonymousModuleCount: appDomains.AnonymousModuleCount,
                TopModulesByTypeCount: appDomains.TopModulesByTypeCount,
                CrossDomainModuleLoads: appDomains.CrossDomainModuleLoads,
                AssemblyRefVersionMismatches: analysis.AssemblyRefMismatches.Count > 0 ? analysis.AssemblyRefMismatches : null)
            {
                Warnings = MergeWarnings(analysis.Warnings, appDomains.Warnings)
            };
        }

        private static IReadOnlyList<string> MergeWarnings(List<string> moduleWarnings, IReadOnlyList<string> appDomainWarnings)
        {
            if (moduleWarnings.Count == 0)
                return appDomainWarnings;

            var merged = new List<string>(moduleWarnings.Count + appDomainWarnings.Count);
            merged.AddRange(moduleWarnings);
            merged.AddRange(appDomainWarnings);
            return merged;
        }

        private static AppDomainAnalysisResult AnalyzeAppDomains(
            ClrRuntime runtime,
            IHeapAnalysisCache? cache,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
                typeAggregates = heapIndex.TypeAggregates;

            IReadOnlyList<ClrAppDomain> appDomains = runtime.AppDomains;
            int totalDynamicModules = 0;
            ulong dynamicModuleBytes = 0;
            int anonymousModuleCount = 0;

            var domainSnapshots = new List<AppDomainSnapshot>(appDomains.Count);
            var warnings = new List<string>();

            // LiveTypeCount/ObjectCount/TotalBytes would all be empty without a heap index, so
            // skip the metadata walk entirely rather than doing wasted work.
            bool hasIndex = typeAggregates is not null;
            if (!hasIndex)
            {
                warnings.Add("TypeAggregates index not found; type enumeration will be skipped for all modules.");
            }

            // Per-module type-count accumulator: module address -> aggregate
            // Using a Dictionary keyed by module address so repeated module references
            // across domains are deduped (a module can appear in multiple domains).
            var moduleTypeData = new Dictionary<ulong, ModuleTypeAccumulator>(capacity: 256);

            // Cross-domain load tracking: same file name seen in more than one AppDomain means a
            // separate managed-memory copy per domain — keyed by file name since distinct domains
            // load their own ClrModule instance (own address) even for the identical assembly.
            var crossDomainData = new Dictionary<string, CrossDomainAccumulator>(capacity: 64);

            const int TopModulesPerDomainToShow = 8;

            foreach (ClrAppDomain domain in appDomains)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<ClrModule> modules = domain.Modules;

                int moduleCount = modules.Count;
                ulong domainManagedBytes = 0;
                var domainTopModules = new List<string>(capacity: Math.Min(moduleCount, TopModulesPerDomainToShow));

                // Sort modules by size descending so the per-domain narrative highlights the largest modules first.
                ClrModule[] sortedModules = new ClrModule[modules.Count];
                for (int i = 0; i < modules.Count; i++) sortedModules[i] = modules[i];
                Array.Sort(sortedModules, static (a, b) => b.Size.CompareTo(a.Size));

                foreach (ClrModule module in sortedModules)
                {
                    if (module.IsDynamic) totalDynamicModules++;
                    if (module.IsDynamic) dynamicModuleBytes += module.Size;
                    if (string.IsNullOrEmpty(module.Name)) anonymousModuleCount++;

                    if (domainTopModules.Count < TopModulesPerDomainToShow)
                    {
                        string moduleDisplay = Path.GetFileName(module.Name ?? string.Empty) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(moduleDisplay))
                            moduleDisplay = "<anonymous>";
                        if (module.IsDynamic)
                            moduleDisplay += " [dynamic]";

                        domainTopModules.Add(moduleDisplay);
                    }

                    if (!module.IsDynamic && !string.IsNullOrEmpty(module.Name))
                    {
                        string crossDomainName = Path.GetFileName(module.Name) ?? string.Empty;
                        if (!string.IsNullOrEmpty(crossDomainName) && !IsFrameworkModule(crossDomainName))
                        {
                            if (!crossDomainData.TryGetValue(crossDomainName, out CrossDomainAccumulator? acc2))
                            {
                                acc2 = new CrossDomainAccumulator(module.AssemblyName ?? "Unknown");
                                crossDomainData[crossDomainName] = acc2;
                            }
                            acc2.DomainIds.Add(domain.Id);
                            if (module.Size > acc2.Size)
                                acc2.Size = module.Size;
                        }
                    }

                    if (module.Address == 0) continue;

                    if (!moduleTypeData.TryGetValue(module.Address, out ModuleTypeAccumulator? acc))
                    {
                        string moduleName = Path.GetFileName(module.Name ?? string.Empty) ?? string.Empty;
                        string assemblyName = module.AssemblyName ?? "Unknown";
                        acc = new ModuleTypeAccumulator(moduleName, assemblyName);
                        moduleTypeData[module.Address] = acc;
                    }

                    if (!hasIndex) continue;

                    foreach ((ulong mt, int _) in module.EnumerateTypeDefToMethodTableMap())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (mt == 0) continue;

                        acc.TypeCount++;

                        if (typeAggregates!.TryGetValue(mt, out TypeAggregateIndexEntry entry) && entry.Count > 0)
                        {
                            acc.LiveTypeCount++;
                            acc.ObjectCount += entry.Count;
                            acc.TotalBytes += entry.TotalSize;
                            domainManagedBytes += entry.TotalSize;
                        }
                    }
                }

                domainSnapshots.Add(new AppDomainSnapshot(
                    Name: domain.Name ?? "<unnamed>",
                    Address: domain.Address,
                    DomainId: domain.Id,
                    ModuleCount: moduleCount,
                    EstimatedManagedBytes: domainManagedBytes,
                    TopModules: domainTopModules));
            }

            // Build TopModulesByTypeCount — sort by TypeCount descending.
            var moduleEntries = new List<ModuleTypeCountEntry>(moduleTypeData.Count);
            foreach (ModuleTypeAccumulator acc in moduleTypeData.Values)
            {
                moduleEntries.Add(new ModuleTypeCountEntry(
                    ModuleName: acc.ModuleName,
                    AssemblyName: acc.AssemblyName,
                    TypeCount: acc.TypeCount,
                    LiveTypeCount: acc.LiveTypeCount,
                    ObjectCount: acc.ObjectCount,
                    TotalBytes: acc.TotalBytes));
            }
            moduleEntries.Sort(static (a, b) => b.TypeCount.CompareTo(a.TypeCount));

            const int MaxCrossDomainModulesToReport = 20;
            var crossDomainLoads = new List<CrossDomainModuleLoad>();
            foreach (var kvp in crossDomainData)
            {
                if (kvp.Value.DomainIds.Count <= 1) continue;
                crossDomainLoads.Add(new CrossDomainModuleLoad(kvp.Key, kvp.Value.AssemblyName, kvp.Value.DomainIds.Count, kvp.Value.Size));
            }
            crossDomainLoads.Sort(static (a, b) =>
            {
                int byDomainCount = b.DomainCount.CompareTo(a.DomainCount);
                return byDomainCount != 0 ? byDomainCount : b.Size.CompareTo(a.Size);
            });
            if (crossDomainLoads.Count > MaxCrossDomainModulesToReport)
                crossDomainLoads.RemoveRange(MaxCrossDomainModulesToReport, crossDomainLoads.Count - MaxCrossDomainModulesToReport);

            return new AppDomainAnalysisResult(
                appDomains.Count,
                domainSnapshots,
                totalDynamicModules,
                dynamicModuleBytes,
                anonymousModuleCount,
                moduleEntries,
                warnings,
                crossDomainLoads);
        }

        private static (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies)?
            BuildModuleHeapStats(IHeapAnalysisCache cache, ClrRuntime runtime, ModuleAnalysisOptions options)
        {
            // Requires a prebuilt heap index with module registry data.
            if (cache is not IHeapIndexBuilder builder || !builder.TryGetHeapIndex(out var index))
                return null;
            if (index.Modules is not { Count: > 0 } modules)
                return null;

            // Name resolution is only invoked for the bounded top-N method tables of modules that
            // already cleared HeavyModuleWarningThresholdBytes — cheap even though it's a live ClrMD call.
            return ModuleAggregator.Aggregate(index.TypeAggregates, index.Modules, options,
                mt => runtime.GetTypeByMethodTable(mt)?.Name);
        }

        private ModuleAnalysis AnalyzeModules(ClrRuntime runtime, ModuleAnalysisOptions options, CancellationToken cancellationToken)
        {
            var analysis = new ModuleAnalysis();
            var modulesByName = new Dictionary<string, List<ModuleInfo>>();
            var processedModuleAddresses = new HashSet<ulong>();
            var moduleByAddress = new Dictionary<ulong, ClrModule>();

            // AssemblyRef audit: requires-vs-loaded version check across all modules. Bounded by
            // MaxModulesForAssemblyRefAudit since each entry costs a process-memory read + metadata parse.
            var pendingAssemblyRefs = new List<(string ModuleName, IReadOnlyList<AssemblyRefProbe.AssemblyRefEntry> Refs)>();
            int assemblyRefModulesScanned = 0;
            bool assemblyRefAuditCapped = false;

            // Scope-local string pool: deduplicates repeated path/name strings within this analysis pass.
            // Unlike string.Intern() this is GC'd when AnalyzeModules returns — safe for multi-dump scenarios.
            var stringPool = new Dictionary<string, string>(StringComparer.Ordinal);
            string Pool(string s) => stringPool.TryGetValue(s, out var c) ? c : (stringPool[s] = s);

            // Most modules share the same (default) AssemblyLoadContext object; cache by ALC address
            // so its name is read from the heap once instead of once per module.
            var alcNameCache = new Dictionary<ulong, string?>();
            ClrHeap heap = runtime.Heap;

            var scanCounter = new ObjectScanCounter("Module scan");

            foreach (var module in runtime.EnumerateModules())
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanCounter.Tick();

                if (module.Name == null)
                    continue;

                if (module.Address == 0 || !processedModuleAddresses.Add(module.Address))
                    continue;

                // keep a reference to the ClrModule for later in-memory manifest probing
                moduleByAddress[module.Address] = module;

                if (!module.IsDynamic)
                {
                    if (assemblyRefModulesScanned < options.MaxModulesForAssemblyRefAudit)
                    {
                        assemblyRefModulesScanned++;
                        var refs = AssemblyRefProbe.TryEnumerateAssemblyRefs(runtime.DataTarget.DataReader, module.MetadataAddress, module.MetadataLength);
                        if (refs.Count > 0)
                            pendingAssemblyRefs.Add((Path.GetFileName(module.Name), refs));
                    }
                    else
                    {
                        assemblyRefAuditCapped = true;
                    }
                }

                // Pool filename and assembly name — directory prefix segments repeat heavily across modules.
                string moduleName = Pool(Path.GetFileName(module.Name));
                string assemblyName = Pool(module.AssemblyName ?? "Unknown");

                bool hasAlc = TryResolveAssemblyLoadContext(runtime, heap, module, alcNameCache, out string? alcName, out bool isCollectibleAlc);

                var moduleInfo = new ModuleInfo
                {
                    Name = moduleName,
                    FullPath = Pool(module.Name),   // pool the full path: same dir prefix shared by many modules
                    AssemblyName = assemblyName,
                    Address = module.Address,
                    Size = module.Size,
                    IsDynamic = module.IsDynamic,
                    IsPEFile = module.IsPEFile,
                    AssemblyLoadContextName = alcName != null ? Pool(alcName) : null,
                    HasAssemblyLoadContext = hasAlc,
                    IsCollectibleAssemblyLoadContext = isCollectibleAlc
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

            if (assemblyRefAuditCapped)
            {
                analysis.Warnings.Add(
                    $"AssemblyRef version audit stopped after {options.MaxModulesForAssemblyRefAudit:N0} modules; " +
                    "results for remaining modules were skipped and may be incomplete.");
            }

            analysis.AssemblyRefMismatches = ComputeAssemblyRefMismatches(modulesByName, pendingAssemblyRefs, cancellationToken);

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
                    bool hasUnknownDuplicates = false;
                    foreach (var kv in identities)
                    {
                        var id = kv.Key;
                        bool isUnknown = string.IsNullOrEmpty(id.Version) && string.IsNullOrEmpty(id.PublicKeyToken) && string.IsNullOrEmpty(id.FileHash);
                        if (isUnknown && kv.Value.Count > 1)
                        {
                            hasUnknownDuplicates = true;
                            foreach (var mi in kv.Value)
                            {
                                if (!mi.AssemblyName.Contains("(Unknown identity)", StringComparison.Ordinal))
                                    mi.AssemblyName = mi.AssemblyName + " (Unknown identity)";
                            }
                        }
                    }
                    if (hasUnknownDuplicates)
                    {
                        analysis.UnknownIdentityDuplicates.Add(kvp.Key);
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

        private const int MaxAssemblyRefMismatchesToReport = 30;
        private const int MaxAssemblyRefsPerModuleToCheck = 200;

        // Cross-checks each module's AssemblyRef ("what version does this module require") table
        // against the versions actually loaded ("what's on the heap right now"). A mismatch means the
        // runtime silently unified/bound to a different version than what was originally referenced —
        // not necessarily a bug (binding redirects do this intentionally) but a signal worth surfacing.
        private static List<AssemblyRefVersionMismatch> ComputeAssemblyRefMismatches(
            Dictionary<string, List<ModuleInfo>> modulesByName,
            List<(string ModuleName, IReadOnlyList<AssemblyRefProbe.AssemblyRefEntry> Refs)> pendingAssemblyRefs,
            CancellationToken cancellationToken)
        {
            var mismatches = new List<AssemblyRefVersionMismatch>();
            if (pendingAssemblyRefs.Count == 0)
                return mismatches;

            var loadedAssemblyVersions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in modulesByName)
            {
                foreach (ModuleInfo moduleInfo in kvp.Value)
                {
                    (string? simpleName, string? version) = TryParseAssemblyNameAndVersion(moduleInfo.AssemblyName);
                    if (simpleName is null || string.IsNullOrEmpty(version))
                        continue;

                    if (!loadedAssemblyVersions.TryGetValue(simpleName, out HashSet<string>? versions))
                    {
                        versions = new HashSet<string>(StringComparer.Ordinal);
                        loadedAssemblyVersions[simpleName] = versions;
                    }
                    versions.Add(version);
                }
            }

            foreach ((string requiringModule, IReadOnlyList<AssemblyRefProbe.AssemblyRefEntry> refs) in pendingAssemblyRefs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int refsToCheck = Math.Min(refs.Count, MaxAssemblyRefsPerModuleToCheck);
                for (int i = 0; i < refsToCheck; i++)
                {
                    AssemblyRefProbe.AssemblyRefEntry entry = refs[i];
                    if (string.IsNullOrEmpty(entry.Version) || entry.Version == "0.0.0.0")
                        continue;

                    if (!loadedAssemblyVersions.TryGetValue(entry.Name, out HashSet<string>? loadedVersions) || loadedVersions.Count == 0)
                        continue;

                    if (loadedVersions.Contains(entry.Version))
                        continue;

                    var orderedLoadedVersions = new List<string>(loadedVersions);
                    orderedLoadedVersions.Sort(StringComparer.Ordinal);
                    mismatches.Add(new AssemblyRefVersionMismatch(
                        requiringModule, entry.Name, entry.Version, string.Join(", ", orderedLoadedVersions)));

                    if (mismatches.Count >= MaxAssemblyRefMismatchesToReport)
                        return mismatches;
                }
            }

            return mismatches;
        }

        private static (string? SimpleName, string? Version) TryParseAssemblyNameAndVersion(string assemblyDisplayName)
        {
            if (string.IsNullOrEmpty(assemblyDisplayName))
                return (null, null);

            try
            {
                var an = new System.Reflection.AssemblyName(assemblyDisplayName);
                return (an.Name, an.Version?.ToString());
            }
            catch
            {
                return (null, null);
            }
        }

        // Resolves the owning AssemblyLoadContext for a module by reading a live heap object — not via
        // reflection on ClrMD's own types. ClrType.AssemblyLoadContextAddress gives the address of the
        // runtime AssemblyLoadContext instance; its private "_name" field (null for the unnamed Default
        // context) is read with the public ClrObject.TryReadStringField API. Only one type per module is
        // probed since every type in a module shares the same load context. Returns false when no live
        // type could be resolved (e.g. an empty dynamic module), distinct from a resolved-but-unnamed ALC.
        private static bool TryResolveAssemblyLoadContext(
            ClrRuntime runtime,
            ClrHeap heap,
            ClrModule module,
            Dictionary<ulong, string?> alcNameCache,
            out string? name,
            out bool isCollectible)
        {
            name = null;
            isCollectible = false;

            ulong alcAddress = 0;
            foreach ((ulong methodTable, int _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (methodTable == 0)
                    continue;

                ClrType? type = runtime.GetTypeByMethodTable(methodTable);
                if (type == null)
                    continue;

                alcAddress = type.AssemblyLoadContextAddress;
                isCollectible = type.IsCollectible;
                break;
            }

            if (alcAddress == 0)
                return false;

            if (alcNameCache.TryGetValue(alcAddress, out string? cachedName))
            {
                name = cachedName;
                return true;
            }

            ClrObject alcObject = heap.GetObject(alcAddress);
            name = alcObject.IsValid && alcObject.TryReadStringField("_name", 256, out string? fieldValue)
                ? fieldValue
                : null;

            alcNameCache[alcAddress] = name;
            return true;
        }

        // Cross-domain loads of framework/BCL modules (System.*, mscorlib, coreclr, ...) are expected —
        // every AppDomain loads them — and are not a useful signal, unlike an application assembly
        // duplicated across domains.
        private static readonly string[] FrameworkModuleFileNames =
        {
            "mscorlib.dll", "netstandard.dll", "clrjit.dll", "coreclr.dll", "System.Private.CoreLib.dll",
            "WindowsBase.dll", "PresentationCore.dll", "PresentationFramework.dll"
        };

        private static bool IsFrameworkModule(string moduleFileName) =>
            moduleFileName.StartsWith("System.", StringComparison.Ordinal) ||
            moduleFileName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            moduleFileName.StartsWith("Windows.", StringComparison.Ordinal) ||
            moduleFileName.StartsWith("Mono.", StringComparison.Ordinal) ||
            Array.IndexOf(FrameworkModuleFileNames, moduleFileName) >= 0;

        private static void ExtractVersionFromAssemblyName(string assemblyName, HashSet<string> versionSet)
        {
            if (string.IsNullOrEmpty(assemblyName))
                return;

            int versionStart = assemblyName.IndexOf("Version=", StringComparison.OrdinalIgnoreCase);
            if (versionStart == -1)
                return;

            versionStart += 8;
            int versionEnd = assemblyName.IndexOf(',', versionStart);
            if (versionEnd == -1)
                versionEnd = assemblyName.Length;

            string version = assemblyName.Substring(versionStart, versionEnd - versionStart).Trim();
            if (!string.IsNullOrEmpty(version))
                versionSet.Add(version);
        }

        public void Dispose() { }
    }

    internal class ModuleAnalysis
    {
        public int TotalModules { get; set; }
        public int DynamicModules { get; set; }
        public Dictionary<string, List<ModuleInfo>> ModulesByName { get; set; } = new();
        public Dictionary<string, List<ModuleInfo>> VersionConflicts { get; set; } = new();
        public HashSet<string> UnknownIdentityDuplicates { get; set; } = new();
        public List<AssemblyRefVersionMismatch> AssemblyRefMismatches { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
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
        public string? AssemblyLoadContextName { get; set; }
        public bool HasAssemblyLoadContext { get; set; }
        public bool IsCollectibleAssemblyLoadContext { get; set; }
    }

    internal sealed class ModuleTypeAccumulator
    {
        public readonly string ModuleName;
        public readonly string AssemblyName;
        public int TypeCount;
        public int LiveTypeCount;
        public long ObjectCount;
        public ulong TotalBytes;

        public ModuleTypeAccumulator(string moduleName, string assemblyName)
        {
            ModuleName = moduleName;
            AssemblyName = assemblyName;
        }
    }

    internal sealed record AppDomainAnalysisResult(
        int TotalDomains,
        IReadOnlyList<AppDomainSnapshot> Domains,
        int TotalDynamicModules,
        ulong DynamicModuleBytes,
        int AnonymousModuleCount,
        IReadOnlyList<ModuleTypeCountEntry> TopModulesByTypeCount,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<CrossDomainModuleLoad> CrossDomainModuleLoads);

    internal sealed class CrossDomainAccumulator
    {
        public readonly string AssemblyName;
        public readonly HashSet<int> DomainIds = new();
        public ulong Size;

        public CrossDomainAccumulator(string assemblyName)
        {
            AssemblyName = assemblyName;
        }
    }
}


