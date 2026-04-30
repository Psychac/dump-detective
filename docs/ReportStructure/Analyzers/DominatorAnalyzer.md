# DominatorAnalyzer — Design Spec

## Status
**New** · Implementation Priority **11** · Effort: Very High · ⏳ **Pending**

> ⚠️ Implement LAST. Requires all other analyzers in place. Mandatory performance testing
> on 10GB+ dumps before merging. Introduces "Phase 1.5" bounded reference edge collection.

## Report Sections Served
- §3.1 Detailed Type Table (estimated retained size per type)
- §3.2 Dominator Candidates (nomination, largest instance, retention estimate)
- §4.1 Retention Hotspots (objects retaining large sub-graphs)
- §4.2 Dominator Tree Approximation (exclusive retained bytes, dominator impact score)
- §4.3 Retention Patterns (cache chain / event chain / thread-local classification)

## Rationale
This is the **largest capability gap** in the system. Retained size and dominator tree are
foundational to professional-tier memory analysis and no current analyzer provides them.

---

## Domain Result

```csharp
DominatorDomainResult(
    int CandidatesAnalyzed,
    ulong TotalRetainedBytesEstimated,
    IReadOnlyList<DominatorCandidate> TopDominatorsByRetainedSize,
    IReadOnlyList<RetentionPatternFinding> DetectedPatterns,
    bool WasBudgetCapped,
    string CapReason)

DominatorCandidate(
    ulong Address,
    string TypeName,
    ulong ShallowSize,
    ulong EstimatedRetainedBytes,
    int RetainedObjectCount,
    RetentionPatternHint PatternHint)

// PatternHint enum
RetentionPatternHint : None | StaticCache | EventChain | ThreadLocal | Singleton | Collection

RetentionPatternFinding(
    RetentionPatternHint Pattern,
    int InstanceCount,
    ulong TotalRetainedBytes,
    string Description)
```

---

## Implementation Strategy

- **Input**: Top 50 types by shallow size from `HeapAnalysisCache` type statistics
- **Per candidate**: Sample 1 representative object of each type; run bounded reverse-BFS
  using `ReverseReferenceIndex` (scoped, not full graph) to estimate retained sub-graph size
- **Budget**: `MaxNodes = 2000` per candidate, `MaxEdges = 5000` total across all candidates
- **Pattern detection**: After BFS, classify by field types in the retained set:
  - `Dictionary/ConcurrentDictionary` → `StaticCache`
  - `EventHandler/MulticastDelegate` → `EventChain`
  - `[ThreadStatic]` or `ThreadLocal<T>` → `ThreadLocal`
- **Post-pass**: Write `EstimatedRetainedBytes` back into a shared result that
  `MemoryAnalyzer` and `ModuleAnalyzer` can expose via their type snapshots

> ⚠️ **Performance constraint**: MUST run after `MemoryAnalyzer`. Must **never** enumerate
> the full heap. Reverse-reference index must be scoped to candidate addresses only.

---

## Phase Assignment

### The "Phase 1.5" Concept

`DominatorAnalyzer` requires reference edges not captured in standard Phase 1. Building a full
reference edge index is prohibitive (80M objects × avg 5 refs = 400M edges × 16 bytes = 6.4GB).

A **bounded second pass** runs after Phase 1 completes but before Phase 2 begins:

```
Phase 1.5 — Bounded Reference Edge Collection:
  1. Select CandidateMtSet = top-50 MTs by TypeAggregates.TotalSize
  2. Sequential read of ObjectIndex.bin (already on disk)
  3. For each entry where MT ∈ CandidateMtSet:
       obj = heap.GetObject(entry.Address)
       foreach reference in obj.EnumerateReferences():
           write (entry.Address, referenceTarget) to PartialRefEdgeIndex.bin
       if total edges written > MaxEdgesBudget (500,000): set CappedFlag = true; break
  4. PartialRefEdgeIndex.bin is a Phase 2 input
```

```
PartialRefEdgeIndex.bin
Header (24 bytes): Magic(4) | Version(4) | CandidateCount(4) | EdgeCount(8) | Capped(1) | Pad(3)
Per record (16 bytes): SourceAddress(8) | TargetAddress(8)
```

Size estimate: capped at 500K edges = 8MB maximum.

Phase 1.5 is implemented as `IBoundedReferenceEdgeBuilder` service — separate from
`IObjectIndexWriter`, to keep Phase 1 writers simple.

```
Trigger condition: DominatorAnalyzer in analyzer set AND ObjectIndex.bin exists
Memory budget:     ≤ 64MB for in-progress edge buffer (flushed every 32K edges)
Time budget:       Configurable timeout (default 60 seconds)
Capping:           Edge count cap (500K), time cap — both independently enforced
```

### Phase 2 Computation
```
DominatorAnalyzer.AnalyzeAsync(context):
  1. Read PartialRefEdgeIndex.bin → build per-source adjacency list (in-memory, bounded)
  2. Build reverse edge map: target → [sources] (bounded to candidates)
  3. BFS from each candidate address following reverse edges, accumulate retained set
  4. EstimatedRetainedBytes = sum(size of all objects in retained set)
  5. Classify by pattern: scan retained set type names for Cache/Event/ThreadLocal hints
  6. Build DominatorDomainResult
  7. Post-pass: write EstimatedRetainedBytes into shared TypeSnapshot entries
     via context.SetRetainedSizeEstimate(typeName, bytes)
```

---

## Related Analyzers
- **`MemoryAnalyzer`** — `TypeSnapshot.EstimatedRetainedBytes` is populated by post-pass
- **`ModuleAnalyzer`** — `ModuleHeapStats.TotalRetainedEstimateBytes` is populated by post-pass
- **`GCRootAnalyzer`** (new) — root path data complements dominator tree for §4.3 pattern classification
- **`CollectionAnalyzer`** — `CachePatternScore` feeds `StaticCache` pattern detection
