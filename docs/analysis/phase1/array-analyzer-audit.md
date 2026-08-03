# ArrayAnalyzer — Phase 1 Audit

**Analyzer:** `ArrayAnalyzer` (§22.1–22.4)
**Files reviewed:** `ArrayAnalyzer.cs`, `ArrayDomainResult.cs`, `ArrayAnalysisOptions.cs`, `ArrayFindingGenerator.cs`, `ArraySectionBuilder.cs`, `ArrayTrendComparer.cs`, `ArrayAnalyzerDiscrepancyTests.cs`, `TypeAggregateIndexEntry.cs`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role
Covers four sub-domains: array population aggregate (§22.1), individual large-array instances (§22.2), sparse/wasteful reference-type arrays (§22.3), and multi-dimensional vs jagged analysis (§22.4). The analyzer is index-first: it reads `TypeAggregates` for population stats and `LargeObjectIndex.bin` for LOH instances, making it very cheap on large dumps.

### Coverage Gaps
- **Value-type sparse arrays are fully excluded.** The comment says "exception-driven null-counting" is avoided, but `ClrArray.GetStructValue` exists; zero-density for structs with a numeric field (e.g. `int[]`, `double[]`) is high-value for finding oversized pre-allocated buffers.
- **No GC generation breakdown per array type.** `TypeAggregateIndexEntry` carries `Gen0Count`, `Gen1Count`, `Gen2Count` but `ArrayAnalyzer` ignores them. Arrays surviving to Gen2/LOH are a direct fragmentation signal.
- **No per-type instance count vs LOH instance count ratio.** It is unclear from the report which types have most of their instances on the LOH vs SOH.
- **Jagged analysis is limited to type-name presence.** The §22.4 title promises jagged vs multi-dim analysis; the only implementation is counting rank ≥ 2 types with a global threshold warning.
- **No retained-bytes rollup per array type.** The report shows `TotalBytes` (sum of array headers + element storage), but no indication of how much live memory each type retains transitively.

### Expansion Opportunities
- **Pinned array detection:** arrays under `GCHandle` of kind `Pinned` cause LOH-like behaviour at any size; `RootIndexReader` already stores GC handles.
- **Oversized `ArrayPool` buffer detection:** `byte[]` instances on LOH that are ≥ 128 KB and a multiple of a power-of-two are likely `ArrayPool` rentals never returned; flaggable with a heuristic.
- **Large string-array concentration:** `string[]` arrays with many non-null entries are common in caching patterns; combining with object reference sizes would add value.
- **Cross-analyzer correlation:** `LohFragmentationAnalyzer` already reads `LargeObjectIndex.bin`; the two analyzers duplicate the same LOH index reads.

### Architectural Observations
- `IAnalyzer` interface `Tags` and `Order` are not overridden. Tags default to `[]`, order to `0`. The finding generator and section builder already assign tags on findings; exposing analyzer-level tags would improve CLI filtering and catalog discovery.
- `IsThreadSafe` is not on the interface (CLAUDE.md lists it, but the actual `IAnalyzer` interface does not). No issue.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths
- Three-panel layout (population, large instances, sparse arrays) is logical and progressive.
- `LargeArrayEntry` exposes `Address`, `Length`, `Rank`, `Size` — sufficient to jump directly to WinDbg.
- `ScanLimited` key metric warns the operator when sparse sampling was capped.
- Anti-pattern labels (`byte[] > 1 MB`, `string[] > 10k`) in `BuildLargeRows` add instant actionability to the large-instance table.
- LOH finding severity escalates from Warning to Critical based on a threshold — good signal layering.

### Weaknesses
- **One sparse finding per run maximum.** `ArrayFindingGenerator` contains a hard `break` after emitting the first sparse finding. If the second-largest sparse array is more diagnostically significant (different type, different module), it is silently suppressed.
- **Multi-dim finding fires on count alone, ignoring memory.** 1,000 `short[,]` arrays at 200 bytes each (200 KB total) trigger the same warning as 1,000 `double[,]` arrays at 80 MB each.
- **No average or per-instance size in the type table.** An operator cannot tell from `Count=5, TotalBytes=500 MB` what a single array instance costs. An average-size column would immediately distinguish "a few huge allocations" from "many medium ones".
- **Sparse table shows only `SparseRatio`** (sampled fraction from the single `SampleAddress` instance per type). This ratio represents one instance, not the whole type population. The report does not communicate this sampling limitation inline.
- **LOH finding uses `TopLargeArrays[0].ElementTypeName`** as the "largest array element type". If `TopLargeArrays` is populated from the fallback path (SampleAddress per type, not size-sorted), index [0] is not guaranteed to be the largest.
- **No allocation site or module attribution.** The report cannot tell the engineer which assembly or namespace owns the largest array types.
- **`TrendComparer` only tracks three global metrics.** Per-type trend (`array.type.bytes` per `ElementTypeName`) is captured but `Compare` only emits three fixed deltas, losing per-type regression detection between dumps.

### Missing Diagnostics
- Which GC generation each array type's instances occupy (survival signal).
- Whether large arrays are pinned.
- Per-type average instance size.
- Module/assembly owning each array type.

### Missing Statistics
- `TotalArrayBytes` as a percentage of total heap bytes.
- LOH array bytes as a percentage of total LOH bytes.
- Aggregate wasted bytes across all sparse arrays (currently only per-entry).

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Issues

**Rank detection via string parsing instead of `ClrArray.Rank`:**
```csharp
// current — expensive per-iteration string scan
int bracket = name.LastIndexOf('[');
for (int ci = bracket; ci < name.Length; ci++)
    if (name[ci] == ',') commas++;
```
`ClrArray.Rank` is a direct CLR property. The type-aggregate loop could be split: use string parse for `TypeAggregateIndexEntry` aggregation (where no `ClrObject` exists), but the `LargeArrayEntry` path already calls `arr.Rank` from ClrMD — this is correct.

**`StaticSize` misused for wasted-bytes estimation:**
```csharp
ulong elemSize = (ulong)(obj.Type.ComponentType?.StaticSize ?? 8);
ulong wastedBytes = (ulong)(arr.Length * sparseRatio * (double)elemSize);
```
For a reference-type component (e.g. `object[]`), `ComponentType.StaticSize` is the object header size of the *component type* (typically 16–24 bytes for a managed object), not the slot size in the array. An array element slot for a reference type is always 8 bytes (pointer size on 64-bit). Using `StaticSize` inflates wasted-byte estimates by 2–3×. The correct value is `IntPtr.Size` (8) for all reference-type arrays.

**`GetObjectValue` on inner loop without `ClrType` guard:**
```csharp
ClrObject elem = arr.GetObjectValue(i);
if (!elem.IsValid || elem.Address == 0) nullCount++;
```
`GetObjectValue` on a value-type element would throw; the outer guard `clrType.ComponentType?.IsObjectReference == true` makes this safe, but this invariant is non-obvious and the safety net is several hundred lines away.

### Platform Utilization Issues

**`LargeObjectIndex.bin` and `LohFragmentationAnalyzer` duplication:**
Both `ArrayAnalyzer.ReadLargeArraysFromIndex` and `LohFragmentationAnalyzer` open `CacheSectionId.LargeObjects` independently. The binary read loop in `ArrayAnalyzer` (24-byte records) is a copy of the same logic in `LohFragmentationAnalyzer`. This should live in `LargeObjectTracker` (which has a doc comment naming both consumers).

**`Gen0Count`/`Gen1Count`/`Gen2Count` from `TypeAggregateIndexEntry` are unused:**
`AllocationPatternAnalyzer`, `GCGenerationAnalyzer`, and `FinalizableObjectAnalyzer` all consume generation breakdown from the same structure. `ArrayAnalyzer` ignores it entirely, missing the survival-to-Gen2/LOH ratio that would dramatically improve LOH findings.

**`AnalysisContext.Cache.GetSampleInstanceAddress` not used:**
`DominatorAnalyzer` calls `cache.GetSampleInstanceAddress(mt)` to get sample addresses without accessing `TypeAggregates` directly. `ArrayAnalyzer` accesses `TypeAggregates` through a manual cast to `HeapAnalysisCache`, bypassing the cache abstraction.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Diagnostics Not Currently Extracted

| Diagnostic | Value | Effort |
|---|---|---|
| Per-type GC generation breakdown (% Gen0/Gen1/Gen2/LOH) | High — identifies long-lived vs short-lived array pressure | Low — data in `TypeAggregateIndexEntry` |
| Pinned arrays (via GC handle index) | High — pinning fragments SOH like LOH | Medium — needs `RootIndexReader` integration |
| Average array instance size per type | Medium — distinguishes "few huge" vs "many medium" | Low — `TotalBytes / Count` |
| Value-type sparse arrays (e.g. `int[]`, `float[]`) | Medium — oversized pre-allocated numeric buffers | Medium — `ClrArray.GetStructValue` for numeric fields |
| Aggregate total wasted bytes (sum across all sparse entries) | Medium — single headline number for the sparse section | Low |
| `ArrayPool<T>` unreturned buffer detection | Medium — LOH `byte[]` at power-of-two sizes ≥ 128 KB | Low — heuristic check |
| Module/assembly attribution for top array types | Medium — directs ownership to a team | Low — `ClrType.Module.Name` |
| Array type concentration (`% of heap bytes` per type) | Medium — identifies dominant consumers instantly | Low |
| Sparse ratio confidence indicator | Low-Medium — single-instance sample may not represent population | Low |

### Investigation Workflow Gaps
- No address in the sparse table links to the instance for immediate `!dumparray` in WinDbg.
  *(It is present — `SparseArrayEntry.Address` is in the table. No gap here.)*
- No linkage between the multi-dim warning and the type table showing which types are multi-dim.

---

## Audit Area 5 — Performance, Memory & Scalability

### Assessment
The analyzer does not scan the heap at all in the aggregate path; it reads `TypeAggregates` (an in-memory dictionary, O(N) over distinct types, not objects). This is the correct design and scales to any dump size.

### Performance Issues

**Step 3 LOH fallback iterates `typeAggregates` a second time:**
```csharp
if (topLargeArrays.Count == 0)
{
    foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
    ...
}
```
This is a second O(T) pass over type aggregates where T = distinct types. On dumps with 50,000+ distinct types this is measurable. The LOH fallback candidates could be accumulated during Step 1 alongside `sparseCandidates` in a single pass.

**`sparseCandidates.Sort` before sampling is correct and efficient** — it ensures the most impactful arrays are probed before `TopSparseLimit` is reached, avoiding sorting post-sampling.

**Sparse sampling opens one `ClrObject` per candidate type, not per object.** Only one instance per type (the `SampleAddress`) is examined. This means the sparse ratio for a type is derived from a single array; a type with 1,000 instances may have varied density. This is an accuracy trade-off, not a perf issue, but should be documented.

**`sparseCandidates` initial capacity is `64`; on large dumps 500+ ref-type array types are plausible.** The list will grow and copy. Use `capacity: Math.Min(typeAggregates.Count / 4, 512)` or similar.

### Scalability
- On 10 GB+ dumps: type aggregate count grows ~linearly; the current approach remains O(T) and bounded.
- `SparseSampleLimit = 500` (default Balanced) with `SampleStride = 100` means at most 500 `ClrObject` lookups and up to `500 × (arrayLength / 100)` index operations. For arrays of 10,000 elements at stride 100, that is 100 GetObjectValue calls per candidate — 50,000 total at worst. Acceptable.
- Disk index reads in `ReadLargeArraysFromIndex` use `stackalloc byte[24]` correctly. No heap allocation per record.

---

## Audit Area 6 — Correctness & Confidence

### Critical Issues

**`StaticSize` used as pointer slot size for reference-type arrays (incorrect):**
```csharp
ulong elemSize = (ulong)(obj.Type.ComponentType?.StaticSize ?? 8);
```
`ClrType.StaticSize` for a reference type is the object's minimum size (header + fields), typically 16–24 bytes. An array of `object` holds 8-byte references (pointers), not inline objects. This makes `WastedBytes` estimates 2–3× too high for most reference-type arrays. The fix is `(ulong)IntPtr.Size` for all reference-element arrays (since `IsObjectReference == true` is already the guard).

**Aggregate `Count` in `typeMap` silently overflows on large heaps:**
```csharp
typeMap[key] = (existing.Count + (int)Math.Min(e.Count, int.MaxValue), ...)
```
`existing.Count` is an `int` accumulated across multiple MethodTable entries with the same element type name. If two variants of `byte[]` each have 2,147,483,647 entries, the sum overflows `int.MaxValue` silently (C# arithmetic wraps in unchecked context). Change accumulator to `long`.

### Medium Issues

**LOH fallback path is not size-sorted.** The fallback iterates `typeAggregates` in dictionary enumeration order and stops at `TopLargeLimit`. There is no guarantee the first N entries are the largest LOH arrays. Result: `findings.Add` uses `TopLargeArrays[0]` as the "top" type, which may not be the largest.

**Sparse ratio extrapolation assumes uniform distribution:**
```csharp
NullOrZeroCount: (int)Math.Min((long)(nullCount * ((double)arr.Length / sampleLen)), int.MaxValue)
```
The extrapolated count assumes every `SampleStride`-th element is representative. For arrays with clustered null regions (common in cache/slot patterns), this over- or under-estimates. The report does not convey sampling methodology to the reader.

**Multi-dim detection from type name `LastIndexOf('[')` + comma count:**
This works for standard CLR names (`T[,]`, `T[,,]`) but would fail for compiler-generated or dynamically-emitted types with unusual name formats. ClrMD provides `ClrType.StaticSize` and rank is readable via `ClrArray.Rank` for instances. For the aggregate path (no instances), string parsing is the only option — but it should handle edge cases (`null` name, malformed name) defensively.

### Edge Cases
- Arrays of arrays (`T[][]`) will appear as `T[]` in the component type name. Their nesting structure is invisible in the report.
- Fixed-size buffers (`fixed byte buf[N]`) are value-type array-like but would not appear in `IsArrayType` aggregates — no issue.
- Zero-length arrays (`new T[0]`) are valid managed objects; they contribute to counts but `arr.Length == 0` skips the sparse sample guard correctly via `SparseSampleMinLength`.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS
- `!dumparray <addr>` provides rank, lengths, and element values for a specific instance — directly actionable after finding an address in the report. **Parity:** ArrayAnalyzer provides addresses in both large and sparse tables. Gap: no `!eeheap -gc` style summary of array space relative to total heap.
- `!dumpheap -type [] -stat` groups all array types with count and total bytes — equivalent to `TopArrayTypesBySize`. **Parity: good.**

### PerfView
- Memory dumps analysis provides object type breakdown by size with generation info. **Gap:** DumpDetective does not show per-type generation breakdown for arrays.
- PerfView shows `% of heap` per type inline. **Gap:** DumpDetective shows absolute bytes but not percentage of total heap.

### Visual Studio Memory Usage
- Groups by type, shows count + inclusive/exclusive size. No sparse detection. **Advantage for DumpDetective:** sparse array detection and LOH heuristics are richer.

### JetBrains dotMemory
- Dominators, largest objects, array/collection analysis by type. Sparse array detection is a key feature with visual heat-maps. **Gap:** DumpDetective's sparse detection is limited to reference-type 1-D arrays; dotMemory covers value-type arrays as well.
- dotMemory shows `% null` per array type across all instances. DumpDetective samples one instance per type. **Gap:** multi-instance null density aggregation.

### Competitive Opportunities
1. Per-type generation breakdown would match PerfView's dump analysis output.
2. Value-type sparse arrays (at minimum `T[]` where `T` is a struct with only primitive fields) would close the dotMemory gap.
3. `% of heap bytes` column in the type table would match VS Memory Usage and WinDbg `!dumpheap -stat` readability.

---

## Final Executive Summary

### Overall Assessment
**Score: 65 / 100**

The analyzer is structurally well-designed: index-first, no heap scan for population stats, bounded sparse sampling, disk-index fallback. The three-table report layout is clear and actionable. However, there is one correctness bug affecting all wasted-byte estimates, a potential overflow in the type count accumulator, missing generation-breakdown context (available for free), and several diagnostic gaps that reduce utility compared to standard tools.

**Production readiness:** Conditional. The `WastedBytes` calculation bug means sparse findings report inflated wasted memory (2–3× for typical reference-type arrays). This is not crash-prone but misleads investigations.

### Priority Roadmap

| Priority | Recommendation | Status | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|---|
| ~~**P0**~~ | ~~Fix `StaticSize` → `IntPtr.Size` for reference-type array element slot size in wasted-bytes calculation~~ | ✓ DONE | High — all sparse findings show inflated numbers | Low | High | Improvement |
| ~~**P0**~~ | ~~Change `typeMap` count accumulator from `int` to `long` to prevent silent overflow~~ | ✓ DONE | High — wrong aggregate count on large heaps | Low | High | Improvement |
| **P1** | Add per-type GC generation breakdown (% Gen2 + LOH) using existing `TypeAggregateIndexEntry` fields | ✓ DONE | High — distinguishes transient vs retained array pressure | Low | High | Improvement |
| **P1** | Remove hard `break` in `ArrayFindingGenerator` sparse loop; emit top-N distinct findings |   | Medium — suppresses valid warnings | Low | High | Improvement |
| **P1** | Sort LOH fallback candidates by `LohSize` before taking `TopLargeLimit` |   | Medium — ensures "top" arrays are actually the largest | Low | High | Improvement |
| **P1** | Accumulate LOH fallback candidates in Step 1 to eliminate second `typeAggregates` pass |   | Medium — eliminates redundant O(T) scan | Low | High | Improvement |
| **P1** | Add `% of total heap` column to the type table |   | Medium — matches standard tooling readability | Low | High | Improvement |
| **P2** | Multi-dim finding: weight by memory (`TotalBytes`) not count alone |   | Medium — avoids false alarms on tiny multi-dim arrays | Low | High | Improvement |
| **P2** | Add average instance size column (`TotalBytes / Count`) to type table |   | Medium — distinguishes "few huge" from "many small" | Low | High | Improvement |
| **P2** | Deduplicate `LargeObjectIndex.bin` read with `LohFragmentationAnalyzer` via `LargeObjectTracker` |   | Medium — removes copy-paste binary reader | Medium | High | Evolution |
| **P2** | Add module/assembly attribution for top array types via `ClrType.Module.Name` |   | Medium — directs ownership to responsible team | Low | Medium | Improvement |
| **P2** | Add aggregate total wasted bytes summary metric for sparse section |   | Low-Medium — headline number for the section | Low | High | Improvement |
| **P3** | Value-type sparse array detection for numeric types (`int[]`, `float[]`) |   | Medium — closes dotMemory gap | Medium | Medium | Improvement |
| **P3** | Pinned array detection via GC handle root index |   | Medium — identifies SOH fragmentation risk | Medium | High | Improvement |
| **P3** | `ArrayPool<T>` unreturned buffer heuristic (`byte[]` LOH at power-of-two sizes ≥ 128 KB) |   | Low-Medium — common production anti-pattern | Low | Low | Improvement |
| **P3** | `sparseCandidates` initial capacity tuned to `Math.Min(typeAggregates.Count / 4, 512)` |   | Low — avoids list growth copies | Low | High | Improvement |

### Final Verdict

1. **Is the analyzer production-ready?** Conditionally. The `WastedBytes` inflation bug (P0) produces misleading sparse findings; should be fixed before shipping the sparse section to end users. All other sections (population, large instances) are correct.

2. **Highest-impact improvements:** Fix `StaticSize` → `IntPtr.Size` (P0), fix `int` overflow in count accumulator (P0), add GC generation breakdown (P1), remove single-finding cap in sparse generator (P1).

3. **Platform evolution opportunities:** Centralising the `LargeObjectIndex.bin` reader in `LargeObjectTracker` (already documented as serving both consumers) avoids binary-format divergence risk when the LOH record layout changes.

4. **Highest engineering return:** P0 and P1 items require minimal code changes and yield correctness fixes plus significant diagnostic richness for free (generation data is already in the index). Combined effort is under one day of work.
