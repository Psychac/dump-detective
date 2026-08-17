using DumpDetective.Analysis.Traversal.Dominator;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// Shared synthetic-graph <see cref="SuccessorsFunc"/> builder for the dominator traversal tests,
/// mirroring the <c>BuildGraph</c> helper style established in <c>LengauerTarjanTests</c>. Previously
/// triplicated verbatim across <c>ReachableGraphWalkerTests</c>, <c>LeafFolderTests</c> and
/// <c>DominatorTreeComputerTests</c>; consolidated here so the buffer-growth contract
/// <see cref="SuccessorsFunc"/> imposes on implementers is expressed in exactly one place.
/// </summary>
internal static class SyntheticSuccessors
{
    /// <summary>
    /// Builds a successors function from a plain <c>(parent, child)</c> edge list. Honours
    /// <see cref="SuccessorsFunc"/>'s contract by growing the caller's buffer when a node has more
    /// children than it currently holds, so tests exercise the resize path and not only the
    /// fits-first-time case.
    /// </summary>
    public static SuccessorsFunc Build(params (ulong Parent, ulong Child)[] edges)
    {
        var forward = new Dictionary<ulong, List<ulong>>();
        foreach ((ulong parent, ulong child) in edges)
        {
            if (!forward.TryGetValue(parent, out List<ulong>? children))
                forward[parent] = children = new List<ulong>();
            children.Add(child);
        }

        return (ulong addr, ref ulong[] buffer) =>
        {
            if (!forward.TryGetValue(addr, out List<ulong>? c))
                return 0;

            if (buffer.Length < c.Count)
                buffer = new ulong[c.Count];

            for (int i = 0; i < c.Count; i++)
                buffer[i] = c[i];

            return c.Count;
        };
    }
}
