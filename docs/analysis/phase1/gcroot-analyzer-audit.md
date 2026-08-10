# GCRootAnalyzer — Architecture Audit

**Analyzer:** `GCRootAnalyzer`
**Files reviewed:**
- `src/DumpDetective.Analysis/Analyzers/GCRootAnalyzer.cs`
- `src/DumpDetective.Analysis/Utilities/GCRootAnalysisProjection.cs`
- `src/DumpDetective.Analysis/Cache/RootSetCache.cs`
- `src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs`
- `src/DumpDetective.Analysis/Indexing/RootIndexReader.cs`
- `src/DumpDetective.Analysis/Models/GCRootDomainResult.cs`
- `src/DumpDetective.Core/Options/GCRootAnalysisOptions.cs`
- `src/DumpDetective.Reporting/SectionBuilders/GCRootIntelligenceSectionBuilder.cs`
- `src/DumpDetective.Reporting/FindingGenerators/GCRootFindingGenerator.cs`
- `src/DumpDetective.Analysis/Trend/Comparers/GCRootTrendComparer.cs`
- `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/GCRootAnalyzerDiscrepancyTests.cs`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`GCRootAnalyzer` is a Phase-2 analyzer that answers the question: *"What is keeping objects alive on the managed heap?"* It sources root records from `RootSetCache` (Phase-1 disk index via `RootIndex.bin` with live `heap.EnumerateRoots()` fallback), groups them by `ClrRootKind`, estimates retained bytes per root, scores severity, and runs bounded BFS path tracing from the top-N suspects.

The design is coherent. The analyzer has a well-scoped job and uses the shared `RootSetCache` abstraction to avoid duplicating root enumeration work already done in Phase 1.

### Coverage Gaps

**`IAnalyzer` contract gaps.** The analyzer does not declare `Tags`, `Order`, or override `Category`. `Category` falls through to the `AnalyzerCategory.Infer` heuristic, which returns `"GC"` for a name containing "GC" — but `GCRootAnalyzer.Category` explicitly returns `"Memory"`. These are inconsistent: the heuristic would return `"GC"` if the explicit override were removed. `Tags` is `[]` (interface default), preventing tag-based pipeline filtering.

**Silent empty-result path.** If the cache is not a `HeapAnalysisCache` or the heap index is unavailable, the analyzer immediately returns an empty result without logging or surfacing a diagnostic. This is invisible to the operator.

**`FieldDescription` is always `null`.** The `RootFinding.FieldDescription` property exists in the model and is rendered as `"—"` in the report, but the projection never populates it. The disk index stores only `(TargetAddr, RootAddr, Kind)` — field name is lost at index time and never recovered. The live fallback path (`BuildFromLiveHeap`) does not capture `ClrRoot.RootName` either.

**`IsThreadSafe` not declared.** The interface's optional `IsThreadSafe` member (used by the pipeline scheduler) is not declared. `RootSetCache` is not thread-safe (`_roots` and `_staticRootedAddresses` are written without synchronization), so the implicit false default is correct, but it should be explicit.

### Expansion Opportunities

- **Per-thread stack root breakdown.** Stack roots could be attributed to specific managed threads (`ClrThread.ManagedThreadId`) when using the live path, enabling "Thread X holds 45% of all stack roots" diagnostics.
- **Cross-analyzer correlation.** Root kind distribution could be correlated against the LeakAnalyzer's top suspects to answer "is this leaked type also strongly rooted?" — currently no link exists between the two analyzers.
- **Handle table statistics.** GC handle counts by kind exist in ClrMD via `runtime.EnumerateHandles()`. This overlaps partially with root enumeration but gives explicit handle lifetime info.
- **Root growth as a leading indicator.** The `GCRootTrendComparer` tracks `gcroot.total.roots` across dumps but there is no trend-based finding generator — a sustained increase in strong handle count across dump series should generate an actionable finding.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Root kind summary table (kind, count, estimated retained bytes, % of heap) gives a fast triage view.
- Severity-ranked findings table with target type and address enables immediate investigation pivoting.
- Root path groups by target type with up to 3 paths per type is well-structured for multi-instance cases.
- Finalizer root sub-table is surfaced separately when present — the right call.
- Confidence band is emitted with appropriate caveats about heuristic estimates.

### Weaknesses

**Retention estimates are misleading.** `EstimateRetainedBytes` uses `agg.TotalSize / agg.Count` — the average object size for that type from the aggregate index. This is not retained size; it is the average self-size of instances of that type. For heterogeneous types (e.g., `byte[]`, `string`), the average can be wildly off. A single root holding a 500 MB `byte[]` would be estimated as the average size across all arrays of that type, not 500 MB. The report label says "Estimated Retained" but the value is closer to "average self-size."

**Field description is always `"—"`.** Every row in the "Top GC roots by severity" table shows `"—"` in the Field column. This column adds visual weight without diagnostic value. An engineer cannot determine *which static field* is the retention point.

**Path BFS produces type-name lists, not chains.** `RootPathFinding.PathTypeNames` is a flat list of type names in BFS traversal order. This is not a *root-to-object chain* — it is the forward BFS frontier. An engineer cannot reconstruct the object graph path from it. The report renders these as path "hops" but they do not represent a directed retention chain.

**Severity scoring silently drops zero-estimate roots.** Roots where `EstimateRetainedBytes == 0` (invalid object, dead address, or type not in aggregate) are completely excluded from findings. On a crash dump with partially corrupt heap regions, this could silently discard the most significant roots.

**Finding generator covers only 2 of 9 root kinds.** `GCRootFindingGenerator` fires findings only for `StrongHandle` (≥10 MB) and `FinalizerQueue` (≥500 objects). `PinnedHandle`, `Stack`, `AsyncPinnedHandle`, `RefCountedHandle`, `SizedRefHandle`, `ThreadStaticVar`, and `StaticVar` never generate cross-analyzer findings regardless of volume.

**`PinnedHandle` fragmentation is completely absent.** Pinned handle accumulation is one of the most common causes of LOH/SOH fragmentation in production .NET services. The analyzer collects and counts pinned handles but never surfaces a finding or recommendation for them.

**Severity score scale is opaque.** Scores range from 5 to 300. The report renders the raw integer. Neither the report nor the data model communicates what the thresholds mean or how scores compare across different dump sizes.

### Report Improvements

- Replace `"—"` Field column with actual field name when available, or omit the column until populated.
- Rename "Estimated Retained" to "Est. Avg Object Size" to reflect the actual computation.
- Add a "Pinned handles" finding when count exceeds a threshold (e.g., 100), with LOH fragmentation risk note.
- Add findings for `StaticVar`/`ThreadStaticVar` above a retained-size threshold.
- Severity score should be rendered as a band label (Low / Medium / High / Critical) in addition to the raw number.

---

## Audit Area 3 — ClrMD & Platform Utilization

### Double `heap.GetObject` per Root

`GCRootAnalysisProjection.Build` calls both `EstimateRetainedBytes(targetAddr, ...)` and `ResolveTypeName(targetAddr, ...)` for every root. Both independently call `heap.GetObject(targetAddr)`. On large heaps with 100K+ roots, this doubles random-access reads. The two calls should be merged into a single `heap.GetObject` invocation per root with the result shared.

```csharp
// Current: two GetObject calls per root
ulong estimate = EstimateRetainedBytes(targetAddr, heap, aggregates);
string targetType = ResolveTypeName(targetAddr, heap, aggregates);

// Better: one call
ClrObject obj = heap.GetObject(targetAddr);
ulong estimate = EstimateFromObject(obj, aggregates);
string targetType = obj.IsValid && obj.Type?.Name is string n ? n : $"0x{targetAddr:X}";
```

### `RootRecord` Loses Field Context

`RootSetCache.BuildFromLiveHeap` iterates `heap.EnumerateRoots()` but captures only `(TargetAddr, RootAddr, Kind)`. `ClrRoot` exposes:
- `RootName` — the field or variable name for static and thread-static roots
- `StackwalkType` — whether the root is from a precise GC or is a possible root (conservative GC)

None of this is captured. `RootRecord` should store at minimum `RootName` (nullable). The disk index binary format would need an extension to persist it — a v2 format with a variable-length string section would suffice.

### `RootIndexReader.ReadRootCandidates` passes `CancellationToken.None`

In `RootSetCache.GetOrBuildRoots`, the call to `ReadRootCandidates(builtIndex, CancellationToken.None)` ignores the caller's cancellation token. This is a latent issue on very large root index files where the read loop could run for several seconds after cancellation is requested.

### `BoundedGraphWalk.CollectForwardTypeNames` — Consecutive-Only Deduplication

The walk deduplicates type names only for consecutive identical names:
```csharp
if (typeNames.Count == 0 || typeNames[typeNames.Count - 1] != name)
    typeNames.Add(name);
```
Non-consecutive duplicates are kept. For graphs with cycles broken by visited-set (correct) but with repeated types at different depths (e.g., many `Node<T>` hops), the list becomes noisy. A `HashSet<string>` deduplication or a `distinct + in-order` approach would be cleaner.

### `IHeapAnalysisCache` Cast

The analyzer immediately casts `context.Cache` to `HeapAnalysisCache` and returns empty on mismatch. This breaks Open/Closed: any alternate `IHeapAnalysisCache` implementation would silently produce no results. The root retrieval should be mediated through the interface, or `IHeapAnalysisCache` should expose a `TryGetRoots` method.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

**High value, missing entirely:**

1. **Exact per-root retained size.** Using `BoundedGraphWalk.ComputeExclusiveRetained` (which already exists in the platform) on the top-N roots would replace the misleading average-size estimate with an actual bounded retained size. The method is already in `BoundedGraphWalk.cs` and used by other analyzers.

2. **Field chain from static root to target.** For `StaticVar` and `StrongHandle` roots, ClrMD can walk the static field table to resolve the holding field. This gives engineers the exact `MyService.s_cache → Dictionary<K,V> → ...` chain, which is the most actionable GC root diagnostic.

3. **Stack root attribution by thread.** Group stack roots by `ClrRoot` → `ClrThread.ManagedThreadId` and `ClrThread.StackTrace`. Top-N threads by retained root volume. Answers "which thread is blocking GC collection?"

4. **Pinned handle count and fragmentation risk.** Report pinned handle count with an explicit LOH/SOH fragmentation risk estimate. `PinnedHandle` + `AsyncPinnedHandle` combined count exceeding a threshold (e.g., 50) warrants a warning.

5. **Rooted-object generation distribution.** For each root kind, what fraction of rooted objects are in Gen0, Gen1, Gen2, LOH? High Gen2 root counts indicate objects that have survived many collections and are likely genuine leaks.

6. **Duplicate root detection.** A single object can be rooted by multiple roots (e.g., both a static field and a GC handle). Identifying multiply-rooted objects reveals the *most defensively retained* objects.

7. **Root-to-leak-suspect correlation.** Cross-reference `TopRootsBySeverity` target addresses/types against the LeakAnalyzer's top suspects. A type that is both a top leak candidate and strongly rooted is the highest-confidence leak.

8. **`FinalizerQueue` type breakdown.** Currently only count is reported. A per-type breakdown (top 10 types by count in the finalizer queue) would immediately identify the source of finalization pressure.

9. **Trend-based finding.** `GCRootTrendComparer` emits `gcroot.strong.handle.count` as `HigherIsWorse` but `GCRootFindingGenerator` never generates a trend finding. If strong handle count grows across a dump series, a finding should fire.

---

## Audit Area 5 — Performance, Memory & Scalability

### Projection Loop: O(roots × heap.GetObject)

`GCRootAnalysisProjection.Build` calls `heap.GetObject` twice per root (see Area 3). On production dumps with 50K–200K roots this is 100K–400K random memory-mapped reads. Root counts are typically bounded in this range, so absolute time is manageable (~seconds), but the factor-of-2 inefficiency is unnecessary.

### Root Materialization

All root records are loaded into `List<(ulong, ulong, byte)>` in memory. At 17 bytes per record (padded in RootRecord struct), 200K roots ≈ 3.4 MB — not a scalability concern. The pattern is acceptable.

### BFS Cancellation Token Not Propagated

`BoundedGraphWalk.CollectForwardTypeNames` does not accept a `CancellationToken`. For `pathN = 60` (Full profile) × `MaxBfsNodes = 2000`, this is up to 120K object reads. On a 25 GB dump this is meaningful. The method should accept and check cancellation.

### Section Builder LINQ

`GCRootIntelligenceSectionBuilder.Build` uses LINQ (`GroupBy`, `OrderByDescending`, `ThenBy`, `Take`, `Select`, `Concat`, `Where`) throughout. This runs once per report build, not in a heap scan loop, so it is not a hot-path concern. Acceptable.

### `ReadRootCandidates` Cancellation

As noted in Area 3, `CancellationToken.None` is passed to `ReadRootCandidates`. On large root index files the read loop cannot be interrupted. Should be `cancellationToken` from the caller's scope.

### Scalability Assessment (1 GB – 100 GB)

| Dump size | Root count estimate | Impact |
|-----------|---|---|
| 1 GB | ~10K–30K | No issue |
| 10 GB | ~50K–150K | Double GetObject is noticeable but not blocking |
| 25 GB | ~100K–300K | Double GetObject + 60×BFS paths becomes a multi-second operation |
| 100 GB | ~300K–1M | Root materialization memory grows to ~17 MB; BFS path time scales with pathN×BfsNodes |

**Bottleneck:** At 100 GB scale, the BFS path tracing for `pathN = 60` becomes the dominant cost. A pre-filter pass (skip roots below a size threshold) before BFS would materially improve Full-profile runtime.

---

## Audit Area 6 — Correctness & Confidence

### Retained Estimate Is Average Self-Size, Not Retained Size

`EstimateRetainedBytes` computes `agg.TotalSize / agg.Count` — average size of objects of that type. This is used as "EstimatedRetainedBytes" in findings, trend metrics, and the report. The computation is wrong for the stated purpose. A 1 MB `List<Foo>` with 50K items averaging 20 bytes per instance would be estimated at 20 bytes, not 1 MB.

**Risk:** High. The severity score, top-findings sort order, and insight findings all depend on this value. With incorrect estimates, the worst-case root can rank below a minor one.

### Severity Score Can Misrank Large vs. Small Roots

`ComputeSeverity` applies a kind multiplier: `StrongHandle × 3`, `FinalizerQueue × 2`, `PinnedHandle × 2`, `Stack × 1`. A 1 MB StrongHandle gets score `60 × 3 = 180`. A 100 MB stack root gets score `100 × 1 = 100`. The multiplier dominates and inverts the ranking for the most critical cases (large stack-retained objects rank below small static handles). The multiplier intent is sound (static roots are harder to release) but the additive interaction with the base score is poorly calibrated.

### Zero-Estimate Roots Are Silently Dropped

```csharp
ulong estimate = EstimateRetainedBytes(targetAddr, heap, aggregates);
if (estimate == 0)
    continue;  // root is excluded entirely
```

Any root whose target object is not in the aggregate index (e.g., runtime-internal objects, free list entries, corrupted objects) is dropped from all findings. These could be the most interesting roots in crash dumps. At minimum a count of dropped roots should be reported.

### BFS Forward Walk Is Not a Retention Chain

`CollectForwardTypeNames` performs a forward BFS from the root target — it finds objects *reachable from* the root, not the *path from a GC root to the object*. In GC root analysis, the relevant chain is: `GC Root → Holder → ... → Target Object`. The current walk goes in the wrong direction for understanding root-to-target retention structure. This is structurally incorrect for the stated "root path" purpose.

The correct approach would be a reverse traversal from the target back to the GC root, which requires the reverse reference index. Without it, forward BFS from the target is a reasonable approximation of the object graph *owned by* the root, but it should be labelled "owned subgraph types" not "root path."

### `IReadOnlyList<string>` Type Name Duplication

`RootPathFinding.PathTypeNames` is a list of type name strings. When 60 root paths share many of the same types (e.g., all include `System.String`, `System.Object[]`), the same string instances are duplicated across findings in memory. For `pathN = 60` with `MaxBfsNodes = 2000`, this can be several MB of redundant strings. String interning or a shared type-name table would reduce allocation.

### Thread Safety

`RootSetCache._roots` and `_staticRootedAddresses` are assigned without interlocked operations. If the pipeline runs analyzers in parallel (even with `IsThreadSafe = false`, the scheduler could still call into shared cache instances from different analyzers), a data race is possible. The fields should use `Volatile.Write`/`Volatile.Read` or `Interlocked.CompareExchange` assignment.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!gcroot <address>` traces the exact root-to-object chain with full field names at every hop. DumpDetective provides neither: field names are missing (`FieldDescription == null`) and the BFS walk is in the wrong direction for root chains. **Gap: critical.** Engineers pivoting from a DumpDetective report to WinDbg will immediately use `!gcroot` because the DumpDetective path data does not give the same information.

`!dumpheap -thinlock`, `!eeheap -gc` — no equivalent in this analyzer, which is appropriate (separate concerns).

### PerfView

PerfView's heap snapshot analysis shows the *dominator tree* — for each object, the single node that retains it. The dominator tree is far more actionable than a flat root list because it attributes retained size to the unique retaining object, not to every root in the path. **Gap: high value.** DumpDetective has no dominator tree. A dominator analysis on the top-N retained types would be a significant platform advance.

### Visual Studio Memory Usage

Shows object reference chains with exact field names and object addresses. Provides type-level retention summaries. DumpDetective's kind-level summary is comparable, but VS provides field-level navigation which is absent here.

### JetBrains dotMemory

dotMemory provides:
- Exact retained size (not average self-size)
- Full retention paths with field names at each hop
- Dominators grouping
- "Why is alive" for any object

**Gap:** Exact retained size (vs. heuristic average) and field-level path chains are the two capabilities dotMemory provides that are most actionable for production GC root investigations. Both are missing in DumpDetective.

### DumpDetective Advantages

- Kind-level statistical aggregation across all roots at once — WinDbg requires scripting to reproduce this.
- Severity scoring and ranking — no equivalent in WinDbg or VS.
- Trend comparison across dump series — unique to DumpDetective.
- Disk-indexed root set reduces Phase-2 analysis startup cost — no equivalent in WinDbg.
- **Root-owned subgraph visualization** — forward BFS from each root shows the object graph (types and structure) that the root retains. Answers "what does this root own?" independently of "why is it rooted?" — useful for understanding retention shape and designing root-release strategies.

---

## Final Executive Summary

### Overall Assessment

**Score: 52 / 100**

**Production readiness:** Conditionally ready. The analyzer produces useful kind-level statistics and severity rankings that have real triage value. However, the retained-size estimate is fundamentally incorrect for the stated purpose, the root path data is directionally wrong (forward BFS rather than reverse root chain), and field-level context is entirely absent. An engineer using this output must validate all findings in WinDbg before acting.

**Major strengths:**
- Correct use of Phase-1 disk index with live fallback — clean two-path architecture.
- Kind-level aggregation, severity scoring, and trend metrics are genuinely useful.
- `BoundedGraphWalk` and `RootSetCache` are solid shared infrastructure.
- Test coverage for disk-vs-memory discrepancy is appropriate.

**Major weaknesses:**
- Retained-size estimate is average self-size, not retained size — misranks findings.
- BFS path walk is forward (owned subgraph) not reverse (root chain) — misleading label.
- `FieldDescription` is always null — the most actionable piece of root information is absent.
- Finding generator covers only 2 of 9 root kinds.
- Zero-estimate roots are silently dropped with no diagnostic.

---

### Priority Roadmap

| Priority | Recommendation | Impact | Difficulty | Confidence | Status | Classification |
|---|---|---|---|---|---|---|
| **P0-1** | Replace average self-size estimate with `BoundedGraphWalk.ComputeExclusiveRetained` for top-N roots | Critical — severity ranking and insight findings are unreliable without this | Medium | High | ⏳ Pending | Improvement |
| **P0-2** | Relabel BFS walk output as "owned subgraph types" not "root path"; correct or suppress the misleading chain presentation | Critical — output is structurally incorrect for its stated purpose | Low | High | ✅ DONE (e4dd83e) | Improvement |
| **P1-1** | Capture `ClrRoot.RootName` in `RootSetCache.BuildFromLiveHeap` and extend `RootRecord` + disk index format to persist it | High — enables field-level diagnostics; the single biggest usability gap vs. WinDbg/dotMemory | High | High | ⏳ Pending | Improvement |
| **P1-2** | Add `PinnedHandle` finding in `GCRootFindingGenerator` with LOH fragmentation risk | High — pinned handle accumulation is a frequent production issue; completely absent today | Low | High | ✅ DONE (8b6b6b8) | Improvement |
| **P1-3** | Propagate `CancellationToken` through `ReadRootCandidates` call and into `BoundedGraphWalk` | Medium — correctness gap that matters at 25 GB+ scale | Low | High | ✅ DONE (d127297) | Improvement |
| **P1-4** | Merge double `heap.GetObject` per root into one call in `GCRootAnalysisProjection` | Medium — halves heap access count in the projection loop | Low | High | ⏳ Pending | Improvement |
| **P2-1** | Add per-type `FinalizerQueue` breakdown (top 10 types by count) | Medium — immediately identifies source of finalization pressure | Low | High | ⏳ Pending | Improvement |
| **P2-2** | Add generation distribution per root kind (Gen0/1/2/LOH fraction) using `heap.GetGeneration` | Medium — distinguishes transient stack roots from long-lived Gen2 retention | Medium | High | ⏳ Pending | Improvement |
| **P2-3** | Declare `Tags` (e.g., `["gc", "roots", "retention"]`) and `Order` on the analyzer | Low-medium — enables pipeline filtering and deterministic ordering | Low | High | ⏳ Pending | Improvement |
| **P2-4** | Fix `RootSetCache` write-ordering: use `Volatile.Write`/`Interlocked.CompareExchange` for `_roots` assignment | Low-medium — race is unlikely in practice but is a correctness gap | Low | High | ⏳ Pending | Improvement |
| **P2-5** | Add count of dropped zero-estimate roots to `GCRootDomainResult` and surface in the report | Low-medium — makes silent exclusions visible | Low | High | ⏳ Pending | Improvement |
| **P3-1** | Implement reverse BFS from root target back to GC root using `ReverseReferenceIndex` for exact retention chains | Very high if implemented — matches WinDbg `!gcroot` capability | Very High | Medium | ⏳ Future | Evolution |
| **P3-2** | Add dominator-tree analysis for top-N leak suspects cross-referencing `GCRootAnalyzer` and `LeakAnalyzer` | Very high if implemented — matches dotMemory capability | Very High | Medium | ⏳ Future | Evolution |
| **P3-3** | Trend-based finding: fire warning when `gcroot.strong.handle.count` increases across dump series | Medium — leverages existing trend infrastructure | Low | Medium | ⏳ Future | Improvement |

---

## Design Decision: P0-2 Root Path Semantics

### Chosen Approach: Option A (Relabel as "Owned Subgraph Types")

**Rationale:**
- Forward BFS from root's target is genuinely useful for understanding what a root retains (retention shape, graph topology)
- Relabeling honest-fies the semantics without losing diagnostic value
- Low effort; immediate improvement to clarity
- P3 reverse-BFS work becomes complementary enhancement, not a replacement

**Trade-off:** Engineers seeking root-to-target chains must use WinDbg; DumpDetective shows what the root *owns* rather than why the object is *rooted*.

---

### Alternative: Option B (Suppress Until Reverse-BFS Available)

**Description:**
- Remove `Hops` data from reports entirely
- Add caveat: "Full root-to-target chains require reverse-index BFS (P3 evolution)"
- Keep severity table and kind breakdown (both correct and valuable)

**Pros:**
- Honest about current capability — no potentially misleading visualizations
- Stronger signal that P3 reverse-BFS work is needed
- Eliminates any residual confusion about forward vs. reverse semantics
- Simpler mental model: "DumpDetective shows *which roots are severe*, and full chains require WinDbg"

**Cons:**
- Loses the "what does this root own?" diagnostic (retention shape, reachable types)
- Engineers lose a useful visualization for designing root-release strategies
- Requires more context-switching to WinDbg for any retention graph inspection

**Viability:** High. Could be revisited if P3 reverse-BFS implementation is imminent, or if user feedback shows the forward BFS visualization adds more confusion than value in practice. Current choice (Option A) can be reverted to Option B with minimal change if needed (remove Hops from report rendering + update caveats).

---

### Final Verdict

1. **Is the analyzer production-ready?** Partially. Kind-level statistics and the severity table are usable for initial triage. The retained-size values and root path data should not be relied on for precise diagnosis without independent validation.

2. **Highest-impact improvements:**
   - Switch to `ComputeExclusiveRetained` for top-N roots (P0) — fixes the core metric.
   - Capture and persist `ClrRoot.RootName` / field name (P1) — closes the largest diagnostic gap vs. WinDbg.
   - Add `PinnedHandle` finding (P1) — covers a production-critical scenario that is completely dark today.

3. **Platform evolution opportunities:**
   - Reverse-BFS root chain using `ReverseReferenceIndex` would bring DumpDetective to parity with WinDbg `!gcroot` — the highest-value single evolution.
   - Dominator tree cross-referencing with `LeakAnalyzer` would deliver a capability that no free tooling currently offers at dump-analysis speed.

4. **Highest engineering return:**
   - P0 fixes (estimate and relabelling) are low-to-medium effort with immediate correctness improvement.
   - P1 field-name capture requires disk index format evolution but unlocks the entire "why is this object alive" narrative that is currently absent.
   - P1 `PinnedHandle` finding is a one-hour change with outsized value for production memory investigations.
