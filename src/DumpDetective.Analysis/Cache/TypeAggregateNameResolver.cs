using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Resolves a display type/module name for a <c>TypeAggregateIndexEntry</c>'s MethodTable,
/// falling back to the aggregate's sample instance when the MethodTable lookup fails (e.g.
/// unloaded module). Shared by every analyzer that reads Phase-1 TypeAggregates so the
/// resolution and placeholder-naming logic exists in exactly one place.
/// </summary>
internal static class TypeAggregateNameResolver
{
    public static string ResolveTypeName(ClrHeap heap, ulong methodTable, ulong sampleAddress)
    {
        ClrType? type = heap.GetTypeByMethodTable(methodTable);
        if (type?.Name is string name)
            return name;

        if (sampleAddress != 0)
        {
            ClrObject sample = heap.GetObject(sampleAddress);
            if (sample.IsValid && sample.Type?.Name is string sampleName)
                return sampleName;
        }

        return $"MethodTable@0x{methodTable:X}";
    }

    public static string ResolveModuleName(ClrHeap heap, ulong methodTable, ulong sampleAddress)
    {
        ClrType? type = heap.GetTypeByMethodTable(methodTable);
        if (type?.Module?.Name is string moduleName && !string.IsNullOrWhiteSpace(moduleName))
            return Path.GetFileName(moduleName);

        if (sampleAddress != 0)
        {
            ClrObject sample = heap.GetObject(sampleAddress);
            if (sample.IsValid && sample.Type?.Module?.Name is string sampleModuleName && !string.IsNullOrWhiteSpace(sampleModuleName))
                return Path.GetFileName(sampleModuleName);
        }

        return "N/A";
    }
}
