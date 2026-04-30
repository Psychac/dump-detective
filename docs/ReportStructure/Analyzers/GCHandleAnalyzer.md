# GCHandleAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Implementation Priority **5** · Effort: Low

## Report Sections Served
- §5.1 Root Distribution (strong GC handle kind — partial)
- §9.3 Pinning Impact (pinned handle count, types, retained bytes)
- §24.1 Weak GC Handle Population (weak handle count — partial)

---

## Currently Produces
- `GCHandleDomainResult`: total handles by kind, pinned type counts, all-target type counts
- `StrongLikeHandles`, `WeakLikeHandles` summary counts

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Pinned object **retained bytes** estimate | §9.3 | High |
| Per-pinned-object size contribution | §9.3 | Medium |
| Dependent handle relationship (cross-references with `DependentHandleAnalyzer`) | §12 | Low |

---

## Required Changes

1. **Add retained-size estimation for pinned handles** — during `foreach ClrHandle`, when
   `handle.HandleKind == Pinned`, resolve `handle.Object` → `ClrObject` → accumulate
   `Size` into `pinnedRetainedBytes`. Add `PinnedRetainedBytes` field to
   `GCHandleDomainResult`.
2. **Add `TopPinnedObjectsBySize`** — `IReadOnlyList<NameBytesEntry>` — top pinned types by
   total pinned bytes. Already has `pinnedTypes` dictionary; extend to track bytes per type
   alongside count.
3. **Reuse `methodTableNameCache`** pattern already present — the existing
   `methodTableNameCache` dict is a good pattern; ensure it's applied to size accumulation
   too for the pinned path.

---

## Phase Assignment

### Current Phase Assignment
| Step | Current Phase | Location |
|------|--------------|----------|
| Enumerate handles | Phase 2 | `runtime.EnumerateHandles()` in `GCHandleAnalyzer.Analyze()` |
| Classify by kind | Phase 2 | In-place during enumeration |
| Pinned size calculation | ❌ Missing | Not currently computed |

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| Handle enumeration | **Phase 1** | `runtime.EnumerateHandles()` — separate CLR API, not heap streaming |
| Write `HandleSnapshot.bin` | **Phase 1** | Written by `DiskBackedObjectIndexWriter`; consumed by `WeakReferenceAnalyzer` (not this analyzer) |
| Pinned size accumulation | Phase 2 | `heap.GetObject(targetAddress).Size` per pinned handle — runs in both modes identically |

**Both modes produce identical output.** `GCHandleAnalyzer` calls `runtime.EnumerateHandles()`
directly in Phase 2 for both memory-backed and disk-backed sessions. It does NOT read
`HandleSnapshot.bin` — that file is consumed by `WeakReferenceAnalyzer` for weak handle
liveness analysis (§24). There is no mode-specific fast-path in the current implementation.

### Phase 1 Extension — `HandleSnapshot.bin`

Handle enumeration uses `runtime.EnumerateHandles()` which is a separate CLR API (not heap
segment streaming). It runs as a **dedicated Phase 1 step** after the heap segment scan
completes, before `HeapIndexBuildResult` is returned.

```
HandleSnapshot.bin
Header (16 bytes): Magic(4) | Version(4) | RecordCount(8)
Per record (20 bytes):
    ObjectAddress(8) | MethodTable(8) | Kind(1) | Padding(3)
```

`Kind` encodes `ClrHandleKind` as a single byte (max 10 known kinds).
Size estimate: 50K handles × 20 bytes = 1MB. Typically much smaller.

### Phase 2 Computation
```
GCHandleAnalyzer.AnalyzeAsync(context):
  1. Call runtime.EnumerateHandles() — same in both memory and disk modes
  2. For Pinned handles: call heap.GetObject(targetAddress) to resolve Size
  3. Accumulate PinnedRetainedBytes per MT via methodTableNameCache
  4. Build GCHandleDomainResult including TopPinnedObjectsBySize
```

No satellite file is read by this analyzer. `HandleSnapshot.bin` is written by Phase 1
(disk mode only) for future consumption by `WeakReferenceAnalyzer`.

---

## Related Analyzers
- **`WeakReferenceAnalyzer`** (new) — also consumes `HandleSnapshot.bin` for weak handle liveness analysis (§24)
- **`DependentHandleAnalyzer`** — covers dependent handles; `GCHandleAnalyzer` covers the remaining kinds
- **`SegmentAnalyzer`** — pinned handle generation correlation (§9.3 Gen0/Gen1 pinned objects) is a report-layer join
