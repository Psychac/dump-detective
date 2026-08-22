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
    bool IsPEFile);

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
    long Gen2ObjectCount);

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
    IReadOnlyList<ModuleTypeCountEntry>? TopModulesByTypeCount = null) : AnalyzerDomainResult;
