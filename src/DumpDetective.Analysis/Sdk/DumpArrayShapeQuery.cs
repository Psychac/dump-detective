using System.Numerics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of <see cref="IDumpArrayShapeQuery"/> (<c>dump.array-elements</c>) —
/// see that interface's own remarks. Lives here rather than a <c>Sources.ClrDump</c> project for the
/// same reason every other dump-side Tier-1/Tier-2 implementation does today.
/// </summary>
internal sealed class DumpArrayShapeQuery : IDumpArrayShapeQuery
{
    private readonly ClrHeap _heap;

    public DumpArrayShapeQuery(ClrHeap heap) => _heap = heap;

    public bool TryGetArrayShape(ulong address, out int length, out int rank)
    {
        length = 0;
        rank = 0;
        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return false;

        ClrArray arr = obj.AsArray();
        length = arr.Length;
        rank = arr.Rank;
        return true;
    }

    public int? CountNullOrZeroElements(ulong address)
    {
        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return null;

        ClrArray arr = obj.AsArray();
        if (arr.Rank != 1)
            return null;

        bool isReference = obj.Type.ComponentType?.IsObjectReference == true;
        int result = isReference
            ? CountNullReferences(arr)
            : CountZeroElements(arr, arr.Length, obj.Type.ComponentType?.ElementType ?? ClrElementType.Unknown);
        return result < 0 ? null : result;
    }

    public IReadOnlyList<ulong> EnumerateObjectReferenceElements(ulong address, int maxElements)
    {
        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return [];

        ClrArray arr = obj.AsArray();
        if (arr.Rank != 1)
            return [];

        if (obj.Type.ComponentType?.IsObjectReference != true)
            return [];

        int count = Math.Min(arr.Length, maxElements);
        var result = new List<ulong>(count);
        for (int i = 0; i < count; i++)
        {
            ClrObject elem = arr.GetObjectValue(i);
            result.Add(elem.IsValid ? elem.Address : 0);
        }
        return result;
    }

    private static int CountNullReferences(ClrArray arr)
    {
        int nullCount = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            ClrObject elem = arr.GetObjectValue(i);
            if (!elem.IsValid || elem.Address == 0) nullCount++;
        }
        return nullCount;
    }

    // Bounds peak allocation to SparseReadChunkSize * sizeof(T) regardless of array length, so a
    // single ReadValues<T> call never has to allocate a buffer as large as e.g. a 100 MB LOH byte[].
    private const int SparseReadChunkSize = 65_536;

    private static int CountZeroElements(ClrArray arr, int length, ClrElementType elemType) => elemType switch
    {
        ClrElementType.Int8 => CountZeroElements<sbyte>(arr, length),
        ClrElementType.UInt8 => CountZeroElements<byte>(arr, length),
        ClrElementType.Int16 => CountZeroElements<short>(arr, length),
        ClrElementType.UInt16 => CountZeroElements<ushort>(arr, length),
        ClrElementType.Int32 => CountZeroElements<int>(arr, length),
        ClrElementType.UInt32 => CountZeroElements<uint>(arr, length),
        ClrElementType.Int64 => CountZeroElements<long>(arr, length),
        ClrElementType.UInt64 => CountZeroElements<ulong>(arr, length),
        ClrElementType.Float => CountZeroElements<float>(arr, length),
        ClrElementType.Double => CountZeroElements<double>(arr, length),
        _ => -1,
    };

    // ReadValues<T> returns null (not a shorter array, not zero-filled) when the requested range is
    // partially or fully unreadable -- e.g. a truncated dump. Surfaced here as -1, mapped to null by
    // the caller, so a candidate with a partial read is skipped rather than reporting a ratio
    // computed over fewer elements than the array's actual length.
    private static int CountZeroElements<T>(ClrArray arr, int length)
        where T : unmanaged, INumber<T>
    {
        int zeroCount = 0;
        for (int offset = 0; offset < length; offset += SparseReadChunkSize)
        {
            int take = Math.Min(SparseReadChunkSize, length - offset);
            T[]? chunk = arr.ReadValues<T>(offset, take);
            if (chunk is null) return -1;
            for (int i = 0; i < chunk.Length; i++)
                if (chunk[i] == T.Zero) zeroCount++;
        }
        return zeroCount;
    }
}
