# LohFragmentationAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Implementation Priority **6** · Effort: Low · ✅ **Completed**

## Report Sections Served
- §10.1 LOH Summary (total size, segment count)
- §10.2 Fragmentation (per-segment %, free blocks, free gap histogram)
- §10.3 Large Object Lifetimes (top large objects by size)

---

## Currently Produces
- `LohFragmentationDomainResult`: per-segment fragmentation %, free bytes, largest free block
- `LohSegmentSnapshot` list with address, total/used/free bytes, object/free-object counts
- Overall `FragmentationPercent`, `TotalLohBytes`, `TotalFreeBytes`

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Free-gap histogram (gap size distribution) | §10.2 | Medium |
| Long-lived object classification (Gen2 LOH objects) | §10.3 | High |
| Large objects sorted by size (top N LOH objects) | §10.3 | Medium |

---

## Required Changes

1. **Add `FreeGapHistogram`** — `IReadOnlyList<FreeGapBucket>` per segment, where each bucket is
   `(GapSizeRange, GapCount)`. Build during the existing per-segment object scan by collecting
   each contiguous free-block size and bucketing it. Zero extra heap scans.
2. **Add `TopLargeObjects`** — `IReadOnlyList<LargeObjectSnapshot>` (top 20 by size). Capture
   `Address`, `TypeName`, `Size` for non-free objects during the existing per-segment object walk.
   Cap at 20 entries — use a min-heap or partial sort pattern. `LargeObjectSnapshot` must be a
   new `internal sealed record`.
3. **Note**: Gen2 LOH cross-reference (§10.3) requires `GCGenerationAnalyzer` data. This is a
   **report-layer join**, not a change to `LohFragmentationAnalyzer` itself. The LOH analyzer
   does not need to re-scan generations.

---

## Phase Assignment

### Current Phase Assignment
| Step | Current Phase | Location |
|------|--------------|----------|
| Enumerate LOH segments | Phase 2 | `foreach ClrSegment in heap.Segments` — second pass |
| Per-segment object walk | Phase 2 | `segment.EnumerateObjects()` — re-walks LOH objects |
| Free block detection | Phase 2 | Computed inline during per-segment object walk |
| Top large objects | ❌ Missing | Not captured |

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| LOH free blocks | **Phase 1** | Detected during existing segment scan, written to `LohFreeBlockIndex.bin` |
| Top large objects | **Phase 1** | Top-100 non-free LOH objects captured to `LargeObjectIndex.bin` |
| Fragmentation % calculation | Phase 2 | Derived from `LohFreeBlockIndex.bin` — no re-scan |
| `FreeGapHistogram` build | Phase 2 | Bucket `LohFreeBlockIndex.bin` entries by size range |

### Phase 1 Extensions

**`LohFreeBlockIndex.bin`** — written during Phase 1 LOH segment scan:
```
Header (16 bytes): Magic(4) | Version(4) | RecordCount(8)
Per record (24 bytes): SegmentAddress(8) | FreeBlockOffset(8) | FreeBlockSize(8)
```
Free blocks detected by `obj.Type?.Name == "Free"` — same as current Phase 2 logic.
Size estimate: LOH free blocks are typically hundreds-to-thousands. Well under 1MB.

**`LargeObjectIndex.bin`** — top-100 LOH objects by size (min-heap maintained during Phase 1):
```
Header (16 bytes): Magic(4) | Version(4) | RecordCount(8)
Per record (24 bytes): Address(8) | MethodTable(8) | Size(8)
```
Cap at 100 entries. Min-heap pattern keeps memory O(100) during Phase 1.

### Phase 2 Computation
**Disk mode**: `LohFragmentationAnalyzer` reads `LohFreeBlockIndex.bin` and `LargeObjectIndex.bin`
from the `.dumpindex/` directory. The per-segment `EnumerateObjects()` call is eliminated
entirely. Total LOH data is produced from pre-indexed records.

**Memory mode**: `IndexPath = "<memory>"` so `Path.GetDirectoryName` returns `""` and both
satellite files are absent. `AnalyzeFromIndex` detects this (`indexDir.Length == 0`) and
immediately delegates to `AnalyzeFromHeap`, which does a full LOH segment scan. This produces
exactly the same rich output (fragmentation %, free-gap histogram, top large objects, per-segment
snapshots) as disk mode.

**Both modes produce identical `LohFragmentationDomainResult`.** The only difference is
execution path: disk mode reads pre-built indices (faster for large dumps); memory mode scans
LOH segments directly (always ≤4GB, so the scan cost is bounded).

---

## Related Analyzers
- **`GCGenerationAnalyzer`** — Gen2 LOH cross-reference is a report-layer join; no analyzer change needed
- **`SegmentAnalyzer`** — shares `SegmentReflectionCache`; deduplication target → shared `SegmentReflectionHelper`
- **`ArrayAnalyzer`** (new) — also consumes `LargeObjectIndex.bin` for large array identification
