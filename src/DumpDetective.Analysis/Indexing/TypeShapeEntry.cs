namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Compact per-MethodTable field layout summary built during Phase 1.
/// Held in <see cref="HeapIndexBuildResult.TypeShapeCache"/> (never written to disk).
/// </summary>
/// <remarks>
/// 6 bytes of payload, padded to 8 bytes by the runtime. 50K types × ~16 bytes (key+value
/// in dictionary) ≈ 800 KB — always fits in L2 cache on analysis startup.
/// </remarks>
internal readonly struct TypeShapeEntry
{
    /// <summary>Number of reference-type instance fields (pointers that GC traces).</summary>
    public readonly short RefFields;

    /// <summary>Number of value-type instance fields (inline value layout).</summary>
    public readonly short ValFields;

    /// <summary>Total instance field count (RefFields + ValFields).</summary>
    public readonly short TotalFields;

    public TypeShapeEntry(short refFields, short valFields)
    {
        RefFields  = refFields;
        ValFields  = valFields;
        TotalFields = (short)(refFields + valFields);
    }
}
