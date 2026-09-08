namespace DumpDetective.Analysis.Indexing;

internal readonly struct HeapEntry
{
    public readonly ulong Address;
    public readonly ulong MethodTable;
    public readonly ulong Size;

    /// <summary>
    /// GC generation (0/1/2, or higher for LOH/POH/Frozen depending on ClrMD's reporting),
    /// or -1 when unresolved/unavailable. Only populated when the entry came from the
    /// disk-backed object index (<see cref="ObjectIndexReader"/>); entries constructed
    /// directly from a live <see cref="Microsoft.Diagnostics.Runtime.ClrObject"/> walk carry
    /// the default -1 sentinel since no persisted value exists for that path.
    /// </summary>
    public readonly sbyte Generation;

    public HeapEntry(ulong address, ulong methodTable, ulong size)
        : this(address, methodTable, size, -1)
    {
    }

    public HeapEntry(ulong address, ulong methodTable, ulong size, sbyte generation)
    {
        Address = address;
        MethodTable = methodTable;
        Size = size;
        Generation = generation;
    }
}
