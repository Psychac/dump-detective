# ReferenceChainAnalyzer — Phase 1 Audit

**Protocol**: [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)
**Analyzer**: `ReferenceChainAnalyzer` (`src/DumpDetective.Analysis/Analyzers/ReferenceChainAnalyzer.cs`)
**Supporting files reviewed**:
- `ReferenceChainDomainResult.cs`
- `ReferenceChainOptions.cs`
- `ReferenceChainSectionBuilder.cs`
- `ReferenceChainFindingGenerator.cs`
- `ReferenceChainTrendComparer.cs`
- `RootPathFinder.cs` (full — `CandidateSetBuilder`, `ReverseReferenceIndex`, `BidirectionalPathFinder`)
- `ReferenceGraph.cs`
- `ReferenceChainAnalyzerDiscrepancyTests.cs`
- `ReferenceChainAnalyzerBenchmark` results

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role
The analyzer answers one question: *"For the top-N heap types by total size, can I find a GC-root path to a representative instance?"*

It does this by:
1. Pulling type statistics from the shared cache (no heap re-scan)
2. Taking the top-N types by total size
3. Fetching a single cached sample instance per type
4. Running a bounded bidirectional root-path search per sample
5. Emitting per-type retention status: `HasGcRoot`, `RootPath`, `TraversalLimited`

Cohesion is reasonable — the class is focused. However, its scope is narrow relative to the problem it implies.

### Coverage Gaps
- **One sample per type.** If a type has 50,000 instances, one path attempt determines the type's "retained" verdict. A single stale or lucky address biases the result.
- **No root-kind breakdown.** The type-level output records a boolean `HasGcRoot` and a single path string. Whether the root is a static field, thread stack, or GC handle is buried in the path string and not structured.
- **No retained-size analysis.** The analyzer records the sample object's own size but not the size of the subgraph it retains — the core metric for diagnosing memory leaks.
- **No cross-type retention correlation.** Two unrelated types retained by the same static root are never linked.
- **`SampleReferenceChains` (max 5) duplicates `TopTypeSampleTraces`.** The list in the domain result reconstructs chains the trace table already contains. No distinct value is added.
- **`KnownLeakTypePatterns` doubles as both a force-expand hint and a leak signal.** These are different semantics that should be separated.

### Unexpected Functionality
`AnalyzeObject(ClrHeap, IHeapAnalysisCache, ulong)` is an `internal bool` method with no callers in the codebase. It is dead code and creates a secondary entry point with a different cache/root initialization path.

### Adjacent Capabilities
The following capabilities belong naturally in this analyzer or a close sibling:
- Retained-size computation per sample (subgraph BFS)
- Root-kind aggregation across all instances of a type (requires multi-sample)
- "Why is this specific object alive?" on-demand API for consumers
- Detection of types exclusively retained via finalizer queue

### Architectural Observations
- `RootPathFinder` is correctly decoupled from the analyzer and reusable. `EventLeakAnalyzer`, `StaticRootLeakDetector`, `TimerLeakAnalyzer` all independently call `GetOrBuildValidRoots` and perform their own traversal; centralizing around `RootPathFinder` would reduce duplication.
- `MaxPathSearchObjects` and `FastModeMaxDepth` in `ReferenceChainOptions` have no code paths that read them — they are dead configuration surface (see Area 3 below).

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths
- Per-type traces with `HasGcRoot`, `TraversalLimited`, and a formatted root path are a useful starting point.
- `SectionBuilder` correctly parses and formats hop sequences from the path string.
- `FindingGenerator` emits a traversal-limit finding when `>20%` of samples are inconclusive — actionable signal.
- Trend comparer tracks `retained.percent` and `retained.samples` over time.

### Weaknesses

**`TopRetainedTypes` is meaningless in practice.**  
`retainedTypeCounts[typeName]` is incremented exactly once per type (single-sample), so the maximum count is 1. The resulting "top retained types" table is just a list of retained types with count = 1, sorted arbitrarily. The `OrderByDescending(kvp => kvp.Value)` sort has no effect.

**Root path is a raw string, not structured.**  
`FormatPath` produces a `"rootKind: TypeA@0xAddr -> TypeB@0xAddr"` string. The `SectionBuilder` then splits it on `" → "` or `" -> "` to reconstruct hops. This round-trip is fragile — type names containing `" -> "` could cause incorrect splits.

**Root kind is not exposed per type trace.**  
`ReferenceTypeSampleSnapshot` has `HasGcRoot: bool` and `RootPath: string?` but no `RootKind: string?`. Engineers cannot filter by root type without parsing the free-text path.

**Retained percent is over analyzed samples, not total instances.**  
The 70% severity threshold in `FindingGenerator` is applied to `RetainedPercent`, which covers only the sampled types (top-N by size). A heap where 95% of the object count is retained but none are in the top-N by size would produce `0%` with an `Info` severity.

**No aggregate summary.**  
The report lacks: total retained heap size (estimated), root-kind distribution, how many types have unknown retention status.

**`TraversalLimitedSamples` is only a count.**  
Which specific types hit the limit is available in `TopTypeSampleTraces` but not surfaced in the summary metrics or finding text.

### Missing Statistics
- Per-type: root kind (Stack/Static/Handle/Finalizer)
- Per-type: estimated retained subgraph size
- Aggregate: root-kind distribution across retained types
- Aggregate: count of types with no sample address (cache miss)
- Aggregate: count of types where sample was invalid

### Missing Diagnostics
- Identification of types retained only by a finalizer queue (possible leak)
- Identification of types retained by a static variable (likely leak pattern)
- Correlation when multiple types share a root object

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage

**`obj.EnumerateReferences(carefully: true)`** is used in `ReferenceGraph.GetReferences` — correct, `carefully: true` is required for heap consistency under ClrMD.

**`heap.GetObject(address)`** is called in `TryGetValidObject` with a `obj.IsValid` guard — correct.

**Root enumeration** uses the shared cache (`GetOrBuildValidRoots`) which wraps `ClrRuntime.EnumerateGCRefs` / `ClrHeap.EnumerateRoots` — no duplicate enumeration.

### Dead Configuration Surface

`ReferenceChainOptions` exposes:
```csharp
public int MaxPathSearchObjects { get; init; } = 5_000;
public int FastModeMaxDepth { get; init; } = 25;
```

The analyzer code comment explicitly states: *"All modes route through the bounded bidirectional search — Fast mode differs only in its (smaller) resolved candidate-set/depth limits."* Neither `MaxPathSearchObjects` nor `FastModeMaxDepth` is read anywhere in the execution path. They are dead configuration properties. `ReferenceChainSearchMode.Fast` reduces to a preset for `MaxCandidateNodes`, `MaxCandidateDepth`, and `MaxRootExpansionDepth` only.

### `ReferenceGraph` Lifetime — Critical Issue

`TryFindAnyRootPath_Bidirectional` creates a `new ReferenceGraph(heap)` **on every call**:
```csharp
var provider = new ReferenceGraph(heap);
```

`ReferenceGraph` maintains a 500K-node edge cache. Since it is created fresh per type iteration, the cache is discarded after each type's analysis. For top-N = 20 types, 20 independent `ReferenceGraph` instances are created and abandoned. If any type shares referencing objects with adjacent types (common in production heaps), those edges are re-fetched from ClrMD for each iteration.

The `ReferenceGraph` should be created once in `AnalyzeTopTypes` and passed into `TryFindAnyRootPath_Bidirectional`.

### `BidirectionalPathFinder` Instance State Leak

`BidirectionalPathFinder` holds `_visited`, `_previous`, `_queue` as **instance fields** that are cleared at the start of each `TryFindPath`. But the instance is created inside `TryFindAnyRootPath_Bidirectional` (per type), so clearing is correct for the root iteration loop. However, should the `ReferenceGraph` be shared (post-fix), care is needed that these collections do not grow unboundedly across calls — they are pre-sized small (`256` initial capacity) and cleared between calls, so this is acceptable.

### Infrastructure Utilization

| Capability | Used? | Notes |
|---|---|---|
| `GetOrBuildTypeStatistics` | ✅ | Correct — avoids heap re-scan |
| `GetSampleInstanceAddress` | ✅ | Correct — cache-backed |
| `GetOrBuildValidRoots` | ✅ | Correct |
| `MethodTableHasOutgoingRefs` | ❌ | Could skip leaf types in candidate building |
| `GetStaticRootedAddresses` | ❌ | Could accelerate static-root detection |
| `EnumerateIndexedEntriesAsTuples` | ❌ | Could support multi-sample analysis |

### Interface Compliance

`IAnalyzer` default properties `Tags` and `Order` are not overridden. `Category` returns `"Memory"` from the manual property rather than deferring to `AnalyzerCategory.Infer`. `Dispose()` is explicitly implemented as `public void Dispose() { }` — overriding the interface default unnecessarily but harmlessly.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Diagnostics Not Extracted

**1. Root kind per type (High Impact)**  
`rootKind` is available from `finder.TryFindAnyRootPath` but stored only in the path string. Structuring it as an enum field on `ReferenceTypeSampleSnapshot` enables filtering and aggregation.

**2. Retained subgraph size (High Impact)**  
The sample object's own size is recorded (`SampleObjectSize`) but not the total size of all objects reachable from it. Retained size is the primary metric for diagnosing memory pressure and is what `!objsize` in WinDbg/SOS computes. A bounded BFS from the sample object over the existing candidate set could approximate this.

**3. Multi-sample analysis (High Impact)**  
Running 3–5 samples per type and reporting root consistency (e.g., "4/5 samples retained via StaticVar") would dramatically improve confidence versus a single-sample boolean.

**4. "Exclusively finalizer-retained" pattern (Medium Impact)**  
Types where the only root path goes through the finalizer queue are a distinct leak pattern — they imply the finalizer is not running or is blocking. This can be detected by filtering `rootKind == "Finalizer"` with no other root.

**5. Shared root detection (Medium Impact)**  
When multiple top-N types share the same root address, that root is a likely retention hub. This is detectable by grouping found root addresses across type iterations.

**6. Types with no sample (Medium Impact)**  
If `GetSampleInstanceAddress` returns null, the type is skipped silently. Surfacing "N types had no sample available" gives the analyst a coverage gap warning.

**7. Instance count vs retained sample divergence (Low Impact)**  
A type with 1,000,000 instances where only 1 sample was analyzed and retained could have very different population-level retention. Flagging high-instance-count types with single-sample confidence would guide targeted follow-up.

### Evidence Improvements
- Expose root address (not just kind) so analysts can cross-reference with other findings (e.g., `StaticRootLeakDetector`)
- Include generation of the sample object (Gen0/Gen1/Gen2/LOH) — a Gen0 sample is a poor candidate for a retained root path

---

## Audit Area 5 — Performance, Memory & Scalability

### Benchmark Baseline
From `BenchmarkSuite1.ReferenceChainAnalyzerBenchmark-report-github.md`:
```
| AnalyzeReferenceChains | 51.74 s | 7.342 s | 0.402 s | Gen0=1431000 | Gen1=5000 | 5.6 GB |
```
(3 iterations on a production dump; ~17s/run, ~1.9 GB allocation/run)

### Performance Issues

**Issue 1 — New `ReferenceGraph` per type (Critical)**  
As noted in Area 3, `new ReferenceGraph(heap)` is created for every type in the top-N loop. This discards the 500K-node edge cache between iterations and forces ClrMD re-reads for shared objects. On a 25GB heap this could mean millions of redundant `obj.EnumerateReferences()` calls.

Fix: Lift `var provider = new ReferenceGraph(heap)` to `AnalyzeTopTypes` scope and pass it down.

**Issue 2 — Gen0 allocation pressure (High)**  
`Gen0=1,431,000` across 3 runs (~477,000 collections per run). Sources:
- `HashSet<ulong>` allocations for `candidateSet`, `rootVisited`, `targetVisited` in `CandidateSetBuilder`
- `Dictionary<ulong, List<ulong>>` in `ReverseReferenceIndex` (built and discarded per type)
- `Dictionary<ulong, ulong>` `_previous` and `_visited` `HashSet` in `BidirectionalPathFinder` (cleared but not pooled)
- `List<ulong>` in `ReferenceGraph.GetReferences` per unique address

Pooling the `CandidateSetBuilder`'s `HashSet` and the `BidirectionalPathFinder`'s internal collections would reduce Gen0 pressure substantially.

**Issue 3 — `topRetainedTypes` LINQ (Low)**  
```csharp
var topRetainedTypes = retainedTypeCounts
    .OrderByDescending(kvp => kvp.Value)
    .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
    .Take(10)
    .Select(kvp => new NameCountEntry(kvp.Key, kvp.Value))
    .ToArray();
```
This runs after the type loop on a small dictionary. Not a hot path; negligible impact. However the sort is semantically pointless because all counts are 0 or 1.

**Issue 4 — `SortAndFilterRoots` called once but allocates (Negligible)**  
Called once per `AnalyzeTopTypes` invocation — one `List<>` allocation and `O(R log R)` sort on root count R. Acceptable.

### Scalability on 100 GB Dumps

- `GetOrBuildTypeStatistics` performs a full heap scan once and caches — this is the dominant fixed cost and is shared with other analyzers.
- Per-type cost: `O(C × E)` where C = candidate set size (up to 200K), E = average out-degree. With 200K nodes and degree 10, each iteration touches 2M edge fetches.
- For Deep mode with `TopCount=20`: 20 × 2M = 40M edge fetches per run. With a shared `ReferenceGraph`, repeated edges are cached. Without (current state), 40M independent fetches against ClrMD.
- The `MaxCachedNodes = 500_000` cap on `ReferenceGraph` and full-clear eviction strategy means that for very large candidate sets, the cache can thrash within a single type's analysis, let alone across types.

### Progress Reporting

`progress?.Report(new(analyzedSamples, ...))` is called at the start of each type iteration — reporting the count of types analyzed, not actual work completed. For `Deep` mode with 20 types where each type takes minutes, the progress signal is too coarse.

### Cancellation

`cancellationToken.ThrowIfCancellationRequested()` is called once at the start of `AnalyzeAsync`. No cancellation check exists inside the per-type loop or within `RootPathFinder`. On a 100GB dump in Deep mode, cancellation can take minutes to take effect.

---

## Audit Area 6 — Correctness & Confidence

### False Negatives (Type Reported as Unreachable When It Is Retained)

**Single-sample bias.** `GetSampleInstanceAddress` returns a cached sample. If that sample happens to be an object that was collected between index build and analysis, or is in Gen0 and was not retained, the type is marked `HasGcRoot=false` when most instances of the type are in fact retained.

**Candidate set miss.** If the true path from root to target object is longer than `MaxCandidateDepth * 2`, the target and root frontiers may not overlap and the candidate set will not contain the full path. `searchTruncated` is correctly set in this case, but the finding reads the same as a genuinely unreachable object unless the caller explicitly checks `TraversalLimited`.

**Forward-only candidate building from target.** `CandidateSetBuilder` expands *forward* from the target (following outgoing references), not backward (incoming). This is described in a comment as "simulating reverse via the heap walk" — but forward refs of the target are the objects *it* points to, which are not on the retention path. The correct simulation of backward expansion requires a full heap scan to find objects that reference the target, which `CandidateSetBuilder` does not do. The approach works when the target is referenced by a parent in the root frontier, but the "target frontier" expansion adds noise rather than true reverse candidates.

### False Positives (Type Reported as Retained When It Is Not)

Low risk. The path found must pass through the verified GC root list. The root list is correctly filtered (Weak/Dependent excluded). `obj.IsValid` is checked throughout.

### Edge Cases

**`address == 0` guard.** Both `TryGetValidObject` and `SortAndFilterRoots` guard against zero addresses — correct.

**Root kind string matching.** `SortAndFilterRoots` filters using:
```csharp
rootKind.Contains("Weak", StringComparison.OrdinalIgnoreCase)
rootKind.Contains("Dependent", StringComparison.OrdinalIgnoreCase)
```
This is substring matching on free-text root kind names from ClrMD. If ClrMD changes root kind naming conventions, this silently stops filtering. A safer approach would use `ClrRootKind` enum values.

**`IsNoisyType` for `System.String`.** `System.String` is excluded from traversal but string objects can legitimately be intermediate retainers in some retention chains (e.g., a cached string dictionary). This pruning is a correctness trade-off — it reduces traversal time but may break paths where string instances are actual intermediate holders.

### Confidence Assessment

| Finding | Confidence | Risk |
|---|---|---|
| Type has GC root (path found) | High — path is verified BFS | Low |
| Type has no GC root (path not found, not truncated) | Medium — depends on candidate set coverage | Medium (false negative if path was pruned) |
| Type has no GC root (search truncated) | Low — inconclusive | High |

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS
`!gcroot <address>` performs an exhaustive backward walk from all GC roots to find every path to the target object. Results include: root kind, source field name, thread ID for stack roots, full chain depth. DumpDetective's analyzer is broader (top-N types automatically) but shallower (one path, no field names, one sample).

**Gap**: Field names (static field name, delegate event owner) are absent from DumpDetective's output. SOS provides these natively via `ClrStaticField.Name` and `ClrInstanceField.Name`, which are accessible via ClrMD.

### PerfView
Provides heap snapshots with *retention trees* — each type shows the aggregated set of objects that retain it, with retained byte size. The "Referred-From" view is equivalent to a multi-sample, aggregated version of what ReferenceChainAnalyzer attempts. DumpDetective produces no equivalent.

**Gap**: Retention trees require repeated multi-type reverse analysis, which would be expensive. However, a lightweight approximation (root-kind frequency across a sample of instances) could be added with multi-sample support.

### Visual Studio Memory Usage
Shows "Object type" browser with instance count, size, and reference chains. Supports "who references this object" interactive exploration. No automatic top-N analysis equivalent.

**DumpDetective advantage**: Automated top-N analysis on dumps without requiring Visual Studio or a live process.

### JetBrains dotMemory
Provides *dominators tree*, *shortest path to GC root*, and *key retention paths*. The dominators tree shows what would be freed if a given type were removed — directly related to retained subgraph size. DumpDetective has a `DominatorAnalyzer` but `ReferenceChainAnalyzer` does not query its results.

**Gap**: `ReferenceChainAnalyzer` and `DominatorAnalyzer` are not correlated. Enriching type traces with dominator data (e.g., "this type is a dominator of 12% of heap") would significantly increase diagnostic value.

### Competitive Summary

| Capability | WinDbg/SOS | PerfView | VS | dotMemory | DumpDetective |
|---|---|---|---|---|---|
| Auto top-N retention | ❌ | ❌ | ❌ | ❌ | ✅ |
| Field name in root path | ✅ | ✅ | ✅ | ✅ | ❌ |
| Retained subgraph size | ✅ | ✅ | ✅ | ✅ | ❌ |
| Root kind structured | ✅ | ✅ | ✅ | ✅ | ❌ |
| Multi-sample confidence | N/A | ✅ | ✅ | ✅ | ❌ |
| Dominator correlation | ❌ | ✅ | ✅ | ✅ | ❌ (gap) |
| Automated findings | ❌ | ❌ | ❌ | ❌ | ✅ |
| Dump-file only (no live process) | ✅ | ✅ | ❌ | ❌ | ✅ |

---

## Recommendation Classification

### Improvements (Enhance the Existing Analyzer)

| ID | Recommendation | Impact | Difficulty | Confidence | Priority | Status |
|---|---|---|---|---|---|---|
| I-1 | Lift `ReferenceGraph` creation out of the per-type loop — share one instance across all top-N iterations | High — reduces redundant ClrMD edge fetches; addresses 5.6GB Gen0 allocation root cause | Low | High | P0 | ✅ DONE (ec42b06) |
| I-2 | Remove dead options `MaxPathSearchObjects` and `FastModeMaxDepth` from `ReferenceChainOptions` | Medium — eliminates misleading configuration surface | Low | High | P1 | — |
| I-3 | Add `RootKind: string?` to `ReferenceTypeSampleSnapshot`; populate from `TryFindAnyRootPath` return | High — enables root-kind filtering and aggregation in reports | Low | High | P1 | ✅ DONE (c05c6f4) |
| I-4 | Fix `TopRetainedTypes` semantics — counts are always 0/1 (single sample); either remove the field or change it to a list of retained type names | Medium — removes misleading metric | Low | High | P1 | — |
| I-5 | Replace path-string round-trip (`FormatPath` → `SectionBuilder` split) with structured `IReadOnlyList<string> PathHops` on domain result | Medium — eliminates fragile string parsing | Low | High | P1 | — |
| I-6 | Add cancellation checks inside the per-type loop and inside `RootPathFinder`'s root iteration | Medium — required for large dumps | Low | High | P1 | — |
| I-7 | Pool `BidirectionalPathFinder` internal collections (`_visited`, `_previous`, `_queue`) using `ArrayPool`/`ObjectPool` | Medium — reduces Gen0 pressure | Medium | Medium | P2 | — |
| I-8 | Surface `RootKind` aggregate distribution in section builder (e.g., "3 of 5 retained types: StaticVar") | Medium — improves report actionability | Low | High | P2 | — |
| I-9 | Add generation check before using a sample address — prefer Gen2/LOH samples over Gen0 | Medium — improves single-sample confidence | Low | High | P2 | — |
| I-10 | Expose count of types with no sample address as a metric | Low — transparency improvement | Low | High | P3 | — |
| I-11 | Remove dead `AnalyzeObject(ClrHeap, IHeapAnalysisCache, ulong)` method or promote to a named public API | Low — dead code cleanup | Low | High | P3 | — |
| I-12 | Replace root-kind string `Contains("Weak")` filter with `ClrRootKind` enum comparison | Low — defensive correctness | Low | Medium | P3 | — |

### Evolutions (Improve the Platform)

| ID | Recommendation | Impact | Difficulty | Confidence | Priority | Status |
|---|---|---|---|---|---|---|
| E-1 | Add multi-sample analysis: sample 3–5 instances per type, report root consistency score | High — transforms single-sample boolean into confidence metric | Medium | High | P1 | — |
| E-2 | Compute approximate retained subgraph size per sample via bounded BFS over the candidate set | High — aligns with dotMemory/PerfView parity; closes the biggest competitive gap | Medium | High | P1 | — |
| E-3 | Enrich path hops with field names from `ClrStaticField.Name` / `ClrInstanceField.Name` for the first and last hop | High — closes the primary WinDbg/SOS parity gap | Medium | High | P1 | — |
| E-4 | Correlate `ReferenceChainAnalyzer` output with `DominatorAnalyzer` results — flag types that are dominators of a significant heap fraction | High — closes the dotMemory parity gap | Medium | Medium | P2 | — |
| E-5 | Detect "exclusively finalizer-retained" pattern as a distinct finding | Medium — specific leak pattern not detectable without this | Low | High | P2 | — |
| E-6 | Detect shared root objects across top-N types — flag root addresses that appear in multiple type traces | Medium — identifies retention hubs | Low | High | P2 | — |
| E-7 | Add `IHeapAnalysisCache.GetTopInstanceAddresses(string typeName, int count)` to support multi-sample analysis (E-1) | Medium — infrastructure dependency for E-1 | Medium | High | P2 | — |

---

## Final Executive Summary

### Overall Assessment

**Score: 52 / 100**

**Production readiness**: Conditional. Produces useful retention signal quickly and correctly identifies retained top-N types in the common case. Not production-grade for investigations requiring confidence, root identification, or retained-size context.

**Major strengths**:
- Correct bounded bidirectional search — no full-heap reverse index, no O(N²) BFS
- Cache-backed type stats and sample addresses — no duplicate heap scan
- `RootPathFinder` is well-decoupled and reusable
- Progress reporting and `TraversalLimited` flag give observability into search limits
- Finding generator surfaces traversal-limit as a distinct signal

**Major weaknesses**:
- `ReferenceGraph` recreated per type — largest fixable performance defect (I-1)
- Single sample per type — single boolean confidence is insufficient for production diagnosis
- Root kind is not structured — engineers must parse free-text to determine retention category
- Retained subgraph size is absent — the primary diagnostic metric for memory leaks
- `TopRetainedTypes` counter is always 0 or 1 — a broken metric
- Field names absent from root paths — the primary gap vs. WinDbg SOS

### Priority Roadmap

| Priority | ID | Description | Expected Impact | Difficulty |
|---|---|---|---|---|
| **P0** | I-1 | Share `ReferenceGraph` across per-type loop | Significant allocation reduction; 30–50% runtime improvement estimated | Low |
| **P1** | I-3 | Structured `RootKind` on `ReferenceTypeSampleSnapshot` | Enables root filtering, aggregate reporting | Low |
| **P1** | I-4 | Fix `TopRetainedTypes` semantics | Removes misleading metric | Low |
| **P1** | I-5 | Structured path hops in domain result | Eliminates fragile string parsing | Low |
| **P1** | I-6 | Cancellation inside per-type loop | Required for large dumps | Low |
| **P1** | I-2 | Remove dead `MaxPathSearchObjects` / `FastModeMaxDepth` | Eliminates misleading configuration | Low |
| **P1** | E-1 | Multi-sample analysis (3–5 per type) | Transforms boolean to confidence score | Medium |
| **P1** | E-2 | Retained subgraph size per sample | Closes largest competitive gap | Medium |
| **P1** | E-3 | Field names in root path hops | Closes primary WinDbg/SOS parity gap | Medium |
| **P2** | I-7 | Pool `BidirectionalPathFinder` collections | Reduces Gen0 pressure | Medium |
| **P2** | I-8 | Root kind aggregate in section builder | Improves report actionability | Low |
| **P2** | I-9 | Prefer Gen2/LOH sample addresses | Improves single-sample confidence | Low |
| **P2** | E-4 | DominatorAnalyzer correlation | Closes dotMemory parity gap | Medium |
| **P2** | E-5 | Finalizer-only retention pattern | New specific finding | Low |
| **P2** | E-6 | Shared root detection across types | Identifies retention hubs | Low |
| **P3** | I-10 | Surface no-sample count as metric | Transparency | Low |
| **P3** | I-11 | Remove dead `AnalyzeObject` method | Dead code cleanup | Low |
| **P3** | I-12 | Use `ClrRootKind` enum for root filtering | Defensive correctness | Low |

### Final Verdict

1. **Is the analyzer production-ready?** For a first-pass triage signal — yes. For confident retention diagnosis — no. The single-sample approach, absent retained sizes, and unstructured root kinds limit its utility on real incident investigations.

2. **Highest-impact improvements?** I-1 (share `ReferenceGraph`) is the highest-return-for-lowest-effort fix. E-2 (retained subgraph size) and E-3 (field names in path) are the highest-return evolutions.

3. **Platform evolution opportunities?** `E-7` (`GetTopInstanceAddresses` on cache) would enable multi-sample analysis across any analyzer. The `RootPathFinder` reusability is already an asset — formalizing it as a shared platform primitive (with a public API) would allow `EventLeakAnalyzer`, `TimerLeakAnalyzer`, and `StaticRootLeakDetector` to unify their traversal logic.

4. **Highest engineering return?** In order: I-1, I-3 + I-5, E-2, E-3. The first two are low-effort fixes that immediately improve correctness of outputs. E-2 and E-3 require moderate effort but close the most significant gaps relative to production diagnostics tools.

