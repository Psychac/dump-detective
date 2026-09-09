namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Streaming heap-object enumeration — the <c>heap.objects</c> capability's coarse-scan surface.
/// Never buffer this into a list; the whole point of streaming here matches the project's
/// no-full-materialization rule for the underlying heap scan itself.
/// </summary>
public interface IHeapObjectStream
{
    IEnumerable<HeapObjectRef> EnumerateObjects();
}
