using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// Cheap per-root BFS for producing a single sample "root path" string for Evidence.
/// Mirrors the Fast-mode search in <c>ReferenceChainAnalyzer</c> (single BFS per root,
/// reused buffers, no candidate-set/reverse-index build) rather than the heavier
/// <see cref="RootPathFinder"/> — intended to be called only for a small top-K set of
/// items per analyzer, never per-candidate before filtering.
/// </summary>
internal static class SampleRootPathFinder
{
    private const int AbsoluteMaxDepth = 20;
    private const int DefaultMaxPathSearchObjects = 5_000;
    private const int DefaultLargeFanoutThreshold = 100;

    public readonly record struct Result(string? Path, bool Truncated);

    public static Result TryFindSampleRootPath(
        ClrHeap heap,
        IReadOnlyList<(string RootKind, ulong Address)> roots,
        ulong targetAddress,
        int maxPathSearchObjects = DefaultMaxPathSearchObjects,
        int maxPathDepth = AbsoluteMaxDepth,
        int largeFanoutThreshold = DefaultLargeFanoutThreshold)
    {
        if (targetAddress == 0 || roots.Count == 0)
            return new Result(null, false);

        maxPathDepth = Math.Min(maxPathDepth, AbsoluteMaxDepth);

        var visited = new HashSet<ulong>(capacity: 1024);
        var previous = new Dictionary<ulong, ulong>(capacity: 1024);
        var queue = new Queue<(ulong Address, int Depth)>(capacity: 256);
        bool anyTruncated = false;

        foreach ((string rootKind, ulong rootAddress) in roots)
        {
            if (TryBuildPath(heap, rootAddress, targetAddress, maxPathSearchObjects, maxPathDepth, largeFanoutThreshold,
                visited, previous, queue, out List<ulong>? addresses, out bool pathSearchLimited))
            {
                return new Result(FormatPath(heap, rootKind, addresses), anyTruncated || pathSearchLimited);
            }

            if (pathSearchLimited)
                anyTruncated = true;
        }

        return new Result(null, anyTruncated);
    }

    private static bool TryBuildPath(
        ClrHeap heap,
        ulong startAddress,
        ulong targetAddress,
        int maxPathSearchObjects,
        int maxPathDepth,
        int largeFanoutThreshold,
        HashSet<ulong> visited,
        Dictionary<ulong, ulong> previous,
        Queue<(ulong Address, int Depth)> queue,
        out List<ulong>? path,
        out bool searchLimitReached)
    {
        path = null;
        searchLimitReached = false;

        if (startAddress == targetAddress)
        {
            path = new List<ulong> { startAddress };
            return true;
        }

        if (startAddress == 0 || targetAddress == 0)
            return false;

        visited.Clear();
        visited.Add(startAddress);
        previous.Clear();
        queue.Clear();
        queue.Enqueue((startAddress, 0));

        int searched = 0;

        while (queue.Count > 0 && searched++ < maxPathSearchObjects)
        {
            (ulong current, int depth) = queue.Dequeue();

            if (depth >= maxPathDepth)
                continue;

            foreach (ulong refAddress in EnumerateReferenceAddresses(heap, current, largeFanoutThreshold))
            {
                if (refAddress == targetAddress)
                {
                    path = ReconstructPath(previous, startAddress, targetAddress, current);
                    return true;
                }

                if (visited.Add(refAddress))
                {
                    previous[refAddress] = current;
                    queue.Enqueue((refAddress, depth + 1));
                }
            }
        }

        searchLimitReached = queue.Count > 0 && searched >= maxPathSearchObjects;
        return false;
    }

    private static List<ulong> ReconstructPath(Dictionary<ulong, ulong> previous, ulong startAddress, ulong targetAddress, ulong targetParent)
    {
        var reversed = new List<ulong>(capacity: 16) { targetAddress, targetParent };

        ulong cursor = targetParent;
        while (cursor != startAddress && previous.TryGetValue(cursor, out ulong parent))
        {
            reversed.Add(parent);
            cursor = parent;
        }

        reversed.Reverse();
        return reversed;
    }

    private static IEnumerable<ulong> EnumerateReferenceAddresses(ClrHeap heap, ulong sourceAddress, int largeFanoutThreshold)
    {
        if (!TryGetValidObject(heap, sourceAddress, out ClrObject sourceObject))
            yield break;

        if (IsNoisyType(sourceObject.Type))
            yield break;

        int counted = 0;
        foreach (ClrObject reference in sourceObject.EnumerateReferences(carefully: true))
        {
            counted++;
            if (counted > largeFanoutThreshold)
                yield break;

            if (!reference.IsValid || reference.Address == 0)
                continue;

            yield return reference.Address;
        }
    }

    private static bool IsNoisyType(ClrType? type)
    {
        if (type is null)
            return false;

        string? name = type.Name;
        return name == "System.String" || name == "System.Object";
    }

    private static string FormatPath(ClrHeap heap, string rootKind, IReadOnlyList<ulong>? addresses)
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
        if (!TryGetValidObject(heap, address, out ClrObject obj))
            return "<invalid>";

        string typeName = obj.Type?.Name ?? StringConstants.UnknownType;
        return $"{typeName}@0x{address:X}";
    }

    private static bool TryGetValidObject(ClrHeap heap, ulong address, out ClrObject obj)
    {
        obj = default;
        if (address == 0)
            return false;

        obj = heap.GetObject(address);
        return obj.IsValid;
    }
}
