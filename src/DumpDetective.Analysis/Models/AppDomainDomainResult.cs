using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// AppDomainAnalyzer domain models

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

internal sealed record AppDomainDomainResult(
    int TotalDomains,
    IReadOnlyList<AppDomainSnapshot> Domains,
    int TotalDynamicModules,
    ulong DynamicModuleBytes,
    int AnonymousModuleCount,
    IReadOnlyList<ModuleTypeCountEntry> TopModulesByTypeCount,
    int ExcludedModuleCount = 0) : AnalyzerDomainResult;
