# ObjectShapeAnalyzer — Design Spec

## Status
**New** · Implementation Priority **9** · Effort: Low · ✅ **Completed**

## Report Sections Served
- §3.3 Object Shape Analysis (reference-heavy vs value-heavy, field layout profiles)
- §3.1 Detailed Type Table (`IsFinalizable`, `IsValueType`, base type depth, interface count, field counts)

## Rationale
Type structure (reference-heavy vs value-heavy) affects GC scan cost and memory layout.
This is purely a `ClrType` metadata analysis — no heap object enumeration required.

---

## Domain Result

```csharp
ObjectShapeAnalyzerDomainResult(
    IReadOnlyList<TypeShapeProfile> TopReferenceHeavyTypes,
    IReadOnlyList<TypeShapeProfile> TopValueHeavyTypes,
    int TotalTypesAnalyzed,
    double AvgRefFieldsPerType)

TypeShapeProfile(
    string TypeName,
    int TotalFields,
    int ReferenceFields,
    int ValueFields,
    double ReferenceFieldRatio,
    ulong InstanceCount,
    bool IsFinalizable,
    bool IsValueType,
    int BaseTypeChainDepth,
    int InterfaceCount,
    ObjectShapeCategory Category)

// Enum
ObjectShapeCategory : ReferenceHeavy | ValueHeavy | Balanced | Scalar
```

---

## Implementation Strategy

- Enumerate `ClrType` entries from the heap index — iterate `TypeAggregates` dictionary keys
  (MethodTable values) and resolve each to `ClrType` via `heap.GetTypeByMethodTable(mt)`
- For each type, inspect:
  - `ClrType.Fields` — count `IsObjectReference` vs value fields
  - `ClrType.IsFinalizable`, `ClrType.IsValueType`, `ClrType.Interfaces.Count`
  - `ClrType.BaseType` chain — count hops to `System.Object`
- Skip array types and primitive/scalar types
- **No per-object scan** — purely type metadata. Very fast.
- Cap at top 200 types by instance count (from TypeAggregates) to bound work

---

## Phase Assignment

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| Count reference/value fields per MT | **Phase 1** | On first encounter of each MT, inspect `obj.Type.Fields` |
| Write per-MT shape to `TypeShapeCache` | **Phase 1** | In-memory `Dictionary<ulong, TypeShapeEntry>` |
| Build `TypeShapeProfile` results | Phase 2 | Join TypeShapeCache + TypeAggregates (instance count) |

### Phase 1 Extension — `TypeShapeCache` (in-memory)

Add to `TypeIndexBuilder`:

```csharp
private readonly Dictionary<ulong, TypeShapeEntry> _shapeCache = new(capacity: 512);
private readonly record struct TypeShapeEntry(short RefFields, short ValFields, short TotalFields);
```

During `TypeIndexBuilder.Add(HeapEntry entry)`, when `!existed` (first encounter of this MT):

```csharp
ClrType? type = heap.GetTypeByMethodTable(entry.MethodTable);
if (type != null && !type.IsArray && !type.IsPrimitive)
{
    short refFields = 0, valFields = 0, total = 0;
    foreach (ClrInstanceField field in type.Fields)
    {
        total++;
        if (field.IsObjectReference) refFields++;
        else valFields++;
    }
    _shapeCache[entry.MethodTable] = new TypeShapeEntry(refFields, valFields, total);
}
```

One-time cost per unique type. For 50K types: 50K ClrType lookups.
Memory: 50K × (8 + 6) bytes = ~700KB. Stored in `HeapIndexBuildResult.TypeShapeCache`.
No new disk file — shape cache stays in memory.

### Phase 2 Computation
```
ObjectShapeAnalyzer.AnalyzeAsync(context):
  1. Read heapIndex.TypeShapeCache
  2. Join with TypeAggregates for InstanceCount per MT
  3. Resolve ClrType for IsFinalizable, IsValueType, Interfaces, BaseType chain
  4. Classify: ReferenceHeavy (refRatio > 0.6), ValueHeavy (refRatio < 0.2), etc.
  5. Emit TopReferenceHeavyTypes, TopValueHeavyTypes sorted by (refRatio × InstanceCount)
```

---

## Related Analyzers
- **`BoxingAnalyzer`** (new) — reuses `TypeShapeCache` for struct padding waste computation
- **`ArrayAnalyzer`** (new) — reuses `TypeShapeCache` with `ComponentTypeName` extension for array element type
- **`MemoryAnalyzer`** — `TypeSnapshot` gains `IsFinalizable`, `IsValueType`, `BaseTypeChainDepth` from this analyzer's output
