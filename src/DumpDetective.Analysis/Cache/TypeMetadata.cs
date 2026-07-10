using System.Collections.Immutable;

namespace DumpDetective.Analysis.Cache;

internal readonly struct TypeMetadata
{
    public readonly ulong MethodTable;
    public readonly bool ContainsPointers;
    public readonly bool IsArray;
    public readonly bool ArrayContainsPointers;
    public readonly bool IsString;
    public readonly bool IsDelegate;
    public readonly bool IsException;
    public readonly bool IsFreeObject;
    public readonly int InstanceSize;
    public readonly int ReferenceFieldCount;
    public readonly ImmutableArray<int> ReferenceFieldOffsets;

    public TypeMetadata(
        ulong methodTable,
        bool containsPointers,
        bool isArray,
        bool arrayContainsPointers,
        bool isString,
        bool isDelegate,
        bool isException,
        bool isFreeObject,
        int instanceSize,
        ImmutableArray<int> referenceFieldOffsets)
    {
        MethodTable = methodTable;
        ContainsPointers = containsPointers;
        IsArray = isArray;
        ArrayContainsPointers = arrayContainsPointers;
        IsString = isString;
        IsDelegate = isDelegate;
        IsException = isException;
        IsFreeObject = isFreeObject;
        InstanceSize = instanceSize;
        ReferenceFieldOffsets = referenceFieldOffsets;
        ReferenceFieldCount = referenceFieldOffsets.Length;
    }
}
