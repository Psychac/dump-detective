# CollectionAnalyzer — Phase 1 Audit

> Protocol: [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)
> Reviewer mindset: Principal .NET Runtime Engineer · ClrMD Expert · CLR & GC Specialist ·
> Memory Diagnostics Engineer · Production SRE · Performance Engineer · Software Architect

**Inputs reviewed**

| Component | File |
|---|---|
| Analyzer | `src/DumpDetective.Analysis/Analyzers/CollectionAnalyzer.cs` (≈1 702 lines) |
| Helpers | `src/DumpDetective.Analysis/Analyzers/CollectionAnalysisHelpers.cs` |
| Domain model | `src/DumpDetective.Analysis/Models/CollectionDomainResult.cs` |
| Generation breakdown | `src/DumpDetective.Analysis/Models/CollectionGenerationBreakdown.cs` |
| Options | `src/DumpDetective.Core/Options/CollectionAnalysisOptions.cs` |
| Finding generator | `src/DumpDetective.Reporting/FindingGenerators/CollectionFindingGenerator.cs` |
| Section builder | `src/DumpDetective.Reporting/SectionBuilders/CollectionSectionBuilder.cs` |
| Trend comparer | `src/DumpDetective.Analysis/Trend/Comparers/CollectionTrendComparer.cs` |
| Insight engine | `src/DumpDetective.Analysis/Insight/InsightEngine.cs` (collection slice) |
| Unit tests | `CollectionAnalyzerHeapIndexScanTests`, `CollectionAnalysisHelpersTests`, `CollectionAnalysisElementSizeTests` |
| Integration test | `CollectionAnalyzerDiscrepancyTests` |
| Platform context | Phase 0 analyzer catalog |

---

## Audit Area 1 — Role & Opportunity Assessment

### Current role

`CollectionAnalyzer` enumerates BCL collection instances from the heap index, reads their internal
array-backed state via ClrMD field reflection, computes a fill-rate and estimated wasted memory per
instance, and surfaces the top-N most wasteful collections. It covers `Dictionary<K,V>`,
`List<T>`, `HashSet<T>`, `Queue<T>`, `ArrayList`, `Stack<T>`, `SortedList<K,V>`, `SortedSet<T>`.

The role is well-defined, single-purpose, and clearly distinguishable from other analyzers. No
other analyzer duplicates fill-rate logic.

### Coverage gaps

| Gap | Evidence |
|---|---|
| `System.Collections.Immutable.*` entirely absent | `BclCollectionNamespacePrefixes` contains only `System.Collections.`, `...Generic.`, `...Concurrent.` — the `System.Collections.Immutable` namespace is missing. Immutable builders can leave large pre-allocated backing arrays. |
| `ImmutableArray<T>` — different structure | `ImmutableArray<T>` wraps a plain `T[]` field named `_array` with no count field; a dedicated probe path is needed. |
| Concurrent collections explicitly excluded with no analysis | `ConcurrentDictionary`, `ConcurrentBag`, `ConcurrentQueue`, `ConcurrentStack` are excluded. Their internal segment/node structures do not have a single fill-rate, but "total payload vs. total allocated nodes/segments" is still a meaningful metric. |
| `IncludeQueueAnalysis` option has no effect | The field is declared, documented, and read from config but **never checked** in `OnHeapEntry` or `ProcessEntry`. Queues are always analyzed regardless of the option value. Dead option. |
| "Empty-but-allocated" collections not distinguished | A collection with `capacity = 100_000`, `count = 0` has 100 % waste. Under the current code, for reference-type elements `WastedMemory = 100_000 × 8 = 800 KB`, which clears the 10 KB threshold and is reported. But for small empty collections (capacity < ~1 280 reference slots) the waste falls under the threshold and is silently dropped — reasonable, but no summary statistic counts these at all. |
| `string` collections penalized by proxy | A `List<string>` of 1 M strings with 50 % fill reports wasted-memory = 500 K × 8 bytes (pointer size). The actual memory waste includes the unreachable `string` objects themselves. The analyzer only accounts for wasted array slots, not the reachable-but-not-useful string heap. (Separate concern, but worth noting as a limitation.) |

### Unexpected / misplaced functionality

- `CollectionAnalyzer` carries its own static `s_fieldLayoutCache` — a `ConcurrentDictionary<ulong, FieldLayout>` — with custom CAS-update logic. This is a private, hidden, cross-instance cache that duplicates the role of `HeapAnalysisCache`/`TypeMetadataCache`. No other analyzer owns a static unbounded type-layout cache.
- `PopulateRootDescriptions` calls `new ReferenceChainAnalyzer()` directly and invokes `AnalyzeObject`. This creates an undeclared dependency on `ReferenceChainAnalyzer` (not expressed in any interface or registration) and duplicates work that the pipeline dispatcher could route through the shared `ReferenceChainAnalyzer` instance.

### Architectural observations

- `CollectionAnalyzer` is `public class`, not `sealed`. `CreateWorkerInstance()` returns `new CollectionAnalyzer(_options, _logger)` explicitly, so a subclass would silently break the parallel scan. Should be `sealed` or the factory should use the concrete type explicitly.
- `Tags` and `Order` from `IAnalyzer` are not overridden. The Phase 0 catalog assigns order 240 and tags `["collections"]`. These defaults are not wired.
- The explicit `public void Dispose() { }` override is redundant — `IAnalyzer` provides a default no-op `Dispose()` and the analyzer holds no resources.

### Expansion opportunities

- **Per-instance memory savings estimate**: compute `WastedMemory + (current capacity → trimmed capacity) → estimated post-TrimExcess size` and surface it as an actionable number.
- **Capacity growth pattern signal**: a power-of-2 capacity suggests normal doubling growth (growth pressure); a non-power-of-2 suggests explicit initial capacity (expected). This distinguishes "over-allocated by design" from "grew unexpectedly".
- **Owner type hinting**: use the reverse reference index to identify the immediate referrer of the most wasteful collections — surfaces the owning service/component without full BFS.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Per-kind inventory (total counts) gives a fast overview of the collection landscape.
- Generation breakdown reveals whether waste is concentrated in Gen2/LOH (long-lived = structural) vs Gen0 (transient = less urgent).
- Queue-specific head/tail and contiguous-free-segment metrics are genuinely useful for circular-buffer diagnostics.
- `SizeEstimateConfidence` ("High"/"Low") signals to the engineer how reliable the wasted-byte number is.
- `DetectionMethod` (the field name used to locate the backing array) aids manual verification.

### Weaknesses

**Finding generator produces a single aggregate finding**

`CollectionFindingGenerator.Generate` always returns exactly one `InsightFinding`. A dump with
10 M wasteful dictionaries and 5 M wasteful queues emits one undifferentiated message. Per-kind
findings with kind-specific recommendations would be actionable; the current single summary is not.

**`WasteCountsByKind` is total inventory, not wasteful count — semantic mismatch**

In both `BuildStatsFromParticipantState` (line ~350) and `RunParallelCollectionAnalysis` (line
~650):

```csharp
var wasteCountsByKind = new Dictionary<CollectionKind, int>(8)
{
    [CollectionKind.Dictionary] = stats.Dictionaries,   // ← ALL dictionaries
    [CollectionKind.List]       = stats.Lists,           // ← ALL lists
    ...
};
stats.WasteCountsByKind = wasteCountsByKind;
```

These are **total collection counts by kind**, not counts of wasteful collections by kind. Yet
`CollectionSectionBuilder` titles this table "Wasteful collections by kind" and
`CollectionFindingGenerator` prints it as a per-kind waste breakdown. `CollectionTrendComparer`
tracks it under `"collection.waste.kind.count"` — all incorrect interpretations. The wasteful
count per kind is computed per-entry in `OnHeapEntry` via `_wastefulCount++` but is not
disaggregated per kind; that count is lost. In `RunParallelCollectionAnalysis` the per-kind arrays
`wasteCountByKind` and `wasteBytesByKind` are accumulated in `LocalWasteAccumulator` but never
surfaced in `CollectionStatistics` or `CollectionDomainResult`.

**`RootDescription` quality is binary and non-actionable**

`PopulateRootDescriptions` assigns either `"Retained (reference path found)"` or `"No root path
found (within budget)"`. An SRE investigating a 2 GB Dictionary in Gen2 gets no path detail —
which static field, which thread root, which event handler chain holds it alive. The full root path
exists inside `ReferenceChainAnalyzer` but is not propagated to the output model.

**Section builder shows Queue-only columns for all collection types**

`CollectionSectionBuilder` emits Head, Tail, LargestFreeGap, FreeSegments columns for the shared
"Wasteful collections" table. For Dictionary/List/HashSet rows these columns always render "—",
adding visual noise and making the table unnecessarily wide (15 columns).

**No per-collection recommendation**

The report never tells the engineer *what to do* about a specific collection. A Dictionary at
5 % fill warrants a different action than a List — `dict.TrimExcess()` vs `list.TrimExcess()` vs
"redesign initial capacity". This is entirely absent.

**`FillRate = 0 %` case has no label**

A completely empty but allocated collection (fill = 0 %) is indistinguishable in the report from a
1 %-filled collection. No "empty allocated" category exists.

### Missing diagnostics

- Actual wasteful count per kind (distinct from total inventory count).
- Total wasted bytes per kind.
- Root path detail (stack frame / static field name / event publisher name).
- Per-collection recommendation string.
- "Empty allocated" bucket count and total bytes.
- Ratio of wasteful collections to total (waste rate %).

---

## Audit Area 3 — ClrMD & Platform Utilization

### Static `s_fieldLayoutCache` — unbounded, undocumented, cross-session

```csharp
private static readonly ConcurrentDictionary<ulong, FieldLayout> s_fieldLayoutCache
    = new(concurrencyLevel: 4, capacity: 256);
```

This is a **process-lifetime static** cache keyed by `MethodTable` address. MethodTable addresses
are only stable within a single dump session; they are meaningless across dumps and can collide
across dumps of different applications. In a host process that analyzes multiple dumps sequentially
(e.g., batch analysis, service host), this cache will:

1. Grow unboundedly — one entry per distinct MethodTable across all dumps.
2. Return stale/wrong field layouts for a dump of a different runtime if MethodTable addresses
   happen to coincide.

The correct scope for this cache is per-dump-session (i.e., tied to `HeapAnalysisCache` or
`AnalysisContext`), not static.

### `TrySetComputedElementSize` — logic bug on first-write path

```csharp
if (!s_fieldLayoutCache.TryGetValue(methodTable, out var existing))
{
    var newLayout = new FieldLayout(existing.SizeField, existing.CountField, ...);
    if (s_fieldLayoutCache.TryAdd(methodTable, newLayout))
        return;
    continue;
}
```

When `TryGetValue` returns `false`, `existing` is the **default value of `FieldLayout`** (all
reference fields null, numeric fields 0). The `newLayout` then has `SizeField = null`,
`CountField = null`, `ItemsField = null`, etc. — preserving none of the previously built layout
— and inserts this nulled entry into the cache. Any subsequent `GetOrAdd` call for the same MT
will hit the corrupt entry and return nulled fields, causing every `Analyze*` method to return
`null` ("no fields found") for any collection type whose first `TrySetComputedElementSize` call
races into the not-found branch.

The intent appears to be: "if not in cache, insert with just the element size". The fix is to
call `GetOrAdd` to retrieve the full layout first, then CAS-update only if
`ComputedElementSize == 0`.

### `heapLock is object` — always true; `SerializeHeapAccess` is dead option

In `RunParallelCollectionAnalysis`:

```csharp
var heapLock = new object();
...
if (heapLock is object)          // ← always true — a new object() is never null
{
    lock (heapLock) { waste = AnalyzeDictionary(heap, address); }
}
else
{
    waste = AnalyzeDictionary(heap, address);  // ← unreachable
}
```

`new object()` is never null, so `heapLock is object` is always `true`. **All ClrMD heap calls in
the parallel path are serialized**. `_options.SerializeHeapAccess` exists to control this behavior
but is never consulted. The `else` branch (lock-free) is dead code. On a 16-core host, the
parallel scan yields zero speedup over sequential for the heap-read portion.

The intended guard was likely `if (_options.SerializeHeapAccess)` or the lock should be
conditionally held via a helper.

### `ResolveCollectionKindConcurrent` — address captured in closure, called under lock

`ResolveCollectionKindConcurrent` is called inside `lock (heapLock)` and uses a `GetOrAdd`
factory capturing `(heap, address)`. Because the lock serializes the `GetOrAdd` factory
invocations, there is no race. However, for a ConcurrentDictionary whose factory must be
idempotent, the closure captures a specific `address` — which is the address that triggered the
first miss for that MethodTable. This means the closure is correct (type name is the same for all
objects of the same MT), but the redundancy of calling `GetOrAdd` under a lock rather than
double-checked with `TryGetValue` first is a minor inefficiency.

### `FindFirstArrayField` / `FindFirstInt32Field` / `FindFieldByNameContains*` — redundant with `GetOrBuildFieldLayout`

Four standalone static helper methods (`FindFirstArrayField`, `FindFirstInt32Field`,
`FindFieldByNameContains`, `FindFieldByNameContainsAny`) are called from `AnalyzeQueue` despite
`GetOrBuildFieldLayout` already performing equivalent field discovery and caching results. The
queue path does a mix of `GetOrBuildFieldLayout` and uncached fallback calls, potentially
re-enumerating `ClrType.Fields` multiple times per object type.

### Missing `_freeCount` accounting in Dictionary

`Dictionary<K,V>` internal layout (all .NET versions):

```
_buckets : int[]
_entries : Entry[]   ← capacity of logical table
_count   : int       ← count of USED + DELETED (free-list) entries
_freeCount : int     ← count of entries in the free list (deleted)
```

Live entry count = `_count - _freeCount`. The analyzer reads `_count` as the "live count" and
computes `fill = _count / _entries.Length`. For a dictionary that had many deletions and no
rehash, `_freeCount` may be large, causing the analyzer to significantly **underestimate waste**
(it thinks there are more live entries than there actually are). The fix is to subtract
`_freeCount` (if present) from `_count` before computing fill rate.

### `HeapAnalysisCache` not used for field-layout caching

`HeapAnalysisCache` provides a `TypeMetadataCache` abstraction. The `s_fieldLayoutCache` could be
implemented against this session-scoped mechanism instead of a static dictionary, giving it correct
lifetime and eviction.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-value additions (ranked)

**1. Actual wasteful count and wasted bytes per kind**  
Currently computable but discarded (see `wasteCountByKind` / `wasteBytesByKind` arrays in
`LocalWasteAccumulator` that are accumulated but not surfaced). Surfacing these in
`CollectionDomainResult` would make per-kind findings and the trend comparer correct.

**2. Root path detail in `WastefulCollectionSnapshot`**  
`RootDescription` should carry the path segments (static field name, thread name/id, object type
chain) rather than a binary flag. `ReferenceChainAnalyzer.AnalyzeObject` returns a path; the
path needs to be serialized into the snapshot. Without this, engineers fall back to WinDbg/SOS for
root investigation.

**3. Per-collection resize/trim recommendation**  
Based on type and fill rate:
- `List<T>` or `HashSet<T>`: `TrimExcess()` is zero-code fix, recovers `(capacity - count) × elemSize`.
- `Dictionary<K,V>`: `TrimExcess()` only available since .NET 5 — flag the framework version.
- Persistent `Queue<T>` at <10 % fill: consider `Dequeue`+re-enqueue or switch to a linked approach.

**4. "Over-allocated capacity pattern" classification**  
A `capacity` that is exactly a power of 2 and larger than the load factor implies the collection
triggered at least one doubling. A capacity that is a prime number and larger than expected
suggests the dictionary's prime-bucket sizing was triggered. These patterns distinguish
"grew beyond expected" from "pre-allocated but never filled".

**5. Per-element-type waste aggregation**  
Group `WastedMemory` by `ElementType`. A report showing "System.String instances: 120 MB of
wasted list slots" is more actionable than an address list.

**6. "Wasteful collection owner" via immediate referrer**  
Use the reverse reference index (partial, scoped to the top-N collection addresses) to find the
immediate referrer. Surfacing "this Dictionary<string,object> is held by
`RequestCache._perUserData`" is far more actionable than an address.

**7. `ImmutableArray<T>` / `ImmutableList<T>` support**  
Add `System.Collections.Immutable` to `BclCollectionNamespacePrefixes` and add a dedicated probe
path for `ImmutableArray<T>` (single `_array : T[]` field, no count field — count = array
length, so waste = 0 unless it's a builder pattern).

**8. Empty-allocated collections summary**  
Count and total bytes for collections where `count == 0` but `capacity > 0`. These represent
allocations that were reserved but never used — a different signal from "partially used".

**9. Concurrent collection node/segment waste**  
`ConcurrentDictionary<K,V>` pre-allocates per-lock tables. `ConcurrentQueue<T>` uses segment
chains. Neither is analyzed. A "total segments allocated vs. total elements" metric is feasible
and surfaces contention-driven over-allocation.

**10. `Dictionary._freeCount` — deleted but unreclamed waste**  
Expose `freeEntries` as a separate metric ("entries deleted but not compacted") alongside the
standard waste calculation. A dictionary with 100 K entries, 90 K deletions, and no `TrimExcess`
shows zero waste under the current model; it actually wastes `90 K × EntrySize`.

---

## Audit Area 5 — Performance, Memory & Scalability

### `heapLock is object` serializes all parallel ClrMD access

As noted in Area 3, every `Analyze*` call in `RunParallelCollectionAnalysis` is unconditionally
wrapped in `lock (heapLock)`. On a 25 GB dump with 5 M collections:

- `Parallel.ForEach` dispatches N threads.
- Each thread immediately contends for `heapLock` before any ClrMD call.
- Effective concurrency = 1 for all heap reads.
- Parallelism benefit is limited to the `ResolveCollectionKindConcurrent`/`methodTableKinds`
  ConcurrentDictionary operations, which are a negligible fraction of per-entry cost.

For large dumps this means linear scan time regardless of `MaxDegreeOfParallelism`. On a 24-core
host, `MaxDegreeOfParallelism = 24` provides zero benefit over `= 1`.

### `s_fieldLayoutCache` — static, unbounded growth across dump sessions

On a service analyzing 100 dumps sequentially, each dump contributing 50–200 distinct collection
MethodTable addresses, the static cache accumulates 5 000–20 000 entries permanently. These entries
reference `ClrInstanceField` objects from `ClrRuntime` instances that have since been disposed,
potentially extending the lifetime of `ClrRuntime` references or causing use-after-dispose access
on the stale `ClrInstanceField` references.

### `AddToTopWasteful` is O(topCapacity) linear scan

For `TopWastefulCollectionsToShow = 100` the per-insertion scan is O(100). With 1 M wasteful
collections found, this is 100 M comparisons. A min-heap (e.g., `SortedSet` or a fixed-size
priority queue) would give O(log 100) per insertion — negligible improvement at topCapacity=100
but relevant if `TopWastefulCollectionsToShow` grows.

### `MergePartial` re-applies `AddToTopWasteful` on every worker's list

During the parallel index scan merge, each `WastefulCollection` from a worker's list is passed
through `AddToTopWasteful` individually, which does an O(topCapacity) scan per item. With 16
workers × topCapacity items per worker, merge is O(16 × 100 × 100) = 160 K comparisons — fine
today, but a sorted merge would be O(N log N).

### `LocalWasteAccumulator.WasteCountByKind` and `WasteBytesByKind` — accumulated but discarded

These per-kind arrays are correctly computed per-thread and merged at the end of
`RunParallelCollectionAnalysis`, but the merged values (`wasteCountByKind`, `wasteBytesByKind`)
are never transferred to `CollectionStatistics`. The work is wasted.

### Progress reporting uses absolute counts, not percentage

`progress?.Report(new(s, "scanning collections"))` reports raw object counts. On a 25 GB dump
with 200 M objects, the consumer has no way to know how far along the scan is without knowing the
total object count. Accepting a `totalObjects` hint from the index metadata would allow percentage
reporting.

### Cancellation granularity in `OnHeapEntry` path

The participant path (`OnHeapEntry`) has no per-entry cancellation check — the comment says "the
dispatcher already throws on cancellation per entry". This is correct if the dispatcher checks per
entry. If the dispatcher batches, a long-running `OnHeapEntry` call for a large collection type
could delay cancellation response.

---

## Audit Area 6 — Correctness & Confidence

### Bug 1 — `WasteCountsByKind` contains total inventory counts, not wasteful counts

**Severity: High**

Both code paths populate `WasteCountsByKind` with total-per-kind counts (e.g., all dictionaries
scanned) rather than counts of wasteful-per-kind. This causes:
- `CollectionSectionBuilder` to display a misleading "Wasteful collections by kind" table.
- `CollectionFindingGenerator` to generate an incorrect per-kind breakdown string.
- `CollectionTrendComparer` to track total-inventory-count-by-kind as `"collection.waste.kind.count"`.

The per-kind wasteful counts and bytes *are* computed correctly in `LocalWasteAccumulator` but are
discarded before being placed in `CollectionStatistics`.

### Bug 2 — `TrySetComputedElementSize` corrupts the field layout cache on first write

**Severity: Medium**

```csharp
if (!s_fieldLayoutCache.TryGetValue(methodTable, out var existing))
{
    // existing is default(FieldLayout) here — all null/zero
    var newLayout = new FieldLayout(existing.SizeField, existing.CountField, ...);
    if (s_fieldLayoutCache.TryAdd(methodTable, newLayout))
        return;
    continue;
}
```

When the entry does not exist yet, `existing` is the zero-value struct. `newLayout` is built from
all-null fields with only `compType` and `computedSize` set. After `TryAdd` succeeds, the cache
entry has `SizeField = null`, `ItemsField = null`, etc. The next time `GetOrBuildFieldLayout`
reads this entry via `GetOrAdd`, it finds an existing entry (from the `TryAdd`) and returns the
corrupted layout without rebuilding — causing every subsequent `Analyze*` call for that MT to
find no fields and return `null`.

This race is reachable when a new collection type's first object is analyzed by both
`GetOrBuildFieldLayout` (which inserts a full layout) and `TrySetComputedElementSize` (which races
to insert a partial one). If `TrySetComputedElementSize` wins the `TryAdd` race, the full layout
is never inserted.

### Bug 3 — `IncludeQueueAnalysis = false` has no effect

**Severity: Low**

`CollectionAnalysisOptions.IncludeQueueAnalysis` is declared, documented, and present in the
`Preset(...)` factory, but never checked in `OnHeapEntry`, `ProcessEntry`, or any other dispatch
path. Queues are always analyzed. Callers who set `IncludeQueueAnalysis = false` expecting to skip
queue probing will be surprised.

### Bug 4 — Dictionary `_count` includes free-list entries; fill rate is wrong after deletions

**Severity: Medium** (silent false negative)

`Dictionary<K,V>._count` counts both live entries and entries in the free list (deleted entries
awaiting reuse). A dictionary with 1 000 inserts and 900 deletes (no `TrimExcess`) will have
`_count = 1 000` and `_entries.Length ≥ 1 000`. The analyzer computes fill rate as
`1 000 / 1 000 = 100 %` and reports **zero waste**, when the actual live occupancy is 100 entries
(10 %). The fix: read `_freeCount` and subtract from `_count` before computing fill rate.

### Correctness strengths

- `heapLock` serialization (despite being always-on) prevents ClrMD race conditions in the parallel path.
- `obj.IsValid` and `obj.Type != null` guards are consistently applied.
- `Math.Max(0, ...)` guards prevent negative counts from overflowing `ulong` subtraction.
- `WastefulCollectionSnapshot` is immutable (`record`) — no mutation after construction.
- `BuildStatsFromParticipantState` and `RunParallelCollectionAnalysis` produce equivalent output (verified by `CollectionAnalyzerDiscrepancyTests`).

### Confidence assessment per collection type

| Type | Confidence | Risk |
|---|---|---|
| `List<T>` | High | `_size` / `_items` field names stable across all .NET versions |
| `Dictionary<K,V>` | Medium | `_count` overestimates live entries after deletions (see Bug 4) |
| `HashSet<T>` | Medium | Same `_count` issue; `_entries` array semantics identical to Dictionary |
| `Queue<T>` | Medium | `_size` is correct; head/tail names vary across .NET versions (`_head`/`_tail` vs `_head`/`_size`) |
| `ArrayList` | High | Simple `_size` / `_items` layout |
| `Stack<T>` | High | `_size` / `_array` layout |
| `SortedList<K,V>` | High | `_size` / `keys` + `values` arrays — only one array is probed |
| `SortedSet<T>` | Low | Tree-backed, not array-backed; capacity concept doesn't apply; fill-rate reported is meaningless |

`SortedSet<T>` is a red-black tree. It has no backing array and no "capacity vs count" concept.
Reporting waste on it is semantically incorrect — the `AnalyzeArrayBackedCollection` path falls
back to "any array field" which picks the comparers or other reference fields, not a pre-allocated
backing array.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!dumpheap -type System.Collections.Generic.Dictionary` lists instances with address and size but
provides no fill-rate, no waste estimate, no top-N ranking, no generation breakdown. The engineer
manually reads `_count` and `_entries.Length` with `!do` per instance — feasible for 2 collections,
impractical for 10 000.

**DumpDetective advantage**: automated fill-rate ranking across the entire heap is unique and
orders of magnitude faster for investigation.

**SOS advantage**: `_freeCount` is visible in raw field dumps — DumpDetective currently ignores it.

### PerfView

PerfView's heap snapshot analysis shows type-grouped object sizes and reference graphs but has no
fill-rate concept for collections. It has no "wasteful collections" list.

**DumpDetective advantage**: unique diagnostic.

### Visual Studio Memory Usage / dotTrace

Neither tool surfaces collection fill rates. VS Memory Profiler groups by type and shows retained
size. It does not distinguish a full `List<T>` from an empty-but-allocated one.

**DumpDetective advantage**: fill-rate analysis is entirely absent from VS tooling.

### JetBrains dotMemory

dotMemory's "Incoming References" and "Group by Type" views can identify large collections. The
"Namespace" grouping helps attribute collections to owners. It does not compute fill rates.

**dotMemory advantage over DumpDetective**:
- Full reference graph with owner attribution — DumpDetective's `RootDescription` is binary.
- Time-based heap comparison (two snapshots) with per-object-type delta — DumpDetective's
  `CollectionTrendComparer` is metric-only, not instance-level.

### Competitive opportunities

1. **Root path detail** — dotMemory's ownership graph is DumpDetective's most significant gap
   in the collection space. Propagating `ReferenceChainAnalyzer` path detail to
   `WastefulCollectionSnapshot.RootDescription` would close most of this gap.
2. **Per-element-type waste** — none of the benchmarked tools aggregate wasted memory by element
   type. This would be a genuinely unique capability.
3. **Temporal delta** — the existing `CollectionTrendComparer` has the scaffolding but only tracks
   aggregate metrics. Instance-level matching across two dumps ("this same Dictionary grew from
   50 % to 5 % fill — it was TrimExcess'd or replaced") is not implemented.
4. **"Fix it" recommendations** — no tool auto-generates `TrimExcess()` call sites or initial
   capacity recommendations. Even a simple per-instance note ("call TrimExcess()") would
   differentiate DumpDetective in the investigation workflow.

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**  
**Production readiness: Conditional** — the analyzer produces useful output and is fast enough for
most dumps, but contains a semantic bug that makes `WasteCountsByKind` incorrect, a correctness
bug that silently misreports Dictionary waste after deletions, a correctness bug in
`TrySetComputedElementSize` that can corrupt the field-layout cache, and a parallelism bug that
eliminates all multi-core benefit.

**Major strengths**:
- Unique diagnostic not available in any benchmarked tool.
- Dual scan paths (disk-index participant + parallel segment fallback) with verified equivalence.
- Queue circular-buffer diagnostics (head/tail/free-segment metrics).
- Generation breakdown supports "is this long-lived waste?" triage.
- `SizeEstimateConfidence` and `DetectionMethod` surface internal analysis quality to the engineer.

**Major weaknesses**:
- `WasteCountsByKind` semantically wrong — reports total inventory, not wasteful count per kind.
- `Dictionary._freeCount` not accounted for — fill-rate silent false negatives after deletions.
- `heapLock is object` always true — zero parallel speedup despite `Parallel.ForEach`.
- Static `s_fieldLayoutCache` unbounded and cross-session — correctness risk in batch analysis hosts.
- `RootDescription` binary, not actionable.
- `SortedSet<T>` waste reporting is semantically incorrect (tree-backed, no array capacity).

---

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P0-1 | **Fix `WasteCountsByKind`** — populate from per-kind wasteful accumulators (`wasteCountByKind`/`wasteBytesByKind` in `LocalWasteAccumulator`), not total inventory counts. Surface both `WasteCountsByKind` and `WasteBytesByKind` in `CollectionDomainResult`. Update finding generator, section builder, trend comparer. | Correctness of all per-kind reporting | Low | High | Improvement |
| P0-2 | **Fix `TrySetComputedElementSize` first-write path** — when `TryGetValue` returns false, call `GetOrBuildFieldLayout` to get the full layout, then CAS-update `ComputedElementSize` only. Do not construct a layout from a default-value struct. | Prevents cache corruption and `null`-return from all `Analyze*` methods for affected types | Low | High | Improvement |
| P0-3 | **Fix `heapLock is object` — consult `_options.SerializeHeapAccess`** — replace `if (heapLock is object)` with `if (_options.SerializeHeapAccess)` and make `heapLock` nullable (`object? heapLock = _options.SerializeHeapAccess ? new object() : null`). This restores multi-core benefit for dumps where ClrMD access is provably thread-safe. | Performance on multi-core hosts; eliminates dead `else` branch | Low | High | Improvement |

#### P1 — High

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P1-1 | **Account for `Dictionary._freeCount`** — read `_freeCount` field (if present, .NET ≥ 5 layout), subtract from `_count` before fill-rate computation. Same for `HashSet<T>` (`_freeCount` field). Report `FreeEntryCount` in `WastefulCollectionSnapshot`. | Correct fill-rate for dictionaries with deletions | Medium | High | Improvement |
| P1-2 | **Scoped, session-lifetime field-layout cache** — replace static `s_fieldLayoutCache` with an instance-level or `AnalysisContext`-scoped cache to prevent stale/cross-session entries and avoid holding `ClrInstanceField` references after `ClrRuntime` disposal. | Correctness in batch/service hosts | Medium | High | Improvement |
| P1-3 | **Remove `SortedSet<T>` from array-backed waste analysis** — `SortedSet<T>` is a red-black tree with no capacity concept. Either exclude it or implement a dedicated node-count probe. Currently reports misleading fill rates. | Removes false findings | Low | High | Improvement |
| P1-4 | **Propagate root path detail** — extend `WastefulCollectionSnapshot.RootDescription` to carry a structured path (field name, owner type, root kind) from `ReferenceChainAnalyzer`. Replace binary "Retained/Not found" with actual path segments. | Eliminates the largest diagnostic gap vs dotMemory | High | Medium | Improvement |
| P1-5 | **Honor `IncludeQueueAnalysis` option** — add `if (!_options.IncludeQueueAnalysis && kind == CollectionKind.Queue) return;` guard in both `OnHeapEntry` and `ProcessEntry`. | Correctness of documented API | Low | High | Improvement |

#### P2 — Medium

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P2-1 | **Surface per-kind wasted bytes in `CollectionDomainResult`** — add `WasteBytesByKind : IReadOnlyDictionary<CollectionKind, ulong>` to enable per-kind wasted-memory trending and per-kind findings. | Richer trend comparisons | Low | High | Improvement |
| P2-2 | **Add `System.Collections.Immutable` namespace** to `BclCollectionNamespacePrefixes`; add dedicated `ImmutableArray<T>` probe (single `_array` field, count = `_array.Length`, waste = 0 unless builder-pattern detection). | New collection type coverage | Medium | Medium | Improvement |
| P2-3 | **Per-collection resize recommendation** — add a `Recommendation` field to `WastefulCollectionSnapshot` (e.g., "Call TrimExcess()", "Construct with initial capacity X", "Unreachable — no fix needed"). Populate in the finding generator. | Actionability for engineers | Low | Medium | Improvement |
| P2-4 | **Seal `CollectionAnalyzer`** and remove `public void Dispose() { }` override (redundant with default interface implementation). Override `Tags` = `["collections"]` and `Order` = `240`. | Code hygiene, prevents accidental subclass breaking `CreateWorkerInstance` | Low | High | Improvement |
| P2-5 | **Consolidate `AnalyzeQueue` field discovery** — remove uncached `FindFirstArrayField` / `FindFieldByNameContains*` calls; route all field resolution through `GetOrBuildFieldLayout` to ensure queue field lookups are cached on first call. | Eliminates redundant `ClrType.Fields` enumeration per queue object | Low | High | Improvement |

#### P3 — Low

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P3-1 | **Replace `AddToTopWasteful` linear scan with a fixed-size min-heap** for `topCapacity > 50` use cases. | Negligible at topCapacity=100; relevant if top-N grows to 1 000+ | Medium | Medium | Improvement |
| P3-2 | **Progress reporting: accept total-object-count hint** from the index header and report percentage instead of raw count. | Better UX on large dumps | Low | Medium | Improvement |
| P3-3 | **Separate Queue columns in section builder** — render head/tail/free-segment columns only in a Queue-specific sub-table; remove these columns from the shared wasteful-collections table. | Report readability | Low | High | Improvement |
| P3-4 | **"Owner type" via partial reverse index** — for top-5 collections, query the partial reverse reference index to surface the immediate referrer type name (owner hint without full BFS). | Quick ownership signal at low cost | Medium | Low | Evolution |
| P3-5 | **Per-element-type wasted memory aggregation** — group top `WastefulCollection` items by `ElementType` and surface total wasted bytes per element type. | Unique diagnostic; actionable for allocation refactoring | Medium | Medium | Improvement |

---

---

## Redesign from Scratch

> What would a ground-up rewrite look like, given DumpDetective's non-negotiable constraints:
> no heap materialization, streaming over the disk-backed index, bounded memory, no LINQ in hot
> paths, `ulong` address as identity, `readonly struct` on hot-path data.

### Thesis

The current analyzer's complexity comes almost entirely from two sources: (1) a single
`FieldLayout` struct trying to serve eight structurally different collection types through generic
fallback chains, and (2) a single scan loop trying to accommodate both the index-participant path
and the segment-fallback path through shared mutable state. A redesign eliminates both sources of
complexity by introducing **per-type probers** and a **single authoritative scan path**.

---

### Core principle: one scan path, per-type probers

The redesigned analyzer has one scan path — the shared heap-index participant path
(`IParallelHeapIndexScanParticipant`). The segment-fallback path (`RunParallelCollectionAnalysis`
over `heap.Segments`) is retained as an escape hatch for dumps with no index, but it calls the
same probers rather than reimplementing logic. There is no parallel-path divergence.

The scan dispatcher calls `OnHeapEntry` per index record. The entry is classified by MethodTable
to a `CollectionKind`. A **prober** for that kind receives the `ClrObject` and returns a
`WasteResult` (a small readonly struct). Probers are stateless, static, and keyed by kind. This
eliminates the generic `AnalyzeArrayBackedCollection` / `AnalyzeDictionary` / `AnalyzeList` /
`AnalyzeHashSet` / `AnalyzeQueue` family and replaces it with a clear, maintainable per-type
contract.

```csharp
internal interface ICollectionProber
{
    CollectionKind Kind { get; }

    // Returns false if the object is not wasteful or cannot be probed.
    bool TryProbe(ClrHeap heap, ClrObject obj, CollectionFieldLayout layout,
                  CollectionAnalysisOptions options, out WasteResult result);
}

internal readonly struct WasteResult
{
    public readonly int Count;
    public readonly int Capacity;
    public readonly int FreeEntryCount;   // for Dict/HashSet: _freeCount (deleted entries)
    public readonly ulong ElementSize;
    public readonly string ElementType;
    public readonly string Confidence;    // "High" | "Medium" | "Low"
    public readonly int? Head;
    public readonly int? Tail;
}
```

Eight probers, one per kind. Each prober knows exactly which fields to read and what they mean
semantically:

| Prober | Key fields | Semantic note |
|---|---|---|
| `ListProber` | `_size`, `_items` | `_size` = live count; no free-list |
| `DictionaryProber` | `_count`, `_freeCount`, `_entries` | live = `_count - _freeCount` |
| `HashSetProber` | `_count`, `_freeCount`, `_entries` | same as Dictionary |
| `QueueProber` | `_size`, `_array`, `_head` | circular buffer; free-segment math in helper |
| `ArrayListProber` | `_size`, `_items` | identical to List layout |
| `StackProber` | `_size`, `_array` | simple array-backed |
| `SortedListProber` | `_size`, `_keys` | two parallel arrays; capacity = `_keys.Length` |
| `SortedSetProber` | **excluded** | red-black tree; no capacity concept — returns `false` always |

`SortedSet<T>` is excluded at the prober level with an explicit comment. It is still classified
and counted in inventory, but `TryProbe` always returns `false`, so it never appears in the
wasteful list.

---

### Session-scoped field layout cache (`CollectionFieldLayout`)

The static `s_fieldLayoutCache` is replaced by a session-scoped
`CollectionFieldLayoutCache` — a plain `Dictionary<ulong, CollectionFieldLayout>` owned by
`AnalysisContext` (or constructed fresh per `CollectionAnalyzer` instance). No static state.

```csharp
internal sealed class CollectionFieldLayoutCache
{
    private readonly Dictionary<ulong, CollectionFieldLayout> _cache = new(capacity: 128);

    public CollectionFieldLayout GetOrBuild(ClrType type)
    {
        if (_cache.TryGetValue(type.MethodTable, out var existing))
            return existing;
        var layout = CollectionFieldLayout.Build(type);
        _cache[type.MethodTable] = layout;
        return layout;
    }
}
```

`CollectionFieldLayout.Build` runs exactly once per MethodTable per session. It returns a
`readonly struct` (or small class) with all fields discovered in a single enumeration of
`ClrType.Fields`. No CAS loops, no concurrent dictionary, no unbounded growth, no cross-session
pollution, no stale `ClrInstanceField` references after `ClrRuntime` disposal.

```csharp
internal readonly struct CollectionFieldLayout
{
    public readonly ClrInstanceField? CountField;
    public readonly ClrInstanceField? FreeCountField;    // _freeCount for Dict/HashSet
    public readonly ClrInstanceField? SizeField;
    public readonly ClrInstanceField? BackingArrayField; // _items / _entries / _array
    public readonly ClrInstanceField? HeadField;
    public readonly ClrInstanceField? TailField;
    public readonly ClrType? ComponentType;
    public readonly ulong ElementSize;

    public static CollectionFieldLayout Build(ClrType type) { ... }
}
```

The field discovery logic in `Build` runs a single pass and applies priority-ordered name
matching per field role, with no fallback re-enumeration. The helper methods
`FindFirstArrayField`, `FindFirstInt32Field`, `FindFieldByNameContains`, `FindFieldByNameContainsAny`
are absorbed into `Build` and eliminated from the public API.

---

### Correct parallelism: lock-free by default, serialized by option

```csharp
// At construction time — not inside the hot loop:
object? _heapLock = options.SerializeHeapAccess ? new object() : null;

// In the hot loop:
WasteResult result;
if (_heapLock is not null)
    lock (_heapLock) { probed = prober.TryProbe(heap, obj, layout, options, out result); }
else
    probed = prober.TryProbe(heap, obj, layout, options, out result);
```

`SerializeHeapAccess = false` (the default) means no lock overhead. The architectural question of
whether ClrMD heap reads are thread-safe under this access pattern is documented in the option and
in the architecture notes, not silently answered by always-on serialization. On a 24-core host with
`SerializeHeapAccess = false`, parallel throughput is fully realized.

---

### Per-kind waste accumulators — first-class, not discarded

Instead of the current `LocalWasteAccumulator` that computes per-kind arrays and then silently
discards them, the redesigned accumulator explicitly surfaces per-kind waste as part of its
contract:

```csharp
internal sealed class PerKindWasteAccumulator
{
    // Per-kind: count of wasteful instances and sum of wasted bytes
    private readonly long[] _wastefulCount;   // indexed by (int)CollectionKind
    private readonly ulong[] _wastedBytes;    // indexed by (int)CollectionKind
    private readonly FixedMinHeap<WastefulCandidate> _topN;

    public void Record(CollectionKind kind, ulong wastedBytes, WastefulCandidate candidate) { ... }
    public void MergeFrom(PerKindWasteAccumulator other) { ... }
    public void FlushInto(CollectionStatistics stats) { ... }  // one place; nothing discarded
}
```

`FlushInto` populates `WastefulCollectionCount`, `TotalWastedMemory`, `WasteCountsByKind`
(wasteful count per kind — correct semantics), and `WasteBytesByKind` (new field — total wasted
bytes per kind). The section builder and finding generator receive both, enabling per-kind
findings like "Dictionaries account for 80 % of wasted bytes (1.2 GB)".

`FixedMinHeap<T>` is a small O(log topCapacity) fixed-capacity min-heap, replacing the current
O(topCapacity) linear scan in `AddToTopWasteful`.

---

### `Dictionary._freeCount` — correct fill rate by default

`DictionaryProber` reads `_freeCount` (present since .NET Core 1.x; field name consistent):

```csharp
bool TryProbe(ClrHeap heap, ClrObject obj, CollectionFieldLayout layout, ..., out WasteResult result)
{
    int count    = layout.CountField!.Read<int>(obj, interior: false);
    int freeCount = layout.FreeCountField?.Read<int>(obj, interior: false) ?? 0;
    int liveCount = Math.Max(0, count - freeCount);

    var entries = layout.BackingArrayField!.ReadObject(obj, interior: false);
    if (!entries.IsValid || !entries.IsArray) { result = default; return false; }

    int capacity = entries.AsArray().Length;
    if (capacity <= 0 || liveCount >= capacity) { result = default; return false; }

    double fillRate = (double)liveCount / capacity;
    ulong elementSize = ComputeElementSize(layout, entries);
    ulong wastedSlots = (ulong)(capacity - liveCount);
    ulong wastedBytes = wastedSlots * elementSize;

    result = new WasteResult(liveCount, capacity, freeCount, elementSize, ...);
    return wastedBytes > options.WasteThresholdBytes;
}
```

`FreeEntryCount` is surfaced in `WastefulCollectionSnapshot` as a separate diagnostic field, not
folded silently into `Count`. An engineer can then see: "this Dictionary has 10 000 allocated
entries, 1 000 live, 9 000 deleted but not compacted — call `TrimExcess()`."

---

### Structured root path

`RootDescription` is replaced by `RootPath`, a small immutable struct carrying the path found by
`ReferenceChainAnalyzer`:

```csharp
internal readonly record struct CollectionRootPath(
    string RootKind,          // "StaticField", "ThreadStack", "GCHandle", "None"
    string? OwnerType,        // immediate referrer type name
    string? FieldOrFrameName, // static field name or stack frame method name
    string? FullPath          // optional multi-hop summary, e.g. "A.field -> B.list"
);
```

`PopulateRootDescriptions` is rewritten to populate `CollectionRootPath` from the structured
output of `ReferenceChainAnalyzer`, not from a boolean check. Probers and the snapshot model
never see `null` descriptions — they see a `CollectionRootPath` with `RootKind = "None"` when
the path search exhausted its budget.

---

### `IncludeQueueAnalysis` respected; options as first-class scan gate

At `BeforeHeapIndexScan`, the set of active probers is assembled from options:

```csharp
_activeProbers = BuildProberSet(_options);  // keyed by CollectionKind

private static IReadOnlyDictionary<CollectionKind, ICollectionProber> BuildProberSet(
    CollectionAnalysisOptions options)
{
    var map = new Dictionary<CollectionKind, ICollectionProber>(8);
    map[CollectionKind.List]        = ListProber.Instance;
    map[CollectionKind.Dictionary]  = DictionaryProber.Instance;
    map[CollectionKind.HashSet]     = HashSetProber.Instance;
    map[CollectionKind.ArrayList]   = ArrayListProber.Instance;
    map[CollectionKind.Stack]       = StackProber.Instance;
    map[CollectionKind.SortedList]  = SortedListProber.Instance;
    // SortedSet deliberately excluded — tree-backed, no capacity concept
    if (options.IncludeQueueAnalysis)
        map[CollectionKind.Queue]   = QueueProber.Instance;
    return map;
}
```

Options that affect which work is done are resolved once at setup time, not tested per-entry in
the hot path. Adding a new collection type means adding one prober and one line here.

---

### Classification table: current vs. redesign

| Concern | Current | Redesign |
|---|---|---|
| Scan path | Two divergent paths (participant + parallel-segment fallback) with shared mutable state | One participant path; fallback calls the same probers |
| Field layout | Static unbounded `ConcurrentDictionary`; cross-session; CAS bugs | Session-scoped `CollectionFieldLayoutCache`; one `Dictionary<ulong,…>`; no CAS |
| Per-type logic | Generic `AnalyzeArrayBackedCollection` + 4 specialized methods + mixed uncached field lookups | 7 stateless `ICollectionProber` implementations; `SortedSet` explicitly excluded |
| Parallelism | Always serialized (`heapLock is object`); `SerializeHeapAccess` ignored | Lock held only when `SerializeHeapAccess = true`; lock-free by default |
| Per-kind waste data | Computed, then discarded in both paths | `PerKindWasteAccumulator`; flushed into model in one place |
| Dictionary fill rate | `_count / capacity`; ignores `_freeCount`; silent false negatives after deletions | `(_count - _freeCount) / capacity`; `FreeEntryCount` surfaced as diagnostic |
| `SortedSet<T>` | Probed as array-backed; reports meaningless waste | Classified in inventory; prober always returns false; never appears as wasteful |
| Root description | Binary string: "Retained" / "Not found" | `CollectionRootPath` struct with kind, owner type, field/frame name |
| Top-N maintenance | O(topCapacity) linear scan per insertion | `FixedMinHeap<T>` O(log topCapacity) per insertion |
| Options as scan gate | `IncludeQueueAnalysis` never checked | Prober set assembled from options at setup; per-entry dispatch is a map lookup |
| `Tags` / `Order` | Interface defaults (empty, 0) | `Tags = ["collections"]`, `Order = 240` |
| Class modifiers | `public class` (subclassable) | `internal sealed class` |

---

### MethodTable → kind classification under lock-free parallelism

When `SerializeHeapAccess = false` the prober dispatch is lock-free, but the MT → `CollectionKind`
lookup still needs to be thread-safe. The current `ConcurrentDictionary` approach is correct for
concurrency but wrong in scope (static, cross-session). In the redesign the classification cache
is a per-instance `ConcurrentDictionary<ulong, CollectionKind>` created in `BeforeHeapIndexScan`
and discarded after `OnHeapIndexScanCompleted`. Because `CollectionKind` resolution is
deterministic for a given `MethodTable` (same type name always maps to the same kind), `GetOrAdd`
is safe under contention with no observable behavior difference between races — the factory simply
runs twice for the same MT on first access, both returning the same value.

For the segment-fallback path, the same per-instance `ConcurrentDictionary` is used, constructed
fresh at the start of each fallback run. No static state, no cross-session collision.

---

### `ImmutableArray<T>` and the Immutable namespace (P2-2)

`System.Collections.Immutable` is added to `BclCollectionNamespacePrefixes` and `BuildProberSet`
gains an `ImmutableArrayProber`:

```csharp
// In BuildProberSet:
if (options.IncludeImmutableCollections)
    map[CollectionKind.ImmutableArray] = ImmutableArrayProber.Instance;
```

`ImmutableArray<T>` has a single backing field `_array : T[]` and no count field — the live count
equals the array length, so capacity = count and waste is structurally zero. The prober is
therefore not a waste detector; instead it counts instances and total bytes as an inventory signal.
The interesting case is `ImmutableArray<T>.Builder`, which has `_elements : T[]` and `_count : int`
matching the standard array-backed pattern — that is reported as wasteful when `_count < _elements.Length`.

`CollectionKind` gains two values: `ImmutableArray` and `ImmutableArrayBuilder`.
`IncludeImmutableCollections` is added to `CollectionAnalysisOptions` (default `false` to avoid
noise until the probe paths are validated across .NET version field layouts).

---

### Per-collection `Recommendation` (P2-3)

`Recommendation` is computed by a static `CollectionRecommendationEngine` after probing, not
inside the prober itself. This keeps probers pure (measure only) and separates advice generation
from measurement:

```csharp
internal static class CollectionRecommendationEngine
{
    public static string Compute(CollectionKind kind, int liveCount, int capacity,
                                 int freeEntryCount, double fillRate, string dotNetVersion)
    {
        if (fillRate == 0.0)
            return "Empty but allocated — release or null out to reclaim immediately.";

        return kind switch
        {
            CollectionKind.List or CollectionKind.ArrayList or CollectionKind.Stack =>
                $"Call TrimExcess() to reclaim ~{FormatSlots(capacity - liveCount)} slots.",

            CollectionKind.HashSet =>
                freeEntryCount > 0
                    ? $"Call TrimExcess() — {freeEntryCount:N0} deleted entries not yet compacted."
                    : $"Call TrimExcess() to reclaim ~{FormatSlots(capacity - liveCount)} slots.",

            CollectionKind.Dictionary =>
                freeEntryCount > 0
                    ? $"Call TrimExcess() — {freeEntryCount:N0} deleted entries inflate apparent count."
                    : IsNet5OrLater(dotNetVersion)
                        ? "Call TrimExcess() to reclaim unused entries."
                        : "TrimExcess() not available before .NET 5 — recreate with correct initial capacity.",

            CollectionKind.Queue =>
                fillRate < 10.0
                    ? "Queue is nearly empty but retains a large circular buffer. Drain and shrink."
                    : "Queue is under-utilised — consider constructing with a smaller initial capacity.",

            CollectionKind.SortedList =>
                "Call TrimExcess() to release unused capacity from both key and value arrays.",

            _ => string.Empty
        };
    }
}
```

`Recommendation` is a `string` field on `WastefulCollectionSnapshot`. It is populated in the
post-scan assembly step, not during the hot scan loop.

---

### Progress reporting as percentage (P3-2)

The heap index header records total object count at build time. `BeforeHeapIndexScan` accepts
this hint and passes it to the scan counter:

```csharp
_scanCounter = new ObjectScanCounter(
    "scanning collections",
    _progress,
    totalHint: context.Cache?.TryGetTotalObjectCount(out long n) == true ? n : 0L);
```

`ObjectScanCounter.Report` emits percentage when `totalHint > 0`:

```csharp
string message = totalHint > 0
    ? $"{(scanned * 100.0 / totalHint):F1} % ({scanned:N0} / {totalHint:N0})"
    : $"{scanned:N0} objects scanned";
```

When the index is unavailable (segment-fallback path), `totalHint = 0` and reporting falls back to
the existing absolute-count behavior. No change needed in that path.

---

### Queue-only columns in the section builder (P3-3)

`CollectionSectionBuilder` splits the single 15-column "Wasteful collections" table into two:

- **"Wasteful collections"** (10 columns): Type, Kind, Count, Capacity, Fill Rate, Wasted,
  Element Type, Element Size, Confidence, Root. Shown for all kinds.
- **"Queue circular-buffer diagnostics"** (5 columns): Type, Address, Head, Tail,
  Largest Free Gap, Free Segments. Shown only when `TopWastefulCollections` contains at least one
  `Queue` entry.

The Queue table is built from the same `TopWastefulCollections` list filtered to `Kind == Queue`,
so no additional data is needed. The main table loses the four always-empty columns for non-Queue
types, reducing visual noise.

---

### Owner type via partial reverse index (P3-4) and per-element-type waste aggregation (P3-5)

Both are post-scan enrichment steps applied to the top-N list only, keeping the hot scan path
unchanged.

**Owner type (P3-4)**: after `PopulateRootDescriptions` runs, a second pass over the top-5
entries queries the partial reverse reference index (if available in `cache`) for each collection
address. This is a single `cache.TryGetReverseReferences(address, maxResults: 1)` call per item.
If an immediate referrer is found its type name is written into `CollectionRootPath.OwnerType`,
giving the section builder a "held by `RequestCache`" hint without a full BFS. If the reverse
index is unavailable the field stays `null`.

**Per-element-type aggregation (P3-5)**: after the scan, a `Dictionary<string, (int count, ulong
wastedBytes)>` is built from the full `wasteful` list (not just top-N) by iterating once and
grouping by `ElementType`. The top-10 element types by wasted bytes are written into
`CollectionDomainResult.WastedByElementType` — a new `IReadOnlyList<ElementTypeWasteStat>` field.
The section builder renders this as a compact "Waste by element type" table. This is an O(N) pass
over the wasteful list (not the heap), so it is bounded and fast.

```csharp
public sealed record ElementTypeWasteStat(
    string ElementType,
    int WastefulCollectionCount,
    ulong TotalWastedBytes);
```

---

### Empty-allocated collections summary (Area 4, item 8)

A collection with `count == 0` and `capacity > 0` represents allocation with zero use — a distinct
signal from "partially used". In the redesigned accumulator, probers that return a `WasteResult`
with `Count == 0` increment a separate `EmptyAllocatedCount` and `EmptyAllocatedBytes` counter
(not gated by `WasteThresholdBytes`). These two scalars are surfaced in `CollectionDomainResult`
and rendered as a single row in the section builder's summary table. An engineer can immediately
see "4 200 collections allocated but never populated — 180 MB wasted" alongside the standard
waste table, without those entries polluting the top-N wasteful list.

---

### What stays the same

- `IParallelHeapIndexScanParticipant` / `IHeapIndexScanParticipant` protocol — the pipeline
  contract is correct and should not change.
- `HeapEntry`-based streaming — no materialization.
- `BeforeHeapIndexScan` / `OnHeapEntry` / `OnHeapIndexScanCompleted` lifecycle.
- `CollectionAnalysisHelpers.ComputeQueueFreeSegments` and `ComputeWastedMemoryFromSlots` — the
  logic is correct; keep them as helpers.
- `WastefulCollectionSnapshot` as the immutable output record — its shape grows (add
  `FreeEntryCount`, `CollectionRootPath`, `Recommendation`) but the record pattern is correct.
- Generation breakdown — `_generationCounts` per-kind approach is sound.
- `CollectionAnalysisOptions` — mostly intact; `SerializeHeapAccess` gains real effect;
  `IncludeQueueAnalysis` gains real effect; `IncludeImmutableCollections` added.
- `CollectionSectionBuilder` and `CollectionTrendComparer` — updated to consume new fields, but
  their structural role is unchanged.

---

### Final Verdict

**1. Is the analyzer production-ready?**  
Conditionally. The output is useful and the scan paths are verified equivalent. However, Bug P0-1
(`WasteCountsByKind` semantic mismatch) means every per-kind report shown to users is wrong. This
should be fixed before the analyzer is considered production-correct.

**2. Highest-impact improvements?**  
P0-3 (parallelism fix), P0-1 (correct per-kind waste counts), P1-1 (Dictionary `_freeCount`), and
P1-4 (root path detail). Together these address the three largest correctness gaps and the primary
performance regression.

**3. Platform evolution opportunities?**  
- The static `s_fieldLayoutCache` should evolve into a session-scoped service injectable via
  `AnalysisContext`, shared across any analyzer that needs field-layout resolution. This would
  benefit `EventLeakAnalyzer` (which also walks delegate/event fields) and reduce duplication.
- `ReferenceChainAnalyzer` path detail should be promoted from an opaque string to a structured
  `RootPath` type shared across all analyzers that surface root descriptions.

**4. Highest engineering return?**  
P0-3 (one-line fix restores multi-core throughput), P0-1 (one-pass fix restores report accuracy),
P1-3 (exclude `SortedSet<T>` — removes false findings with no upside). These three changes have
the highest ratio of diagnostic improvement to implementation cost.
