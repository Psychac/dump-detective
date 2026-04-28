namespace DumpDetective.Analysis.Indexing;

internal readonly struct HeapEntry
{
    public readonly ulong Address;
    public readonly ulong MethodTable;
    public readonly ulong Size;

    public HeapEntry(ulong address, ulong methodTable, ulong size)
    {
        Address = address;
        MethodTable = methodTable;
        Size = size;
    }
}
