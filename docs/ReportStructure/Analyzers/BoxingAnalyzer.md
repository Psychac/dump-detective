# BoxingAnalyzer — Design Spec

## Status
**New** · Implementation Priority **21** · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §20.1 Boxed Value Type Inventory (boxed object count/size, boxed enums, structs in collections)
- §20.2 Value Type Shape Issues (struct padding waste, large stack structs, oversized value types)

## Rationale
Boxing pressure is invisible to the heap index unless explicitly classified. Boxed enum
anti-patterns are extremely common in large codebases and have no current detector.

---

## Domain Result

```csharp
BoxingDomainResult(
    int TotalBoxedObjects,
    ulong TotalBoxedBytes,
    IReadOnlyList<BoxedTypeEntry> TopBoxedTypes,
    int BoxedEnumCount,
    ulong BoxedEnumBytes,
    int OversizedValueTypeCount,
    IReadOnlyList<StructPaddingEntry> TopPaddingWasteTypes)

BoxedTypeEntry(
    string ValueTypeName,
    int BoxCount,
    ulong TotalBoxBytes,
    bool IsEnum)

StructPaddingEntry(
    string TypeName,
    int TotalFieldBytes,
    int StructSize,
    int WastedPaddingBytes,
    double WasteRatio)
```

---

## Implementation Strategy

- Boxed value type detection: scan `TypeAggregates`; for each MT, resolve `ClrType`;
  if `!IsValueType` AND `BaseType?.Name == "System.ValueType"` or `"System.Enum"` → boxed
- Enum boxing: `ClrType.IsEnum` on the unboxed inner type (resolved via `ClrType.Name` stripping)
- Struct padding waste: for each value type with `IsValueType = true` in `TypeAggregates`,
  compute `ClrType.StaticSize - sum(ClrInstanceField.Size)` for fields using `ClrInstanceField.Offset`
- Large stack structs: sample top threads via `ClrThread.EnumerateStackObjects()`,
  collect value-type objects where `ClrType.StaticSize > 64` (bounded by thread count)
- **Uses TypeShapeCache** from Phase 1 (ObjectShapeAnalyzer) where available —
  no additional `ClrType.Fields` walk if cache is populated

---

## Phase Assignment — Entirely Phase 2

Boxed value type detection uses `TypeAggregates` + `ClrType` metadata — no heap scan needed
for aggregate counts. Deep struct padding analysis uses `TypeShapeCache` from Phase 1.

```
Phase 2:
  1. Scan TypeAggregates: resolve each MT to ClrType
  2. If !IsValueType AND BaseType.Name in ("System.ValueType", "System.Enum") → boxed
  3. Aggregate count/size from TypeAggregates.Count / TotalSize
  4. Struct padding: read TypeShapeCache for field layout, compute Offset gaps
  5. Stack struct scan: bounded ClrThread.EnumerateStackObjects() for top 10 threads
```

No new disk file required. Uses `TypeShapeCache` (ObjectShapeAnalyzer Phase 1) and `TypeAggregates`.

---

## Related Analyzers
- **`ObjectShapeAnalyzer`** (new) — provides `TypeShapeCache` which `BoxingAnalyzer` reuses for padding analysis
- **`InsightEngine`** — boxed enum anti-pattern count finding, struct padding waste threshold alert
