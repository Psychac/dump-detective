using Microsoft.Diagnostics.Runtime;
using System;

namespace DumpDetective.Analysis.Indexing.Satellite;

internal sealed class MemoryHandleSnapshotReader : IHandleSnapshotReader
{
    private readonly ClrRuntime _runtime;
    private readonly ClrHeap _heap;
    private readonly int _cap;
    private long _count;

    public MemoryHandleSnapshotReader(ClrRuntime runtime, ClrHeap heap, int cap)
    {
        _runtime = runtime;
        _heap = heap;
        _cap = cap;
    }

    public long? RecordCount => null;

    public IEnumerable<HandleRecord> EnumerateRecords(System.Threading.CancellationToken token)
    {
        _count = 0;
        foreach (var h in _runtime.EnumerateHandles())
        {
            token.ThrowIfCancellationRequested();
            var kind = (byte)h.HandleKind;
            ulong addr = h.Object.Address;
            ulong mt = 0UL;
            if (addr != 0)
            {
                var obj = _heap.GetObject(addr);
                if (obj.IsValid) mt = obj.Type?.MethodTable ?? 0UL;
            }

            yield return new HandleRecord(addr, mt, kind);
            _count++;
            if (_count > _cap) yield break;
        }
    }

    public void Dispose() { }
}
