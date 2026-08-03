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