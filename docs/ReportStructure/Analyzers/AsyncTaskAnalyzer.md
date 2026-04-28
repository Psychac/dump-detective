# AsyncTaskAnalyzer — Design Spec

## Status
**New** (split from `HangAnalyzer`) · Implementation Priority **2** · Effort: Medium

## Report Sections Served
- §8.1 Task Summary (total tasks, status breakdown)
- §8.2 Orphaned Tasks (never-awaited, faulted-no-continuation)
- §8.3 Continuation Chains (depth, top types, async state machine cross-reference)

## Rationale
Task lifecycle analysis is a first-class §8 report section. It is currently buried inside
`HangDomainResult` alongside thread-blocking data, with no orphan task detection.

---

## Domain Result

```csharp
AsyncTaskDomainResult(
    int TotalTasks,
    int PendingTasks,
    int RunningTasks,
    int FaultedTasks,
    int CanceledTasks,
    int CompletedTasks,
    int OrphanedTasks,
    int MaxContinuationDepth,
    double AvgContinuationDepth,
    bool TaskScanLimited,
    IReadOnlyList<NameCountEntry> TopPendingTaskTypes,
    IReadOnlyList<NameCountEntry> TopFaultedTaskTypes,
    IReadOnlyList<NameCountEntry> TopContinuationTypes,
    IReadOnlyList<OrphanedTaskSnapshot> TopOrphanedTasks)

OrphanedTaskSnapshot(ulong Address, string TaskType, string? ResultType, ulong Size)
```

---

## Implementation Strategy

- Scan the heap for objects whose `MethodTable` resolves to `Task`, `Task<T>`,
  `ValueTask`, `IValueTaskSource` (use heap index MT → type name cache for O(1) lookup)
- For each task object, read `m_stateFlags` field to determine state
- For orphan detection: read `m_continuationObject` field — if null or
  `System.Threading.Tasks.Task+<>c` (no-op), classify as orphaned
- For chain depth: BFS following `m_continuationObject` links, depth-capped at 20
- **Bounded by** `MaxTasksToScan` (carry over from `HangAnalyzer`'s existing constant)
- Uses heap index (not raw `heap.EnumerateObjects()`) for the initial type-filtered scan

---

## Phase Assignment

### Current Phase Assignment (in HangAnalyzer)
| Step | Current Phase | Location |
|------|--------------|----------|
| Detect Task MTs | Phase 2 | `HangAnalyzer` resolves task type names per object during full heap scan |
| Enumerate task objects | Phase 2 | Full `heap.EnumerateObjects()` loop, not index-backed |
| Read m_stateFlags | Phase 2 | Per-object ClrMD field read inside HangAnalyzer loop |
| Continuation chain traversal | Phase 2 | BFS via `m_continuationObject` field |

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| Tag Task/ValueTask MTs | **Phase 1** | `Flags` byte bit 1 = `IsTaskType` in TypeAggregateIndexEntry |
| Build `TaskIndex.bin` | **Phase 1** | Lightweight index file capturing state per task object |
| Classify task states | Phase 2 | Read TaskIndex, no heap re-scan for state classification |
| Continuation BFS | Phase 2 | Still requires ClrMD field access — bounded by MaxTasksToScan |
| Orphan detection | Phase 2 | Read `m_continuationObject` field for each task in TaskIndex |

### Phase 1 Extension — `TaskIndex.bin`

During the parallel segment scan, when `TypeAggregateIndexEntry.Flags.IsTaskType` is set for
a given MT, write a record to `TaskIndex.bin`:

```
Header (16 bytes): Magic(4) | Version(4) | RecordCount(8)
Per record (20 bytes):
    Address(8) | MethodTable(8) | StateFlags(4)
```

`StateFlags` is read from `obj.ReadField<int>("m_stateFlags")` — one cheap integer field read.
If field not found (version differences), write `StateFlags = 0` and let Phase 2 re-resolve.
Size estimate: 1M tasks × 20 bytes = 20MB (worst case).

### Phase 2 Computation
```
AsyncTaskAnalyzer.AnalyzeAsync(context):
  1. Read TaskIndex.bin → task address list + pre-captured state flags
  2. Classify each task by state from StateFlags (bit masks)
  3. For top N tasks: read m_continuationObject field → BFS chain depth
  4. Orphan = m_continuationObject is null or sentinel type
  5. Build AsyncTaskDomainResult
```

> `TaskScanLimited` flag is preserved on `AsyncTaskDomainResult` for §17 Confidence output.

---

## Related Analyzers
- **`HangAnalyzer`** — source of the split; retains blocking/hang analysis
- **`AsyncStateMachineAnalyzer`** (new) — §23 state machine cross-reference with faulted tasks
- **`ThreadAnalyzer`** — `AsyncChainThreadCount` and `MaxAsyncChainDepth` are complementary to continuation chain data
