# StringAnalyzer — Design Spec

## Status
**Existing** · Split from `MemoryLeakAnalyzer` · Implementation Priority **1** · Effort: Low · ✅ **Completed**

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

- Uses `TypeAggregateFlags.IsStringType` (bit 0) to build `stringMts` set from `TypeAggregates`
  with **zero heap re-scan** for type identification (Phase 1 fast path)
- Uses heap index fast path (`cache.EnumerateIndexedEntriesAsTuples()`) when index is available;
  falls back to `heap.EnumerateObjects()` when not
- `DuplicationRatio = (TotalStrings - UniqueStrings) / (double)TotalStrings`
- `PctOfManagedHeap = TotalStringMemoryBytes / totalManagedBytes * 100`
  (`totalManagedBytes` derived from `TypeAggregates.TotalSize` sum, or segment spans as fallback)
- Very long strings: filter `Size >= 85_000` during enumeration; capture address + estimated char length
- Interned strings: strings whose address falls within FOH segment ranges detected via
  `ClrSegment.Kind` reflection (same approach as `SegmentAnalyzer`) — excluded from deduplication
- Gen2 strings: derived from `TypeAggregates.Gen2Count` for each string MT — **no heap re-scan**
- `TopDuplicatesByWaste` sorted by `TotalSize` (most memory wasted per pattern)
- `TopDuplicatesByCount` sorted by `Count` (most frequently repeated pattern)
- FNV-64 fingerprint used for deduplication (`Hash | Length | FirstChar | LastChar`)

---

## Progress Reporting

`StringAnalyzer` uses all three standard progress patterns:

### Pattern 1 — Phase announce
Not used explicitly; `ObjectScanCounter` starts emitting as soon as the first tick fires,
which serves as an implicit phase announce.

### Pattern 2 — `ObjectScanCounter` (main enumeration loops)

Two counter instances cover the two enumeration paths:

| Path | Label | Cadence |
|------|-------|---------|
| Indexed (`ObjectIndex.bin`) | `"scanning string objects (indexed)"` | Default: 250 K objects or 2 s |
| Raw heap fallback | `"scanning string objects"` | Default: 250 K objects or 2 s |

`Tick()` is called on **every** object visited (all MTs, not only strings) so the CLI
reflects actual scan throughput. `Complete()` is called after each path to emit the final count.

### Pattern 3 — No manual phase reports needed
The Gen2 correlation and duplicate ranking passes are O(|stringMts|) and O(|stringStats|)
respectively — fast enough that no additional progress reports are warranted.

---

## Phase Assignment

| Step | Phase | Notes |
|------|-------|-------|
| Tag string MTs (`IsStringType` flag) | **Phase 1** | `TypeAggregateIndexEntry.Flags` bit 0 |
| Enumerate + fingerprint string objects | **Phase 2** | Filter via `stringMts`; `ObjectScanCounter` ticks |
| Gen2 count derivation | **Phase 2** | Read from `TypeAggregates` — zero re-scan |
| FOH / interned detection | **Phase 2** | `ClrSegment.Kind` reflection; set built once in `AnalyzeAsync` |
| Duplicate ranking | **Phase 2** | Two fixed-size `PriorityQueue<>` min-heaps (O(N log K)) |

---

## Satellite Files Used

| File | Access Pattern | Purpose |
|------|---------------|---------|
| `ObjectIndex.bin` / `InMemoryEntries[]` (via `EnumerateIndexedEntriesAsTuples`) | Sequential in both modes | All-object enumeration, MT-filtered to strings. Memory mode iterates `InMemoryEntries[]`; disk mode reads `ObjectIndex.bin` sequentially. Both paths yield the same `(Address, MethodTable, Size)` tuples — identical output. |
| `TypeAggregateIndex` (in-memory) | Random read by MT key | `IsStringType` flag, `Gen2Count`, `TotalSize` |

No new satellite files are written by this analyzer. Both modes produce identical `StringDomainResult`.

---

## Related Analyzers
- **`MemoryLeakAnalyzer`** — source of the split; string fields removed from `MemoryLeakDomainResult`
- **`SegmentAnalyzer`** — FOH segment address detection reuses same `ClrSegment.Kind` reflection pattern
- **`InsightEngine`** — consumes `DuplicationRatio > 0.5` as a high-duplication alert finding
- **`StringFindingGenerator`** — emits Critical/Warning/Info findings from `StringDomainResult`
- **`StringTrendComparer`** — tracks 9 scalar metrics across dump series
- **`StringSectionBuilder`** — renders §11 report section with 3 collapsible tables
