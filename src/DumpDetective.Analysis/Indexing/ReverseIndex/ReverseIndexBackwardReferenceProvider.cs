using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// Adapts <see cref="ReverseEdgeIndexReader"/> to the <see cref="IBackwardReferenceProvider"/>
/// abstraction so traversal code in <c>DumpDetective.Analysis.Traversal</c> can consume it without
/// depending on the container/indexing internals directly.
/// </summary>
internal sealed class ReverseIndexBackwardReferenceProvider(ReverseEdgeIndexReader reader) : IBackwardReferenceProvider
{
    public bool TryGetParents(ulong child, out IReadOnlyList<ulong> parents, out bool truncated) =>
        reader.TryGetParents(child, out parents, out truncated);
}
