using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>Adapts <see cref="ForwardEdgeIndexReader"/> to <see cref="IForwardReferenceProvider"/>, mirroring <see cref="ReverseIndex.ReverseIndexBackwardReferenceProvider"/>.</summary>
internal sealed class ForwardIndexForwardReferenceProvider(ForwardEdgeIndexReader reader) : IForwardReferenceProvider
{
    public bool TryGetChildren(ulong parent, out IReadOnlyList<ulong> children) =>
        reader.TryGetChildren(parent, out children);

    /// <summary>Overrides the interface's allocating default with the reader's buffer-reusing path.</summary>
    public int GetChildren(ulong parent, ref ulong[] buffer) =>
        reader.GetChildren(parent, ref buffer);
}
