namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Point lookup by address — the <c>heap.objects</c> capability's random-access surface,
/// complementing <see cref="IHeapObjectStream"/>'s sequential one. Mirrors
/// <c>IHeapAnalysisCache.TryGetObjectMetadata</c>: returns <c>false</c> for a dead/invalid address,
/// which is an expected outcome, not an error.
/// </summary>
public interface IHeapObjectLookup
{
    bool TryGetObject(ulong address, out HeapObjectRef heapObject);
}
