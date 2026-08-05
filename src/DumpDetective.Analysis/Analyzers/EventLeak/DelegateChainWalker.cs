using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers.EventLeak;

/// <summary>
/// Pointer-chase logic for extracting subscriber (address, methodTable, delegateAddress)
/// tuples from a MulticastDelegate chain. Used by <see cref="FieldBackedDelegateShape.Extract"/>
/// (shape-level correctness tests) and mirrored inline by <c>EventLeakFastScanner</c>'s hot
/// path — the two are kept as separate call sites deliberately (design §5): the hot path must
/// never go through the <see cref="IPublisherShape"/> virtual call.
/// </summary>
internal static class DelegateChainWalker
{
    public static void ExtractSubscribers(
        ClrHeap heap, IMemoryReader reader, ulong delegateAddr,
        int targetOffset, int invListOffset,
        List<(ulong Address, ulong MethodTable, ulong DelegateAddress)> results)
    {
        if (!reader.ReadPointer(delegateAddr + (ulong)invListOffset, out ulong invListAddr))
            return;

        if (invListAddr == 0)
        {
            ExtractSingleTarget(reader, delegateAddr, targetOffset, results);
            return;
        }

        // Multicast: invListAddr points to a Delegate[] array containing per-subscriber
        // delegates. ClrMD's GetObject/IsArray validates this and gives an authoritative
        // Length — raw MT-lookup is unreliable here (see EventLeakFastScanner history).
        ClrObject invListObj = heap.GetObject(invListAddr);
        if (!invListObj.IsValid || !invListObj.IsArray)
        {
            ExtractSingleTarget(reader, delegateAddr, targetOffset, results);
            return;
        }

        ClrArray arr = invListObj.AsArray();
        int invCount = arr.Length;
        if (invCount == 0 || invCount > 1_000_000) // sanity cap
            return;

        for (int i = 0; i < invCount; i++)
        {
            ClrObject elem = arr.GetObjectValue(i);
            if (elem.IsValid && elem.Address != 0)
                ExtractSingleTarget(reader, elem.Address, targetOffset, results);
        }
    }

    public static void ExtractSingleTarget(
        IMemoryReader reader, ulong delegateAddr, int targetOffset,
        List<(ulong Address, ulong MethodTable, ulong DelegateAddress)> results)
    {
        if (reader.ReadPointer(delegateAddr + (ulong)targetOffset, out ulong targetAddr) && targetAddr != 0)
        {
            // Instance method handler: _target is the subscriber object.
            if (reader.ReadPointer(targetAddr, out ulong targetMt) && targetMt != 0)
                results.Add((targetAddr, targetMt, delegateAddr));
        }
        else
        {
            // Static method handler: use the delegate object itself as the token.
            if (reader.ReadPointer(delegateAddr, out ulong delegateMt) && delegateMt != 0)
                results.Add((delegateAddr, delegateMt, delegateAddr));
        }
    }
}
