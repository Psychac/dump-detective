# SegmentAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Implementation Priority (no standalone priority; changes are low effort) · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §2.1 Heap Composition (SOH/LOH/POH/FOH proportions, Server GC topology)
- §9.2 GC Efficiency (segment utilisation, cross-heap distribution)
- §10.1 LOH/POH Summary (POH per-type distribution)
- §10.4 POH Diagnostics
- §10.5 FOH Diagnostics
- §25.1 Committed vs Reserved (partial — per-segment committed bytes)
- §25.2 Segment Lifecycle (segment count by kind)

---

## Currently Produces
- `SegmentAnalysisDomainResult`: per-kind bytes + object counts (SOH/LOH/POH/Frozen)
- `HeapSegmentSnapshot` list — per-segment address, start, end, committed bytes, generation, kind
- `TopSegmentsCount = 10` largest segments surfaced

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Object size distribution per segment kind | §2.1 | Low |
| Committed vs reserved bytes distinction | §2.1, §25.1 | Low |
| POH per-type breakdown | §10.1 | Medium |
| Ephemeral segment fill % | §25.2 | Medium — needs `ClrSegment.IsEphemeral` + `CommittedMemory ÷ Length` |
| Logical heap assignment (`ClrSegment.LogicalHeap`) | §25.2 | Medium — per-CPU heap in server GC mode |
| Total reserved memory across all segments | §25.1 | Low — passed to `SegmentReservationAnalyzer` |

---

## Required Changes

1. **Add `CommittedVsReserved`** ratio per segment kind to the result — `SegmentAnalyzer` already
   reads `CommittedBytes` via reflection; add `ReservedBytes` if available in `ClrSegment`.
2. **Add `PohTypeDistribution`** — `IReadOnlyList<NameCountEntry>` — top pinned-object types by
   count. Enumerate POH segment objects once (they are typically small in number).
3. The duplicate `SegmentReflectionCache` between `SegmentAnalyzer` and `LohFragmentationAnalyzer`
   should be **deduplicated** into a shared `SegmentReflectionHelper` utility class.

---

## Phase Assignment

`SegmentAnalyzer` is **entirely Phase 2**. It enumerates `ClrHeap.Segments` which is a
runtime API call that cannot be streamed during Phase 1 (segment membership changes as GC
runs; the heap must be paused/quiesced at the dump moment for consistent reads).

The POH type distribution addition requires a targeted per-object scan of POH segments only
(typically very small — dozens to hundreds of objects). Bounded, fast.

---

## Related Analyzers
- **`SegmentReservationAnalyzer`** (new, §25) — handles the `ReservedMemory`, `IsEphemeral`, and `LogicalHeap` data that `SegmentAnalyzer` does not produce
- **`LohFragmentationAnalyzer`** — shares `SegmentReflectionCache`; deduplication target
- **`GCGenerationAnalyzer`** — generation data is now Phase 1-backed; `SegmentAnalyzer` may cross-reference for per-kind generation distribution
