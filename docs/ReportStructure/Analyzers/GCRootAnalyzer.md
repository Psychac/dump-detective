# GCRootAnalyzer — Design Spec

## Status
**New** · Implementation Priority **10** · Effort: High

## Report Sections Served
- §5.1 Root Distribution (all root kinds, retained bytes per kind)
- §5.2 Root Severity Ranking (top 20 roots by impact)
- §5.3 Root Paths (root → object chain paths for top suspects)

## Rationale
`BoundedRootPathFinder` and `StaticRootLeakDetector` exist as utilities and partial detectors.
No pipeline analyzer produces a **unified root intelligence result** covering all root kinds
(Static, Stack, GCHandle, Finalizer) with retention estimates and severity ranking.

---

## Domain Result

```csharp
GCRootDomainResult(
    int TotalRoots,
    IReadOnlyList<RootKindSummary> ByKind,
    IReadOnlyList<RootFinding> TopRootsBySeverity,
    IReadOnlyList<RootPathFinding> RootPaths,
    bool PathSearchCapped,
    int PathSearchCappedCount)

RootKindSummary(
    string Kind,
    int Count,
    ulong EstimatedRetainedBytes,
    double PctOfManagedHeap)

RootFinding(
    string RootKind,
    ulong RootAddress,
    string? FieldDescription,
    string TargetTypeName,
    ulong TargetAddress,
    ulong EstimatedRetainedBytes,
    int SeverityScore)

RootPathFinding(
    ulong TargetAddress,
    string TargetTypeName,
    string RootKind,
    IReadOnlyList<string> PathTypeNames,
    int PathLength,
    bool WasCapped)
```

---

## Implementation Strategy

- Use `cache.GetOrBuildValidRoots(heap)` — **do not re-enumerate roots from scratch**
- Group roots by kind using a single pass
- For top N roots by estimated retained bytes (N = 25, configurable), run
  `BoundedRootPathFinder` with tight budget (`MaxNodes = 500`, `MaxDepth = 20`)
- Retained bytes per root = BFS-discovered object count × average type size (from heap index)
- Do **not** build a full reverse graph; scope entirely to sampled roots
- Severity score = `f(RetainedBytes, RootKind, IsStatic)`

---

## Phase Assignment

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| Enumerate all GC roots once | **Phase 1** | Dedicated Phase 1 step after segment scan |
| Write `RootIndex.bin` | **Phase 1** | One record per root |
| Group roots by kind | Phase 2 | Pure iteration over RootIndex |
| BFS retention estimate per root | Phase 2 | `BoundedRootPathFinder` on top-25 roots |
| `RootKindSummary` + severity ranking | Phase 2 | Derived from grouped roots |

### Phase 1 Extension — `RootIndex.bin`

After the heap segment scan completes but before `HeapIndexBuildResult` is returned, enumerate
`heap.EnumerateRoots(enumerateStatics: true)` and write:

```
RootIndex.bin
Header (16 bytes): Magic(4) | Version(4) | RecordCount(8)
Per record (20 bytes):
    TargetAddress(8) | RootAddress(8) | Kind(1) | Padding(3)
```

Kind encoding:
```
0 = Static       3 = StrongHandle    6 = DependentHandle
1 = Stack        4 = WeakHandle      7 = Other
2 = Finalizer    5 = PinnedHandle
```

Size estimate: 100K roots × 20 bytes = 2MB.

`cache.GetOrBuildValidRoots()` becomes a read from `RootIndex.bin` — no heap re-walk.
`StaticRootLeakDetector` and `FinalizableObjectAnalyzer` also benefit from this index.

### Phase 2 Computation
```
GCRootAnalyzer.AnalyzeAsync(context):
  1. Read RootIndex.bin → group by Kind
  2. Compute EstimatedRetainedBytes per root kind using TypeAggregates (avg size × count)
  3. Sort all roots by estimated impact → select top 25 for path finding
  4. For each top root: run BoundedRootPathFinder(MaxNodes=500, MaxDepth=20)
  5. Build GCRootDomainResult with ByKind summary + RootPaths + TopRootsBySeverity
```

---

## Related Analyzers
- **`StaticRootLeakDetector`** — static-only precursor; shares `BoundedRootPathFinder`; may become internal component
- **`FinalizableObjectAnalyzer`** (new) — consumes `RootIndex.bin` `Kind == 2` for finalizer roots
- **`ReferenceChainAnalyzer`** — shifts to on-demand deep path tracing once `GCRootAnalyzer` handles breadth
