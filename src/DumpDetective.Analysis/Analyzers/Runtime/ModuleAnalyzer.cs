using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Runtime;

/// <summary>
/// Analyzes loaded modules: inventory, version conflicts, AssemblyRef-vs-loaded version audit,
/// per-AppDomain module/type breakdown, cross-domain loads, and per-module heap memory footprint.
/// </summary>
/// <remarks>
/// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) —
/// scoped once before (Batch 8, 2026-09-11) and deferred at the time because
/// <c>dump.object-fields</c> didn't exist yet and this analyzer's own AppDomain/module surface
/// wasn't designed. Both are resolved here: the ALC-name read goes through the now-existing
/// <see cref="IDumpObjectFieldQuery.TryReadField"/> (no new Tier-2 primitive needed), and the
/// AppDomain/module surface is <see cref="IRuntimeModuleQuery"/>'s first real implementation
/// (redesigned from its speculative, zero-consumer shipped shape — see that interface's own
/// remarks).
///
/// <b>No bespoke scan participant needed</b>: unlike <c>StringAnalyzer</c>/<c>CrashAnalyzer</c>/
/// <c>HangAnalyzer</c>, nothing here needs data accumulated during a shared heap-index or
/// thread-stack scan pass — every fact comes from <see cref="IRuntimeModuleQuery"/> or the
/// already-available <see cref="IHeapTypeStatisticsQuery"/>, so <c>ModuleAnalyzerLegacyAdapter</c>
/// is a plain adapter.
///
/// <b>A real, evidence-backed simplification</b>: the pre-retyping analyzer's per-module heap
/// footprint (<c>ModuleAggregator</c>) joined <c>HeapIndexBuildResult.Modules</c> (a Phase-1
/// disk-index module registry) against <c>TypeAggregateIndexEntry</c> by <c>ModuleId</c>. That
/// whole join is replaced by grouping <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/> by
/// <see cref="HeapTypeStatistics.ModuleName"/> — confirmed (not assumed) to resolve to the exact
/// same <c>Path.GetFileName(ClrType.Module.Name)</c> normalization the old join produced, by
/// reading <c>TypeAggregateNameResolver.ResolveModuleName</c> directly. This also means the
/// heavy-module top-types breakdown no longer needs a separate method-table-to-name resolution
/// pass: <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/>'s dictionary key already *is* the
/// resolved type name.
/// </remarks>
[AnalyzerModule(key: "module", displayName: "Module Analysis", order: 160, tags: ["runtime"])]
public sealed class ModuleAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "Module Analysis";
    public string Category => "Modules";

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    // Only the heaviest modules (>= HeavyModuleWarningThresholdBytes) get a type breakdown -- keeps
    // the bounded top-N insertion cost proportional to a small module subset, not all modules.
    private const int TopTypesPerHeavyModuleToShow = 10;
    private const int TopModulesPerDomainToShow = 8;
    private const int MaxCrossDomainModulesToReport = 20;
    private const int MaxAssemblyRefMismatchesToReport = 30;
    private const int MaxAssemblyRefsPerModuleToCheck = 200;

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IRuntimeModuleQuery modules = context.RuntimeModules
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.RuntimeModules}' capability.");
        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");

        ModuleAnalysisOptions options = context.AnalyzerOptions as ModuleAnalysisOptions ?? new ModuleAnalysisOptions();

        LastResult = Analyze(modules, fields, typeStatistics, options, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static ModuleDomainResult Analyze(
        IRuntimeModuleQuery modules, IDumpObjectFieldQuery fields, IHeapTypeStatisticsQuery typeStatistics,
        ModuleAnalysisOptions options, CancellationToken cancellationToken)
    {
        (ModuleAnalysisState state, List<ModuleFacts> inventory) = AnalyzeModuleInventory(modules, fields, options, cancellationToken);
        AppDomainAnalysisResult appDomains = AnalyzeAppDomains(modules, typeStatistics, cancellationToken);

        var assemblyNameByModuleName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ModuleFacts m in inventory)
            assemblyNameByModuleName.TryAdd(m.Name, m.AssemblyName);

        (IReadOnlyList<ModuleHeapStats> topByMemory, IReadOnlyList<ModuleTypeDensity> densityAnomalies) =
            AggregateModuleHeapStats(typeStatistics, options, assemblyNameByModuleName);

        return BuildDomainResult(state, inventory, options, topByMemory, densityAnomalies, appDomains);
    }

    // ── Module inventory + version-conflict + AssemblyRef audit ─────────────────────────────────

    private readonly record struct ModuleFacts(
        string Name, string FullPath, string AssemblyName, ulong Address, ulong Size, bool IsDynamic, bool IsPEFile,
        string? AssemblyLoadContextName, bool HasAssemblyLoadContext, bool IsCollectibleAssemblyLoadContext);

    private sealed class ModuleAnalysisState
    {
        public int TotalModules;
        public int DynamicModules;
        public readonly Dictionary<string, List<ModuleFacts>> ModulesByName = new();
        public readonly Dictionary<string, List<ModuleFacts>> VersionConflicts = new();
        public readonly HashSet<string> UnknownIdentityDuplicates = new();
        public List<AssemblyRefVersionMismatch> AssemblyRefMismatches = new();
        public readonly List<string> Warnings = new();
    }

    private static (ModuleAnalysisState State, List<ModuleFacts> Inventory) AnalyzeModuleInventory(
        IRuntimeModuleQuery modules, IDumpObjectFieldQuery fields, ModuleAnalysisOptions options, CancellationToken cancellationToken)
    {
        var state = new ModuleAnalysisState();
        var inventory = new List<ModuleFacts>();
        var alcNameCache = new Dictionary<ulong, string?>();

        var pendingAssemblyRefs = new List<(string ModuleName, IReadOnlyList<(string Name, string Version)> Refs)>();
        int assemblyRefModulesScanned = 0;
        bool assemblyRefAuditCapped = false;

        foreach (RuntimeModuleInfo module in modules.EnumerateModules())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!module.IsDynamic)
            {
                if (assemblyRefModulesScanned < options.MaxModulesForAssemblyRefAudit)
                {
                    assemblyRefModulesScanned++;
                    IReadOnlyList<(string Name, string Version)> refs = modules.GetAssemblyReferences(module.Address);
                    if (refs.Count > 0)
                        pendingAssemblyRefs.Add((module.Name, refs));
                }
                else
                {
                    assemblyRefAuditCapped = true;
                }
            }

            string? alcName = ResolveAssemblyLoadContextName(modules, fields, module.Address, alcNameCache, out bool hasAlc, out bool isCollectibleAlc);

            var facts = new ModuleFacts(
                module.Name, module.FullPath, module.AssemblyName, module.Address, module.Size, module.IsDynamic, module.IsPEFile,
                alcName, hasAlc, isCollectibleAlc);
            inventory.Add(facts);

            if (!state.ModulesByName.TryGetValue(module.Name, out List<ModuleFacts>? list))
                state.ModulesByName[module.Name] = list = new List<ModuleFacts>();
            list.Add(facts);

            state.TotalModules++;
            if (module.IsDynamic)
                state.DynamicModules++;
        }

        if (assemblyRefAuditCapped)
        {
            state.Warnings.Add(
                $"AssemblyRef version audit stopped after {options.MaxModulesForAssemblyRefAudit:N0} modules; " +
                "results for remaining modules were skipped and may be incomplete.");
        }

        state.AssemblyRefMismatches = ComputeAssemblyRefMismatches(state.ModulesByName, pendingAssemblyRefs, cancellationToken);

        foreach (KeyValuePair<string, List<ModuleFacts>> kvp in state.ModulesByName)
        {
            if (kvp.Value.Count <= 1)
                continue;

            // Conflict means the same module file name appears with different assembly identities.
            var identities = new Dictionary<AssemblyIdentity, List<ModuleFacts>>(new AssemblyIdentityComparer());
            foreach (ModuleFacts moduleFacts in kvp.Value)
            {
                AssemblyIdentity identity = ModuleProbe.ProbeAssemblyIdentity(moduleFacts.FullPath, moduleFacts.AssemblyName);
                if (!identities.TryGetValue(identity, out List<ModuleFacts>? list))
                    identities[identity] = list = new List<ModuleFacts>();
                list.Add(moduleFacts);
            }

            int knownCount = 0;
            foreach (KeyValuePair<AssemblyIdentity, List<ModuleFacts>> kv in identities)
                if (!IsUnknownIdentity(kv.Key)) knownCount++;

            if (knownCount > 1)
            {
                state.VersionConflicts[kvp.Key] = kvp.Value;
            }
            else
            {
                bool hasUnknownDuplicates = false;
                foreach (KeyValuePair<AssemblyIdentity, List<ModuleFacts>> kv in identities)
                {
                    if (IsUnknownIdentity(kv.Key) && kv.Value.Count > 1)
                        hasUnknownDuplicates = true;
                }
                if (hasUnknownDuplicates)
                    state.UnknownIdentityDuplicates.Add(kvp.Key);
            }
        }

        return (state, inventory);
    }

    private static bool IsUnknownIdentity(AssemblyIdentity id) =>
        string.IsNullOrEmpty(id.Version) && string.IsNullOrEmpty(id.PublicKeyToken) && string.IsNullOrEmpty(id.FileHash);

    /// <summary>
    /// Resolves the owning AssemblyLoadContext's display name for a module — structural resolution
    /// (which ALC address, whether it's collectible) via <see cref="IRuntimeModuleQuery.TryResolveAssemblyLoadContext"/>,
    /// then the ALC object's own <c>"_name"</c> field value via <see cref="IDumpObjectFieldQuery.TryReadField"/>
    /// (Tier 2 — a genuine per-object field read). Cached by ALC address, mirroring the pre-retyping
    /// analyzer's own <c>alcNameCache</c> — most modules share the same (default) load context.
    /// </summary>
    private static string? ResolveAssemblyLoadContextName(
        IRuntimeModuleQuery modules, IDumpObjectFieldQuery fields, ulong moduleAddress,
        Dictionary<ulong, string?> alcNameCache, out bool hasAlc, out bool isCollectible)
    {
        hasAlc = modules.TryResolveAssemblyLoadContext(moduleAddress, out ulong alcAddress, out isCollectible);
        if (!hasAlc)
            return null;

        if (alcNameCache.TryGetValue(alcAddress, out string? cachedName))
            return cachedName;

        string? name = fields.TryReadField(alcAddress, "_name", out FieldValue value) && value.Kind == FieldValueKind.String
            ? value.AsString()
            : null;

        alcNameCache[alcAddress] = name;
        return name;
    }

    private static List<AssemblyRefVersionMismatch> ComputeAssemblyRefMismatches(
        Dictionary<string, List<ModuleFacts>> modulesByName,
        List<(string ModuleName, IReadOnlyList<(string Name, string Version)> Refs)> pendingAssemblyRefs,
        CancellationToken cancellationToken)
    {
        var mismatches = new List<AssemblyRefVersionMismatch>();
        if (pendingAssemblyRefs.Count == 0)
            return mismatches;

        var loadedAssemblyVersions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<ModuleFacts>> kvp in modulesByName)
        {
            foreach (ModuleFacts moduleFacts in kvp.Value)
            {
                (string? simpleName, string? version) = TryParseAssemblyNameAndVersion(moduleFacts.AssemblyName);
                if (simpleName is null || string.IsNullOrEmpty(version))
                    continue;

                if (!loadedAssemblyVersions.TryGetValue(simpleName, out HashSet<string>? versions))
                    loadedAssemblyVersions[simpleName] = versions = new HashSet<string>(StringComparer.Ordinal);
                versions.Add(version);
            }
        }

        foreach ((string requiringModule, IReadOnlyList<(string Name, string Version)> refs) in pendingAssemblyRefs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int refsToCheck = Math.Min(refs.Count, MaxAssemblyRefsPerModuleToCheck);
            for (int i = 0; i < refsToCheck; i++)
            {
                (string refName, string refVersion) = refs[i];
                if (string.IsNullOrEmpty(refVersion) || refVersion == "0.0.0.0")
                    continue;

                if (!loadedAssemblyVersions.TryGetValue(refName, out HashSet<string>? loadedVersions) || loadedVersions.Count == 0)
                    continue;

                if (loadedVersions.Contains(refVersion))
                    continue;

                var orderedLoadedVersions = new List<string>(loadedVersions);
                orderedLoadedVersions.Sort(StringComparer.Ordinal);
                mismatches.Add(new AssemblyRefVersionMismatch(requiringModule, refName, refVersion, string.Join(", ", orderedLoadedVersions)));

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

    // ── Per-AppDomain analysis + cross-domain loads ──────────────────────────────────────────────

    private sealed class ModuleTypeAccumulator
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

    private sealed class CrossDomainAccumulator
    {
        public readonly string AssemblyName;
        public readonly HashSet<int> DomainIds = new();
        public ulong Size;

        public CrossDomainAccumulator(string assemblyName)
        {
            AssemblyName = assemblyName;
        }
    }

    private static AppDomainAnalysisResult AnalyzeAppDomains(
        IRuntimeModuleQuery modules, IHeapTypeStatisticsQuery typeStatistics, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, HeapTypeStatistics> stats = typeStatistics.GetTypeStatistics();
        bool hasStats = stats.Count > 0;

        var domainSnapshots = new List<AppDomainSnapshot>();
        var warnings = new List<string>();
        if (!hasStats)
            warnings.Add("Type statistics unavailable; per-module live-type/object/byte counts will be zero.");

        int totalDynamicModules = 0;
        ulong dynamicModuleBytes = 0;
        int anonymousModuleCount = 0;

        var moduleTypeData = new Dictionary<ulong, ModuleTypeAccumulator>(capacity: 256);
        var crossDomainData = new Dictionary<string, CrossDomainAccumulator>(capacity: 64);

        int domainCount = 0;
        foreach (AppDomainInfo domain in modules.EnumerateAppDomains())
        {
            cancellationToken.ThrowIfCancellationRequested();
            domainCount++;

            var domainModules = new List<RuntimeModuleInfo>(modules.EnumerateModules(domain));
            domainModules.Sort(static (a, b) => b.Size.CompareTo(a.Size));

            var domainTopModules = new List<string>(Math.Min(TopModulesPerDomainToShow, domainModules.Count));
            ulong domainBytes = 0;

            foreach (RuntimeModuleInfo module in domainModules)
            {
                if (module.IsDynamic) { totalDynamicModules++; dynamicModuleBytes += module.Size; }
                if (string.IsNullOrEmpty(module.Name)) anonymousModuleCount++;

                if (domainTopModules.Count < TopModulesPerDomainToShow)
                {
                    string moduleDisplay = string.IsNullOrWhiteSpace(module.Name) ? "<anonymous>" : module.Name;
                    if (module.IsDynamic) moduleDisplay += " [dynamic]";
                    domainTopModules.Add(moduleDisplay);
                }

                if (!module.IsDynamic && !string.IsNullOrEmpty(module.Name) && !IsFrameworkModule(module.Name))
                {
                    if (!crossDomainData.TryGetValue(module.Name, out CrossDomainAccumulator? acc))
                        crossDomainData[module.Name] = acc = new CrossDomainAccumulator(module.AssemblyName);
                    acc.DomainIds.Add(domain.DomainId);
                    if (module.Size > acc.Size) acc.Size = module.Size;
                }

                if (module.Address == 0) continue;

                if (!moduleTypeData.TryGetValue(module.Address, out ModuleTypeAccumulator? typeAcc))
                    moduleTypeData[module.Address] = typeAcc = new ModuleTypeAccumulator(module.Name, module.AssemblyName);

                foreach (string typeName in modules.EnumerateDeclaredTypeNames(module.Address))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    typeAcc.TypeCount++;

                    if (hasStats && stats.TryGetValue(typeName, out HeapTypeStatistics entry) && entry.InstanceCount > 0)
                    {
                        typeAcc.LiveTypeCount++;
                        typeAcc.ObjectCount += entry.InstanceCount;
                        typeAcc.TotalBytes += entry.TotalSize;
                    }
                }

                domainBytes += typeAcc.TotalBytes;
            }

            domainSnapshots.Add(new AppDomainSnapshot(
                Name: domain.Name, Address: domain.Address, DomainId: domain.DomainId,
                ModuleCount: domainModules.Count, EstimatedManagedBytes: domainBytes, TopModules: domainTopModules));
        }

        var moduleEntries = new List<ModuleTypeCountEntry>(moduleTypeData.Count);
        foreach (ModuleTypeAccumulator acc in moduleTypeData.Values)
            moduleEntries.Add(new ModuleTypeCountEntry(acc.ModuleName, acc.AssemblyName, acc.TypeCount, acc.LiveTypeCount, acc.ObjectCount, acc.TotalBytes));
        moduleEntries.Sort(static (a, b) => b.TypeCount.CompareTo(a.TypeCount));

        var crossDomainLoads = new List<CrossDomainModuleLoad>();
        foreach (KeyValuePair<string, CrossDomainAccumulator> kvp in crossDomainData)
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
            domainCount, domainSnapshots, totalDynamicModules, dynamicModuleBytes, anonymousModuleCount,
            moduleEntries, warnings, crossDomainLoads);
    }

    // Cross-domain loads of framework/BCL modules (System.*, mscorlib, coreclr, ...) are expected --
    // every AppDomain loads them -- and are not a useful signal, unlike an application assembly
    // duplicated across domains.
    private static readonly string[] FrameworkModuleFileNames =
    {
        "mscorlib.dll", "netstandard.dll", "clrjit.dll", "coreclr.dll", "System.Private.CoreLib.dll",
        "WindowsBase.dll", "PresentationCore.dll", "PresentationFramework.dll",
    };

    private static bool IsFrameworkModule(string moduleFileName) =>
        moduleFileName.StartsWith("System.", StringComparison.Ordinal) ||
        moduleFileName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        moduleFileName.StartsWith("Windows.", StringComparison.Ordinal) ||
        moduleFileName.StartsWith("Mono.", StringComparison.Ordinal) ||
        Array.IndexOf(FrameworkModuleFileNames, moduleFileName) >= 0;

    private sealed record AppDomainAnalysisResult(
        int TotalDomains, IReadOnlyList<AppDomainSnapshot> Domains, int TotalDynamicModules, ulong DynamicModuleBytes,
        int AnonymousModuleCount, IReadOnlyList<ModuleTypeCountEntry> TopModulesByTypeCount, IReadOnlyList<string> Warnings,
        IReadOnlyList<CrossDomainModuleLoad> CrossDomainModuleLoads);

    // ── Per-module heap footprint (replaces ModuleAggregator's ModuleId join) ────────────────────

    private static (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies)
        AggregateModuleHeapStats(IHeapTypeStatisticsQuery typeStatistics, ModuleAnalysisOptions options,
        IReadOnlyDictionary<string, string> assemblyNameByModuleName)
    {
        IReadOnlyDictionary<string, HeapTypeStatistics> stats = typeStatistics.GetTypeStatistics();

        var byModule = new Dictionary<string, (string AssemblyName, int UniqueTypes, long ObjectCount, ulong TotalBytes, ulong LohBytes, long Gen2ObjectCount)>(StringComparer.Ordinal);

        // Pass 1: per-module aggregate totals.
        foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
        {
            HeapTypeStatistics entry = kv.Value;
            if (string.IsNullOrEmpty(entry.ModuleName) || entry.ModuleName == "N/A")
                continue;

            if (!byModule.TryGetValue(entry.ModuleName, out var agg))
            {
                assemblyNameByModuleName.TryGetValue(entry.ModuleName, out string? assemblyName);
                agg = (assemblyName ?? "Unknown", 0, 0L, 0UL, 0UL, 0L);
            }
            agg.UniqueTypes++;
            agg.ObjectCount += entry.InstanceCount;
            agg.TotalBytes += entry.TotalSize;
            agg.LohBytes += entry.LohSize;
            agg.Gen2ObjectCount += entry.Gen2Count;
            byModule[entry.ModuleName] = agg;
        }

        // Pass 2: heavy modules (by their aggregate total, not any single type's own size) get a
        // top-N types-by-bytes breakdown -- requires the module totals from pass 1 to know which
        // modules qualify before walking the per-type entries again.
        var heavyModuleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, (string AssemblyName, int UniqueTypes, long ObjectCount, ulong TotalBytes, ulong LohBytes, long Gen2ObjectCount)> kv in byModule)
            if (kv.Value.TotalBytes >= options.HeavyModuleWarningThresholdBytes)
                heavyModuleNames.Add(kv.Key);

        var typesByModule = new Dictionary<string, List<(string TypeName, long Count, ulong TotalSize)>>(StringComparer.Ordinal);
        if (heavyModuleNames.Count > 0)
        {
            foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
            {
                HeapTypeStatistics entry = kv.Value;
                if (!heavyModuleNames.Contains(entry.ModuleName))
                    continue;

                if (!typesByModule.TryGetValue(entry.ModuleName, out List<(string, long, ulong)>? typeList))
                    typesByModule[entry.ModuleName] = typeList = new List<(string, long, ulong)>(TopTypesPerHeavyModuleToShow);
                InsertTopType(typeList, TopTypesPerHeavyModuleToShow, kv.Key, entry.InstanceCount, entry.TotalSize);
            }
        }

        var topByMemory = new List<ModuleHeapStats>(byModule.Count);
        var density = new List<ModuleTypeDensity>();
        foreach (KeyValuePair<string, (string AssemblyName, int UniqueTypes, long ObjectCount, ulong TotalBytes, ulong LohBytes, long Gen2ObjectCount)> kv in byModule)
        {
            var s = kv.Value;

            IReadOnlyList<ModuleTypeUsage>? topTypes = null;
            if (typesByModule.TryGetValue(kv.Key, out List<(string TypeName, long Count, ulong TotalSize)>? typeList))
            {
                var usages = new List<ModuleTypeUsage>(typeList.Count);
                foreach ((string typeName, long count, ulong totalSize) in typeList)
                    usages.Add(new ModuleTypeUsage(typeName, count, totalSize));
                topTypes = usages;
            }

            topByMemory.Add(new ModuleHeapStats(kv.Key, s.AssemblyName, s.UniqueTypes, s.ObjectCount, s.TotalBytes, s.LohBytes, s.Gen2ObjectCount, topTypes));

            if (s.UniqueTypes <= options.DensityAnomalyMaxTypes && s.TotalBytes >= options.DensityAnomalyMinBytes)
            {
                ulong bytesPerType = s.UniqueTypes > 0 ? s.TotalBytes / (ulong)s.UniqueTypes : s.TotalBytes;
                density.Add(new ModuleTypeDensity(kv.Key, s.AssemblyName, s.UniqueTypes, s.ObjectCount, s.TotalBytes, bytesPerType));
            }
        }

        topByMemory.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        density.Sort(static (a, b) => b.BytesPerType.CompareTo(a.BytesPerType));
        return (topByMemory, density);
    }

    // Maintains a small (<= cap) descending-by-TotalSize list. Re-sorting on every insert is fine
    // at cap=10: O(cap log cap), and only heavy modules' types reach this path.
    private static void InsertTopType(List<(string TypeName, long Count, ulong TotalSize)> list, int cap, string typeName, long count, ulong totalSize)
    {
        if (list.Count < cap)
        {
            list.Add((typeName, count, totalSize));
            list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
            return;
        }

        if (totalSize <= list[^1].TotalSize) return;
        list[^1] = (typeName, count, totalSize);
        list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
    }

    // ── Domain-result assembly ───────────────────────────────────────────────────────────────────

    private static ModuleDomainResult BuildDomainResult(
        ModuleAnalysisState state, List<ModuleFacts> inventory, ModuleAnalysisOptions options,
        IReadOnlyList<ModuleHeapStats> topByMemory, IReadOnlyList<ModuleTypeDensity> densityAnomalies,
        AppDomainAnalysisResult appDomains)
    {
        // Top modules by size -- same selection logic as the pre-retyping analyzer: the single
        // largest instance per module name.
        var candidates = new List<ModuleFacts>(state.ModulesByName.Count);
        foreach (List<ModuleFacts> group in state.ModulesByName.Values)
        {
            if (group.Count == 0) continue;
            ModuleFacts largest = group[0];
            for (int i = 1; i < group.Count; i++)
                if (group[i].Size > largest.Size) largest = group[i];
            candidates.Add(largest);
        }
        candidates.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        var topModulesBySize = new List<LoadedModuleSnapshot>(candidates.Count);
        foreach (ModuleFacts m in candidates)
            topModulesBySize.Add(ToSnapshot(m));

        var conflictDetails = new List<ModuleConflictGroup>(state.VersionConflicts.Count);
        foreach (KeyValuePair<string, List<ModuleFacts>> kvp in state.VersionConflicts)
        {
            var instances = new List<LoadedModuleSnapshot>(kvp.Value.Count);
            var versionSet = new HashSet<string>();
            foreach (ModuleFacts m in kvp.Value)
            {
                instances.Add(ToSnapshot(m));
                ExtractVersionFromAssemblyName(m.AssemblyName, versionSet);
            }
            conflictDetails.Add(new ModuleConflictGroup(kvp.Key, instances, versionSet.Order().ToList()));
        }

        return new ModuleDomainResult(
            state.TotalModules,
            state.DynamicModules,
            state.ModulesByName.Count,
            state.VersionConflicts.Count,
            state.VersionConflicts.Keys.ToArray(),
            topModulesBySize,
            conflictDetails,
            options.HeavyModuleWarningThresholdBytes,
            state.UnknownIdentityDuplicates,
            topByMemory,
            densityAnomalies,
            TotalDomains: appDomains.TotalDomains,
            Domains: appDomains.Domains,
            TotalDynamicModules: appDomains.TotalDynamicModules,
            DynamicModuleBytes: appDomains.DynamicModuleBytes,
            AnonymousModuleCount: appDomains.AnonymousModuleCount,
            TopModulesByTypeCount: appDomains.TopModulesByTypeCount,
            CrossDomainModuleLoads: appDomains.CrossDomainModuleLoads,
            AssemblyRefVersionMismatches: state.AssemblyRefMismatches.Count > 0 ? state.AssemblyRefMismatches : null)
        {
            Warnings = MergeWarnings(state.Warnings, appDomains.Warnings),
        };
    }

    private static LoadedModuleSnapshot ToSnapshot(ModuleFacts m) => new(
        m.Name, m.AssemblyName, m.FullPath, m.Address, m.Size, m.IsDynamic, m.IsPEFile,
        m.AssemblyLoadContextName, m.HasAssemblyLoadContext, m.IsCollectibleAssemblyLoadContext);

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

    private static IReadOnlyList<string> MergeWarnings(List<string> moduleWarnings, IReadOnlyList<string> appDomainWarnings)
    {
        if (moduleWarnings.Count == 0)
            return appDomainWarnings;

        var merged = new List<string>(moduleWarnings.Count + appDomainWarnings.Count);
        merged.AddRange(moduleWarnings);
        merged.AddRange(appDomainWarnings);
        return merged;
    }

    public void Dispose() { }
}
