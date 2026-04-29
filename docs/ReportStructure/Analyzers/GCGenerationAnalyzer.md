# GCGenerationAnalyzer — Coverage & Change Spec

## Status
**Existing** · **✅ Implemented** · Implementation Priority **4** · Effort: Medium

## Report Sections Served
- §2.2 Generation Pressure (Gen0/1/2/LOH distribution)
- §9.1 Allocation Patterns (per-type generation profile)
- §9.2 GC Efficiency (Gen2 accumulation signal)
- §10.1 LOH Summary (TopLohTypes)

---

## Currently Produces
- `GCGenerationDomainResult`: Gen0/1/2/LOH bytes + object counts
- `TopLohTypes` — top LOH types by count/size
- Uses parallel generation scan via reflection on `ClrHeap.GetGeneration`

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Per-type generation distribution (Gen0 % vs Gen2 % per type) | §2.2, §9.1, §9.2 | High |
| POH object distribution | §10.1 | Medium |
| Gen2 % as a "long-lived pressure" signal | §9.2 | Medium |

---

## Required Changes

1. **Add `PerTypeGenerationProfile`** — `IReadOnlyList<TypeGenerationProfile>` on
   `GCGenerationDomainResult` for top N types. Each record:
   ```
   TypeGenerationProfile(string TypeName, int Gen0Count, int Gen1Count, int Gen2Count, int LohCount)
   ```
   This is **computed in the existing parallel scan** — just accumulate per type instead of
   discarding generation data after the global counter update. Use the heap index to avoid
   a second heap walk.
2. **Compute `Gen2Pct`** — Gen2 objects / total objects as a signal for `AllocationPatternAnalyzer`
   to consume. Add as a field to `GCGenerationDomainResult`.
3. **`TopLohTypes`** — currently typed as `IReadOnlyList<TypeSnapshot>?` with default null.
   Make this non-nullable and always populated (empty list if no LOH objects). Consumers
   should not guard against null.

---

## Phase Assignment

### Current Phase Assignment
| Step | Current Phase | Location |
|------|--------------|----------|
| Global Gen0/1/2/LOH byte+count totals | Phase 2 | Parallel re-scan of heap via `RunParallelGenerationScan()` using reflection |
| Per-type generation breakdown | Phase 2 | **Not currently computed** |

### Problem
`GCGenerationAnalyzer` currently does a **full second heap scan** in Phase 2 using
`ClrHeap.GetGeneration()` via reflection. This is the most expensive duplicate work in the system.

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| Per-MT generation counts (Gen0/1/2/LOH) | **Phase 1** | Extend `MutableTypeAggregate` with Gen0Count/Gen1Count/Gen2Count |
| Global generation totals | Phase 2 | Sum from per-MT aggregates — no heap re-scan |
| Per-type generation profile | Phase 2 | Read directly from extended TypeAggregates |

### Phase 1 Extension — Generation Counts in `TypeAggregateIndexEntry`

Extend `MutableTypeAggregate` with three additional counters:

```csharp
private struct MutableTypeAggregate
{
    // existing:
    public long Count;
    public ulong TotalSize;
    public long LohCount;
    public ulong LohSize;
    public ulong SampleAddress;
    public int ModuleId;
    // new:
    public int Gen0Count;    // +4 bytes
    public int Gen1Count;    // +4 bytes
    public int Gen2Count;    // +4 bytes
    public byte Flags;       // +1 byte (IsStringType, IsTaskType, IsDelegateType, …)
}
```

During `DiskBackedObjectIndexWriter` segment scan, call `heap.GetGeneration(obj.Address)`
(or derive from segment generation range) per object and increment the appropriate counter.

`TypeAggregateIndexEntry` grows from 48 → 61 bytes (padded to 64 bytes in binary format).
For 50K types: +768KB in-memory. Negligible.

### Phase 2 Computation
1. Reads `heapIndex.TypeAggregates` → builds `GCGenerationDomainResult` directly from
   the pre-computed per-MT generation counts. **Zero heap re-scan. Zero reflection.**
2. `RunParallelGenerationScan()` is deleted entirely — the Phase 2 bottleneck is eliminated.

> No change to `ObjectIndex.bin` format — generation is stored per-MT in `TypeAggregates`, not per-object.

---

## Related Analyzers
- **`AllocationPatternAnalyzer`** (new) — consumes `PerTypeGenerationProfile` and `Gen2Pct`; must run after this analyzer (Order = 11 vs 10)
- **`FinalizableObjectAnalyzer`** (new) — uses `Gen0/1/2Count` from TypeAggregates for generation breakdown of finalizable objects
- **`ArrayAnalyzer`** (new) — uses generation data for array lifecycle classification
