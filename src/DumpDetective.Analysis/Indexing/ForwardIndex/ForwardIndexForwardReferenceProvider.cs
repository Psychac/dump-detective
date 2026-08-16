using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>Adapts <see cref="ForwardEdgeIndexReader"/> to <see cref="IForwardReferenceProvider"/>, mirroring <see cref="ReverseIndex.ReverseIndexBackwardReferenceProvider"/>.</summary>
internal sealed class ForwardIndexForwardReferenceProvider(ForwardEdgeIndexReader reader) : IForwardReferenceProvider
{
    public bool TryGetChildren(ulong parent, out IReadOnlyList<ulong> children) =>
        reader.TryGetChildren(parent, out children);
}
