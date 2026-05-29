# RetentionAnalyzer — Coverage & Change Spec

## Status
**Existing** · Split + Modify · Implementation Priority **1** (split) · Effort: Low · ✅ **Split Completed** · ⏳ **Modifications Pending**

## Report Sections Served (post-split)
- §6.1 Leak Candidates (highly-referenced objects, finalizer queue candidates)
- §6.2 Leak Classification (finalizer-backed, highly-retained)
- §6.4 Leak Impact (memory bytes at risk)
- §21.2 Finalizer Queue depth and top types

> ⚠️ String analysis is extracted to `StringAnalyzer` (Priority 1, same split effort).
> See [StringAnalyzer.md](StringAnalyzer.md) for the extracted component.

---

## Currently Produces
- `RetentionDomainResult`: finalizer queue count, duplicate string stats, highly-referenced objects
- Performs **two logically unrelated tasks** in one heap pass:
  - String deduplication analysis (→ extracted to `StringAnalyzer`)
  - Incoming-reference counting for highly-referenced objects (→ stays here)

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| String data as standalone result | §11.1, §11.2 | High — report section mismatch → extracted |
| Unified leak candidate score | §6.1 | High |
| Leak classification label per candidate | §6.2 | High |
| Memory + performance impact estimate | §6.4 | Medium |

---

## Required Changes — SPLIT

### ✂️ Extract `StringAnalyzer` (new analyzer, Priority 1)
Move all string-related logic and data from `RetentionAnalyzer` into `StringAnalyzer`:
- `ProcessStringObjectByAddress` → `StringAnalyzer`
- `IsStringEntry` helper → `StringAnalyzer`
- `stringStats`, `stringMethodTables` dictionaries → `StringAnalyzer`
- Remove string fields from `RetentionDomainResult` (now in `StringDomainResult`)

### Modify `RetentionAnalyzer` (after split)
After string logic is removed, `RetentionAnalyzer` retains:
- Finalizer queue analysis
- Highly-referenced object detection
- Add **`SuspicionScore`** to `HighlyReferencedObjectSnapshot` — integer 0–100 derived from:
  `IncomingReferences`, `Size`, whether the object is in Gen2
- Add **`LeakClassification`** enum value to `HighlyReferencedObjectSnapshot`:
  `Unknown | HighlyRetained | FinalizerBacked | ThreadRetained`
- Add `IReadOnlyList<LeakCandidateSnapshot>` as top-level field — ranked union of finalizer
  candidates + highly-referenced candidates, sorted by `SuspicionScore` descending

---

## Phase Assignment

`RetentionAnalyzer` is **entirely Phase 2**. The highly-referenced object detection relies on
reference walking (`ClrObject.EnumerateReferences()`) which requires a live heap. The finalizer
queue scan uses `ClrHeap.EnumerateRoots()` filtered to finalizer kind.

The string split does NOT change `RetentionAnalyzer`'s phase footprint. After the split,
`RetentionAnalyzer` is leaner and faster (no string hashing).

> **Confidence signal**: `SkippedReferenceAddresses` is already surfaced in `RetentionDomainResult`
> and must be preserved post-split. It feeds §17 Confidence & Limitations.

---

## Related Analyzers
- **`StringAnalyzer`** (new, extracted) — handles §11 string data; split from this analyzer
- **`StaticRootLeakDetector`** — provides static retention signal that feeds §6.1 unified score
- **`FinalizableObjectAnalyzer`** (new) — extends §21.2 beyond queue count to full population + sub-graph retention
- **`DominatorAnalyzer`** (new) — provides `SuspicionScore` input via retained size estimation
