using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Modules

public sealed record LoadedModuleSnapshot(
    string Name,
    string AssemblyName,
    string FullPath,
    ulong Address,
    ulong Size,
    bool IsDynamic,
    bool IsPEFile,
    string? AssemblyLoadContextName = null,
    bool HasAssemblyLoadContext = false,
    bool IsCollectibleAssemblyLoadContext = false);

public sealed record ModuleConflictGroup(
    string ModuleName,
    IReadOnlyList<LoadedModuleSnapshot> Instances,
    IReadOnlyList<string> Versions);

/// <summary>Per-module heap memory and object footprint aggregated from the heap index.</summary>
public sealed record ModuleHeapStats(
    string ModuleName,
    string AssemblyName,
    int UniqueTypeCount,
    long ObjectCount,
    ulong TotalBytes,
    ulong LohBytes,
    long Gen2ObjectCount,
    /// <summary>Top types by total bytes within this module. Populated only for modules whose
    /// <see cref="TotalBytes"/> meets <see cref="ModuleDomainResult.HeavyModuleWarningThresholdBytes"/>.</summary>
    IReadOnlyList<ModuleTypeUsage>? TopTypes = null);

/// <summary>One type's contribution to a heavy module's heap footprint.</summary>
public sealed record ModuleTypeUsage(
    string TypeName,
    long ObjectCount,
    ulong TotalBytes);

/// <summary>Modules where memory is abnormally concentrated into very few types.</summary>
public sealed record ModuleTypeDensity(
    string ModuleName,
    string AssemblyName,
    int UniqueTypeCount,
    long ObjectCount,
    ulong TotalBytes,
    ulong BytesPerType);

internal sealed record AppDomainSnapshot(
    string Name,
    ulong Address,
    int DomainId,
    int ModuleCount,
    ulong EstimatedManagedBytes,
    IReadOnlyList<string>? TopModules = null);

internal sealed record ModuleTypeCountEntry(
    string ModuleName,
    string AssemblyName,
    int TypeCount,
    int LiveTypeCount,
    long ObjectCount,
    ulong TotalBytes);

/// <summary>A non-framework module loaded into more than one AppDomain — each load is a
/// separate managed-memory copy, so this multiplies the module's heap footprint per domain.</summary>
public sealed record CrossDomainModuleLoad(
    string ModuleName,
    string AssemblyName,
    int DomainCount,
    ulong Size);

/// <summary>A module's AssemblyRef entry requires a version of another assembly that differs from
/// every version of that assembly actually loaded — evidence of an implicit/unification binding.</summary>
public sealed record AssemblyRefVersionMismatch(
    string RequiringModule,
    string RequiredAssemblyName,
    string RequiredVersion,
    string LoadedVersions);

internal sealed record ModuleDomainResult(
    int TotalModules,
    int DynamicModules,
    int UniqueModuleNames,
    int VersionConflictGroups,
    IReadOnlyList<string> ConflictingAssemblyNames,
    IReadOnlyList<LoadedModuleSnapshot> TopModulesBySize,
    IReadOnlyList<ModuleConflictGroup> ConflictDetails,
    ulong HeavyModuleWarningThresholdBytes,
    IReadOnlySet<string> UnknownIdentityDuplicateModules,
    IReadOnlyList<ModuleHeapStats>? TopModulesByHeapMemory = null,
    IReadOnlyList<ModuleTypeDensity>? HeavyTypeDensityModules = null,
    int TotalDomains = 0,
    IReadOnlyList<AppDomainSnapshot>? Domains = null,
    int TotalDynamicModules = 0,
    ulong DynamicModuleBytes = 0,
    int AnonymousModuleCount = 0,
    IReadOnlyList<ModuleTypeCountEntry>? TopModulesByTypeCount = null,
    IReadOnlyList<CrossDomainModuleLoad>? CrossDomainModuleLoads = null,
    IReadOnlyList<AssemblyRefVersionMismatch>? AssemblyRefVersionMismatches = null) : AnalyzerDomainResult;
