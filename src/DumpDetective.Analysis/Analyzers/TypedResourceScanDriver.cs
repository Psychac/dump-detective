using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Compiler-checked call-order driver for the typed-resource quartet. Replaces the convention
/// (each analyzer calling <see cref="TypedResourceCandidateScanner"/>/<see cref="InstanceStateSampler{TSnapshot}"/>
/// static helpers by hand, in its own order) with an entry point that pins candidate discovery
/// down at the type level, always going through <see cref="ITypedResourceCandidateSource"/>.
/// </summary>
internal static class TypedResourceScanDriver
{
    public static Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> DiscoverCandidates(
        ITypedResourceCandidateSource source, ClrHeap heap, IHeapAnalysisCache? cache, CancellationToken cancellationToken = default) =>
        TypedResourceCandidateScanner.DiscoverCandidates(heap, cache, source.IsCandidateType, cancellationToken);

    // `source` is otherwise unused — kept as the call site's TSnapshot type-inference anchor
    // (`CreateSampler(this)` instead of an explicit `CreateSampler<TSnapshot>()`).
    public static InstanceStateSampler<TSnapshot> CreateSampler<TSnapshot>(ITypedResourceInstanceSampler<TSnapshot> source) =>
        new();

    /// <summary>
    /// Reads and classifies the instance at <paramref name="entry"/>'s address via
    /// <see cref="ITypedResourceInstanceSampler{TSnapshot}.TrySample"/>. Thin wrapper so callers
    /// can pass <c>this</c> without an explicit interface cast.
    /// </summary>
    public static TSnapshot? TryGetSample<TSnapshot>(
        ITypedResourceInstanceSampler<TSnapshot> source, ClrHeap heap, in HeapEntry entry, string typeName) =>
        source.TrySample(heap, in entry, typeName);
}
