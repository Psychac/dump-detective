# FinalizableObjectAnalyzer — Design Spec

## Status
**New** · Implementation Priority **15** · Effort: Medium · ⏳ **Pending**

## Report Sections Served
- §21.1 Finalizable Object Population (all `IsFinalizable` objects, by generation, undisposed detection)
- §21.2 Finalizer Queue Analysis — deep sub-graph retention and resurrection detection
  (§21.2 queue count and top types are already covered by `MemoryLeakAnalyzer`)

> §21.3 Finalizer Thread Health is fully covered by `ThreadAnalyzer`. See [ThreadAnalyzer.md](ThreadAnalyzer.md).

## Rationale
`MemoryLeakAnalyzer` counts the finalizer queue but does not enumerate all finalizable objects
across the heap or detect sub-graph retention. §21.1 requires a full population sweep.

---

## Domain Result

```csharp
FinalizableObjectDomainResult(
    int TotalFinalizableObjects,
    ulong TotalFinalizableBytes,
    int Gen0Count, int Gen1Count, int Gen2Count,
    int FinalizerQueueCount,
    ulong FinalizerQueueRetainedBytes,
    bool PotentialResurrectionDetected,
    IReadOnlyList<TypeGenerationProfile> TopFinalizableTypesByGen2Count,
    IReadOnlyList<FinalizerQueueEntry> TopQueueEntriesByRetainedSize)

FinalizerQueueEntry(
    ulong Address,
    string TypeName,
    ulong ShallowSize,
    ulong EstimatedRetainedBytes,
    bool IsDisposableType,
    bool DisposedFieldFound,
    bool DisposedFieldValue)
```

---

## Implementation Strategy

- Phase 1 flags finalizable types via `TypeAggregateIndexEntry.Flags` bit 3
- Phase 2 filters `ObjectIndex.bin` to finalizable MTs — no full heap re-scan
- Generation correlation: from `TypeAggregates.Gen0/1/2Count` (after GCGenerationAnalyzer changes)
- Finalizer queue objects: `ClrHeap.EnumerateRoots()` filtered to `RootKind.Finalizer`
  or `RootIndex.bin` `Kind == 2` after GCRootAnalyzer Phase 1 extension
- Sub-graph retention: bounded BFS from top-10 finalizer queue objects only
- `IDisposable` check: `ClrType.Interfaces` contains `System.IDisposable`
- `_disposed` field heuristic: `ClrType.Fields.FirstOrDefault(f => f.Name == "_disposed")` —
  if found, read value via `ClrInstanceField.Read<bool>(address)`

---

## Phase Assignment

| Step | Phase | Notes |
|------|-------|-------|
| Tag `IsFinalizableType` in `TypeAggregateIndexEntry.Flags` bit 3 | **Phase 1** | First encounter: `ClrType.IsFinalizable` flag |
| Finalizer queue root records | **Phase 1** | Captured in `RootIndex.bin` `Kind == 2` (Finalizer) |
| Population sweep (count + size per finalizable type) | Phase 2 | Filter `ObjectIndex.bin` to finalizable MTs via Flags bit |
| Generation correlation | Phase 2 | From extended `TypeAggregates.Gen0/1/2Count` |
| Sub-graph BFS for top queue objects | Phase 2 | Bounded BFS, top 10 queue objects only |

**No new disk file required.** Reuses `RootIndex.bin` (GCRootAnalyzer Phase 1) and `TypeAggregateIndexEntry.Flags`.

---

## Related Analyzers
- **`MemoryLeakAnalyzer`** — provides queue count and top types; this analyzer extends to full population and sub-graphs
- **`ThreadAnalyzer`** — §21.3 finalizer thread health (blocked, frames) fully covered there
- **`GCRootAnalyzer`** (new) — `RootIndex.bin` `Kind == 2` provides finalizer queue root set without re-enumeration
- **`GCGenerationAnalyzer`** — `PerTypeGenerationProfile` provides gen breakdown for finalizable types
