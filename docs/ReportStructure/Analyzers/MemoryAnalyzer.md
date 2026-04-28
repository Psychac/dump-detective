# MemoryAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Implementation Priority **3** · Effort: Low

## Report Sections Served
- §1 Executive Summary (total managed memory)
- §2.1 Heap Composition (object size histogram)
- §3.1 Detailed Type Table (count, shallow size, avg size, estimated retained size)

---

## Currently Produces
- `MemoryDomainResult`: total bytes, LOH bytes, LOH %, total objects, unique types
- `TopTypesBySize` and `TopTypesByCount` — top 20 `TypeSnapshot` records
- `TypeSnapshot` has: `TypeName`, `Count`, `TotalSize`, `LohSize`

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| `AverageSize` per type | §3.1 | Low — derivable but not surfaced |
| **Retained size** per type | §3.1, §3.2 | High — fundamental gap; shallow only today |
| Object size distribution histogram (size-bucket counts) | §2.1 | Medium |
| Process total memory for % calculation | §1 | Medium |

---

## Required Changes

1. **Add `AverageSize`** to `TypeSnapshot` — trivially `TotalSize / Count`. Zero cost.
2. **Add `EstimatedRetainedBytes`** to `TypeSnapshot` — set to `0` / `null` by `MemoryAnalyzer`;
   populated by `DominatorAnalyzer` in a post-pass. `MemoryAnalyzer` itself must not walk
   references (that would double heap scan time).
3. **Add `SizeBucketHistogram`** to `MemoryDomainResult` — `IReadOnlyList<SizeBucketEntry>` where
   each bucket is `(RangeLabel, ObjectCount, TotalBytes)`. Build during the existing
   `typeStats` iteration — no extra heap scan required.
4. **Consider** exposing `UniqueTypes` more prominently; already present, just needs surfacing
   in the type table section of the report.

---

## Phase Assignment

### Current Phase Assignment
| Step | Current Phase | Location |
|------|--------------|----------|
| TypeAggregates build (count, size per MT) | **Phase 1** ✅ | `TypeIndexBuilder.Add()` during segment scan |
| Sort by size / count | Phase 2 | `MemoryAnalyzer.BuildDomainResult()` |
| Build MemoryDomainResult | Phase 2 | Reads from `cache.GetOrBuildTypeStatistics()` |
| SizeBucketHistogram | ❌ Missing | Not computed anywhere |

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| SizeBucketHistogram | **Phase 1** | 8 global counters accumulated in `TypeIndexBuilder.Add()` |
| AverageSize derivation | Phase 2 | `TotalSize / Count` — trivially from TypeAggregates |

### Phase 1 Extension — Global Size Buckets

Add 8 `long` counters to `TypeIndexBuilder` accumulated during `Add()`:

```
Bucket 0:  Size ≤ 24 bytes        (minimum .NET object)
Bucket 1:  25 – 64 bytes
Bucket 2:  65 – 256 bytes
Bucket 3:  257 – 1,024 bytes
Bucket 4:  1,025 – 8,192 bytes
Bucket 5:  8,193 – 85,000 bytes
Bucket 6:  85,001 – 1,048,576 bytes  (LOH boundary → 1MB)
Bucket 7:  > 1,048,576 bytes
```

These 8 counters merge trivially across parallel threads in `TypeIndexBuilder.Merge()`.
Store in `HeapIndexBuildResult.GlobalSizeBuckets` as `long[]`. **No new disk file needed** —
64 bytes written into the ObjectIndex header (4 bytes reserved → expand to 72 byte header).

### Phase 2 Computation
`MemoryAnalyzer` reads `heapIndex.GlobalSizeBuckets` directly from `HeapIndexBuildResult`.
`SizeBucketHistogram` is a pure struct copy — zero allocation path.

---

## Related Analyzers
- **`DominatorAnalyzer`** (new) — populates `TypeSnapshot.EstimatedRetainedBytes` in a post-pass
- **`ModuleAnalyzer`** — provides `ModuleHeapStats` that attributes heap bytes per assembly
- **`GCGenerationAnalyzer`** — provides per-type generation distribution (§3.1 gen column)
