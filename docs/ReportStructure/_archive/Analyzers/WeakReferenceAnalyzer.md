# WeakReferenceAnalyzer — Design Spec

## Status
**New** · Implementation Priority **20** · Effort: Low · ✅ **Completed**

## Report Sections Served
- §24.1 Weak GC Handle Population (alive vs dead targets, per-kind breakdown)
- §24.2 `WeakReference<T>` Object Analysis (stale wrapper detection)
- §24.3 `ConditionalWeakTable` — live vs dead key analysis (complements `DependentHandleAnalyzer`)

## Rationale
Stale `WeakReference` wrappers accumulate in caches silently. No current analyzer detects
whether weak handles are targeting collected (dead) objects.

---

## Domain Result

```csharp
WeakReferenceDomainResult(
    int TotalWeakHandles,
    int AliveWeakTargets,
    int DeadWeakTargets,
    double DeadTargetRatio,
    int WeakReferenceObjectCount,
    ulong WeakReferenceObjectBytes,
    int StaleWrapperCount,
    IReadOnlyList<NameCountEntry> TopWeakTargetTypes,
    IReadOnlyList<NameCountEntry> TopStaleWrapperHolderTypes,
    int DependentHandleDeadKeyCount)
```

---

## Implementation Strategy

- Weak handles: read from `HandleSnapshot.bin` (GCHandleAnalyzer Phase 1) filtered to
  `Kind in (4=WeakHandle, 5=WeakLong)` — no `runtime.EnumerateHandles()` re-call needed
- For each handle: `ClrHeap.GetObject(ObjectAddress).IsValid` — alive/dead check
- `WeakReference<T>` objects: filter `TypeAggregates` for type name == `"System.WeakReference`1"`
  and `"System.WeakReference"` — read `m_handle` field via `ClrInstanceField`
- Stale wrapper holder: for stale wrappers, check which type holds a reference to them
  via `RootIndex.bin` or bounded reverse lookup
- Dependent handle dead-key: from `HandleSnapshot.bin` `Kind == DependentHandle` entries —
  check `ClrHeap.GetObject(ObjectAddress).IsValid` for source address
- **Bounded**: cap liveness check at 50K handles max

---

## Phase Assignment

| Step | Phase | Notes |
|------|-------|-------|
| Weak + dependent handles in `HandleSnapshot.bin` | **Phase 1** | Captured by GCHandleAnalyzer Phase 1 extension |
| Liveness check per weak handle target | Phase 2 | `ClrHeap.GetObject(address).IsValid` per weak handle record |
| `WeakReference<T>` object scan | Phase 2 | Filter `TypeAggregates` for known type names |
| `m_handle` field inspection for stale wrappers | Phase 2 | `ClrInstanceField.Read<IntPtr>()` on each wrapper object |
| Dependent handle dead-key count | Phase 2 | `HandleSnapshot.bin` Kind=DependentHandle + IsValid check on source |

No new disk file required. Uses `HandleSnapshot.bin` from GCHandleAnalyzer Phase 1.

---

## Related Analyzers
- **`GCHandleAnalyzer`** — `HandleSnapshot.bin` shared input; eliminates duplicate `runtime.EnumerateHandles()` call
- **`DependentHandleAnalyzer`** — source/target type distribution covered there; live/dead key analysis is this analyzer's contribution to §24.3
- **`InsightEngine`** — stale wrapper accumulation finding: `DeadTargetRatio > 0.5` threshold alert
