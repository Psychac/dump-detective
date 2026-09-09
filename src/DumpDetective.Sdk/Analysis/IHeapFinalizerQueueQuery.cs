namespace DumpDetective.Sdk.Analysis;

/// <summary>The <c>heap.finalizer-queue</c> capability — mirrors <c>heap.EnumerateFinalizableObjects()</c>.</summary>
public interface IHeapFinalizerQueueQuery
{
    IEnumerable<ulong> EnumerateFinalizerQueueAddresses();
}
