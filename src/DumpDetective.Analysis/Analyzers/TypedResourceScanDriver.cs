using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Compiler-checked call-order driver for the typed-resource quartet. Replaces the convention
/// (each analyzer calling <see cref="TypedResourceCandidateScanner"/>/<see cref="InstanceStateSampler{TSnapshot}"/>
/// static helpers by hand, in its own order) with two entry points that pin the order down at the
/// type level: candidate discovery always goes through <see cref="ITypedResourceCandidateSource"/>,
/// and a per-instance state read is only ever reachable after the sampler has reserved a slot.
/// </summary>
internal static class TypedResourceScanDriver
{
    public static Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> DiscoverCandidates(
        ITypedResourceCandidateSource source, ClrHeap heap, IHeapAnalysisCache? cache, CancellationToken cancellationToken = default) =>
        TypedResourceCandidateScanner.DiscoverCandidates(heap, cache, source.IsCandidateType, cancellationToken);

    public static InstanceStateSampler<TSnapshot> CreateSampler<TSnapshot>(ITypedResourceInstanceSampler<TSnapshot> source) =>
        new(source.MaxStateSamplesPerType, source.TopSampleCap);

    /// <summary>
    /// Reserves a sample slot for <paramref name="entry"/>'s MethodTable and, only if reserved,
    /// calls <see cref="ITypedResourceInstanceSampler{TSnapshot}.TrySample"/>. Returns the
    /// resulting snapshot (or null if the slot wasn't reserved or the read failed) so callers can
    /// still tally per-instance state and decide independently whether the snapshot is interesting
    /// enough to keep via <see cref="InstanceStateSampler{TSnapshot}.AddTopSample"/> — that
    /// filtering decision is analyzer-specific business logic, not part of the shared call order.
    /// </summary>
    public static TSnapshot? TryGetSample<TSnapshot>(
        ITypedResourceInstanceSampler<TSnapshot> source, InstanceStateSampler<TSnapshot> sampler,
        ClrHeap heap, in HeapEntry entry, string typeName)
    {
        if (!sampler.TryReserveSample(entry.MethodTable))
            return default;

        return source.TrySample(heap, in entry, typeName);
    }
}
