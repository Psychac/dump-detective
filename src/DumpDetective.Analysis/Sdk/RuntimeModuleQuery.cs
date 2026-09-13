using DumpDetective.Analysis.Utilities;
using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>runtime.modules</c> capability, built for
/// <c>ModuleAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md)
/// — <see cref="IRuntimeModuleQuery"/>'s first real consumer.
/// </summary>
internal sealed class RuntimeModuleQuery(ClrRuntime runtime) : IRuntimeModuleQuery
{
    private Dictionary<ulong, ClrModule>? _modulesByAddress;

    private Dictionary<ulong, ClrModule> ModulesByAddress => _modulesByAddress ??= BuildModulesByAddress();

    private Dictionary<ulong, ClrModule> BuildModulesByAddress()
    {
        var map = new Dictionary<ulong, ClrModule>();
        foreach (ClrModule module in runtime.EnumerateModules())
            if (module.Address != 0)
                map[module.Address] = module;
        return map;
    }

    public IEnumerable<RuntimeModuleInfo> EnumerateModules()
    {
        var seen = new HashSet<ulong>();
        foreach (ClrModule module in runtime.EnumerateModules())
        {
            if (module.Address == 0 || !seen.Add(module.Address))
                continue;
            yield return ToModuleInfo(module);
        }
    }

    public IEnumerable<AppDomainInfo> EnumerateAppDomains()
    {
        foreach (ClrAppDomain domain in runtime.AppDomains)
            yield return new AppDomainInfo(domain.Id, domain.Name ?? string.Empty, domain.Address);
    }

    public IEnumerable<RuntimeModuleInfo> EnumerateModules(AppDomainInfo domain)
    {
        foreach (ClrAppDomain appDomain in runtime.AppDomains)
        {
            if (appDomain.Id != domain.DomainId)
                continue;

            foreach (ClrModule module in appDomain.Modules)
                yield return ToModuleInfo(module);
            yield break;
        }
    }

    private static RuntimeModuleInfo ToModuleInfo(ClrModule module) => new(
        Address: module.Address,
        Name: Path.GetFileName(module.Name ?? string.Empty) ?? string.Empty,
        FullPath: module.Name ?? string.Empty,
        AssemblyName: module.AssemblyName ?? "Unknown",
        Size: module.Size,
        IsDynamic: module.IsDynamic,
        IsPEFile: module.IsPEFile,
        AppDomainId: module.AppDomain.Id);

    public IEnumerable<string> EnumerateDeclaredTypeNames(ulong moduleAddress)
    {
        if (!ModulesByAddress.TryGetValue(moduleAddress, out ClrModule? module))
            yield break;

        foreach ((ulong methodTable, int _) in module.EnumerateTypeDefToMethodTableMap())
        {
            if (methodTable == 0)
                continue;
            ClrType? type = runtime.GetTypeByMethodTable(methodTable);
            if (type?.Name is string name)
                yield return name;
        }
    }

    public bool TryResolveAssemblyLoadContext(ulong moduleAddress, out ulong alcAddress, out bool isCollectible)
    {
        alcAddress = 0;
        isCollectible = false;

        if (!ModulesByAddress.TryGetValue(moduleAddress, out ClrModule? module))
            return false;

        foreach ((ulong methodTable, int _) in module.EnumerateTypeDefToMethodTableMap())
        {
            if (methodTable == 0)
                continue;

            ClrType? type = runtime.GetTypeByMethodTable(methodTable);
            if (type is null)
                continue;

            alcAddress = type.AssemblyLoadContextAddress;
            isCollectible = type.IsCollectible;
            return alcAddress != 0;
        }

        return false;
    }

    public IReadOnlyList<(string Name, string Version)> GetAssemblyReferences(ulong moduleAddress)
    {
        if (!ModulesByAddress.TryGetValue(moduleAddress, out ClrModule? module) || module.IsDynamic)
            return [];

        IReadOnlyList<AssemblyRefProbe.AssemblyRefEntry> entries =
            AssemblyRefProbe.TryEnumerateAssemblyRefs(runtime.DataTarget!.DataReader, module.MetadataAddress, module.MetadataLength);

        if (entries.Count == 0)
            return [];

        var result = new List<(string Name, string Version)>(entries.Count);
        foreach (AssemblyRefProbe.AssemblyRefEntry entry in entries)
            result.Add((entry.Name, entry.Version));
        return result;
    }
}
