using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Indexing;

internal static class HeapStreamer
{
    public static IEnumerable<HeapEntry> Stream(ClrHeap heap)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null)
            {
                continue;
            }

            ulong methodTable = obj.Type.MethodTable;
            if (methodTable == 0)
            {
                continue;
            }

            yield return new HeapEntry(obj.Address, methodTable, obj.Size);
        }
    }
}
