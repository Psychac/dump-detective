# StringAnalyzer — Design Spec

## Status
**New** (split from `MemoryLeakAnalyzer`) · Implementation Priority **1** · Effort: Low

## Report Sections Served
- §11.1 Duplicate Strings (count, ratio, top duplicates by waste and count)
- §11.2 Memory Waste & Optimisation Potential (LOH strings, interned strings, encoding waste)

## Rationale
String analysis is a standalone §11 report section. Its data is currently embedded in
`MemoryLeakDomainResult`, making it inaccessible to §11 report renderers without coupling
them to leak analysis.

---

## Domain Result

```csharp
StringDomainResult(
    int TotalStrings,
    ulong TotalStringMemoryBytes,
    int UniqueStrings,
    int DuplicatePatternCount,
    ulong DuplicateWastedBytes,
    double DuplicationRatio,
    double PctOfManagedHeap,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByWaste,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByCount,
    IReadOnlyList<LongStringEntry> VeryLongStrings,
    ulong LohStringBytes,
    int InternedStringCount,
    ulong InternedStringBytes,
    int Gen2StringCount,
    ulong Gen2StringBytes)

LongStringEntry(ulong Address, int CharLength, ulong SizeBytes)
```

---

## Implementation Strategy

- Lift `ProcessStringObjectByAddress`, `IsStringEntry`, `stringStats`, `stringMethodTables`
  verbatim from `MemoryLeakAnalyzer`
- Uses heap index fast path (`concreteCache.EnumerateIndexedEntriesAsTuples()`) already
  present in `MemoryLeakAnalyzer` — carry it forward exactly
- `DuplicationRatio = (TotalStrings - UniqueStrings) / (double)TotalStrings`
- `PctOfManagedHeap = TotalStringMemoryBytes / totalManagedBytes * 100` (from `MemoryDomainResult`)
- Very long strings: filter `Size > 85_000` during enumeration; capture address + length
- Interned strings: strings whose address falls within FOH segment ranges
  (from `SegmentAnalyzer` FOH segment list — `HeapSegmentKind.Frozen`)
- Gen2 strings: cross-reference with generation from `TypeAggregates.Gen2Count` for string MT
- `TopDuplicatesByCount` sorted by `Count` in addition to `TopDuplicatesByWaste` by `WastedBytes`

---

## Phase Assignment

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| Tag string MTs | **Phase 1** | `TypeAggregateIndexEntry.Flags` bit 0 = `IsStringType` |
| Enumerate string objects | Phase 2 | Filter ObjectIndex using pre-tagged string MT set |
| Fingerprint + deduplicate | Phase 2 | Hash string values; accumulate per-value counts |

### Phase 1 Extension
Set `Flags.IsStringType` bit (bit 0) on first encounter of any MT where
`obj.Type.Name == "System.String"`. No new disk file; the flag is in `TypeAggregates`.

### Phase 2 Computation
```
StringAnalyzer.AnalyzeAsync(context):
  1. stringMts = TypeAggregates.Where(e => e.Flags.IsStringType).Select(MT)
  2. foreach entry in cache.EnumerateIndexedEntriesAsTuples():
       if entry.MethodTable in stringMts → ProcessStringObjectByAddress(heap, entry)
  3. Build StringDomainResult from accumulated stats
```

---

## Related Analyzers
- **`MemoryLeakAnalyzer`** — source of the split; string fields removed from `MemoryLeakDomainResult`
- **`SegmentAnalyzer`** — FOH segment ranges used to identify interned strings
- **`InsightEngine`** — consumes `DuplicationRatio > 0.5` as a high-duplication alert finding
