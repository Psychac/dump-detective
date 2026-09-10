namespace DumpDetective.Sdk.Analysis;

/// <summary>The <c>heap.finalizer-queue</c> capability — mirrors <c>heap.EnumerateFinalizableObjects()</c>.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapFinalizerQueueQuery
{
    IEnumerable<ulong> EnumerateFinalizerQueueAddresses();
}
