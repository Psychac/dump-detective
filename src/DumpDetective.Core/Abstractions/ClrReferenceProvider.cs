using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

internal sealed class ClrReferenceProvider : IReferenceProvider
{
    private readonly ClrHeap _heap;

    public ClrReferenceProvider(ClrHeap heap)
    {
        _heap = heap;
    }

    public IEnumerable<ulong> GetReferences(ulong obj)
    {
        var clrObj = _heap.GetObject(obj);
        if (!clrObj.IsValid)
            yield break;

        foreach (var child in clrObj.EnumerateReferences(carefully: true))
        {
            if (!child.IsValid) continue;
            if (child.Address == 0) continue;
            yield return child.Address;
        }
    }
}
