# ArrayAnalyzer — Design Spec

## Status
**New** · Implementation Priority **17** · Effort: Medium

## Report Sections Served
- §22.1 Array Population Overview (count, size, by element type, by rank, by generation)
- §22.2 Large Array Analysis (LOH arrays, pooling candidates, multi-dim anti-patterns)
- §22.3 Sparse & Wasteful Arrays (null/zero density, over-capacity backing arrays)
- §22.4 Jagged vs Multi-Dimensional (rank analysis, usage recommendations)

## Rationale
Arrays are the single most common source of LOH pressure yet no current analyzer classifies
them by element type, rank, or fill density.

---

## Domain Result

```csharp
ArrayDomainResult(
    int TotalArrayObjects,
    ulong TotalArrayBytes,
    int MultiDimArrayCount,
    int LohArrayCount,
    ulong LohArrayBytes,
    IReadOnlyList<ArrayTypeProfile> TopArrayTypesBySize,
    IReadOnlyList<LargeArrayEntry> TopLargeArrays,
    IReadOnlyList<SparseArrayEntry> TopSparseArrays)

ArrayTypeProfile(
    string ElementTypeName,
    int Rank,
    int Count,
    ulong TotalBytes,
    bool IsMultiDimensional)

LargeArrayEntry(
    ulong Address,
    string ElementTypeName,
    int Length,
    int Rank,
    ulong Size)

SparseArrayEntry(
    ulong Address,
    string ElementTypeName,
    int Length,
    int NullOrZeroCount,
    double SparseRatio,
    ulong WastedBytes)
```

---

## Implementation Strategy

- Phase 1 tags array MTs in `Flags` bit 4; `TypeShapeCache` extended with `ComponentTypeName`
- Phase 2 filters `TypeAggregates` by `IsArrayType` flag for aggregate counts (no object scan)
- Large arrays: filter `ObjectIndex.bin` to array MTs where `Size > 85_000`; read
  `ClrObject.AsArray().Rank` and `ClrType.ComponentType.Name` per large array
- Sparse analysis: bounded element sampling on arrays > 10K elements —
  check every 100th element for null/zero; extrapolate. Cap at 500 arrays total
- Multi-dim: `ClrArray.Rank > 1` flag

---

## Phase Assignment

| Step | Phase | Notes |
|------|-------|-------|
| Tag `IsArrayType` in `TypeAggregateIndexEntry.Flags` bit 4 | **Phase 1** | `ClrType.IsArray` on first MT encounter |
| Store `ComponentTypeName` per array MT | **Phase 1** | Extend `TypeShapeCache` with element type name string |
| Array population aggregate (count, size, rank) | Phase 2 | Filter `TypeAggregates` by `IsArrayType` flag |
| Large array individual entries | Phase 2 | Filter `ObjectIndex.bin` to array MTs where `Size > 85_000` |
| Sparse sampling | Phase 2 | Bounded `ClrObject.AsArray().GetObjectValue()` on large arrays |

No new disk file required. Uses extended `TypeAggregateIndexEntry.Flags` and `TypeShapeCache`.

`LargeObjectIndex.bin` (from `LohFragmentationAnalyzer` Phase 1) also provides top-100 large
objects as a starting point for large array identification.

---

## Related Analyzers
- **`LohFragmentationAnalyzer`** — `LargeObjectIndex.bin` consumer; overlap for top-N largest LOH objects
- **`ObjectShapeAnalyzer`** (new) — `TypeShapeCache` extended with `ComponentTypeName` for array element type
- **`CollectionAnalyzer`** — backing array fill rate (§22.3 over-capacity collections)
- **`InsightEngine`** — LOH array pressure finding, multi-dim array anti-pattern warning
