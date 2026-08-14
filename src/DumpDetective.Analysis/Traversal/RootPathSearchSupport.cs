using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Shared pure helpers for evidence-population callers of <see cref="RootPathFinder"/> that just
/// want a single formatted path and don't need custom noise filtering or telemetry. Search
/// construction/configuration (limits, provider) stays inline at each call site.
/// </summary>
internal static class RootPathSearchSupport
{
    public static readonly IPathSearchTelemetry NoOpTelemetry = new NoOpPathSearchTelemetry();

    public static bool IsNoisyType(ClrType? type)
    {
        if (type is null)
            return false;

        string? name = type.Name;
        return name == "System.String" || name == "System.Object";
    }

    /// <summary>
    /// Resolves the <see cref="ClrType"/> at <paramref name="address"/> via the disk-backed
    /// address index when <paramref name="cache"/> is supplied (see
    /// docs/cache/cache-architecture.md), falling back to a live
    /// <c>heap.GetObject(address).Type</c> resolution when <paramref name="cache"/> is <c>null</c>
    /// or the index is unavailable. Returns <c>null</c> when <paramref name="address"/> isn't a
    /// live object — the same contract <c>heap.GetObject(address).Type</c> already had, so callers
    /// don't need to distinguish "no cache" from "cache miss."
    /// </summary>
    public static ClrType? ResolveType(ClrHeap heap, IHeapAnalysisCache? cache, ulong address)
    {
        if (cache is not null && cache.TryGetObjectMetadata(heap, address, out ulong methodTable, out _))
            return methodTable != 0 ? heap.GetTypeByMethodTable(methodTable) : null;

        return heap.GetObject(address).Type;
    }

    public static string FormatPath(ClrHeap heap, string rootKind, IReadOnlyList<ulong>? addresses, IHeapAnalysisCache? cache = null)
    {
        if (addresses is null || addresses.Count == 0)
            return $"{rootKind}: <no path>";

        var parts = new List<string>(addresses.Count);
        for (int i = 0; i < addresses.Count; i++)
            parts.Add(FormatNodeByAddress(heap, addresses[i], cache));

        return $"{rootKind}: {string.Join(" -> ", parts)}";
    }

    private static string FormatNodeByAddress(ClrHeap heap, ulong address, IHeapAnalysisCache? cache)
    {
        if (address == 0)
            return "<invalid>";

        ClrType? type = ResolveType(heap, cache, address);
        if (type is null)
            return "<invalid>";

        string typeName = type.Name ?? StringConstants.UnknownType;
        return $"{typeName}@0x{address:X}";
    }

    private sealed class NoOpPathSearchTelemetry : IPathSearchTelemetry
    {
        public void IncrementPruned() { }
        public void IncrementLargeFanout() { }
    }
}
