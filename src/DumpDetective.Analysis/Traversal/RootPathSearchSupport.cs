using Microsoft.Diagnostics.Runtime;
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

    public static string FormatPath(ClrHeap heap, string rootKind, IReadOnlyList<ulong>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
            return $"{rootKind}: <no path>";

        var parts = new List<string>(addresses.Count);
        for (int i = 0; i < addresses.Count; i++)
            parts.Add(FormatNodeByAddress(heap, addresses[i]));

        return $"{rootKind}: {string.Join(" -> ", parts)}";
    }

    private static string FormatNodeByAddress(ClrHeap heap, ulong address)
    {
        if (address == 0)
            return "<invalid>";

        ClrObject obj = heap.GetObject(address);
        if (!obj.IsValid)
            return "<invalid>";

        string typeName = obj.Type?.Name ?? StringConstants.UnknownType;
        return $"{typeName}@0x{address:X}";
    }

    private sealed class NoOpPathSearchTelemetry : IPathSearchTelemetry
    {
        public void IncrementPruned() { }
        public void IncrementLargeFanout() { }
    }
}
