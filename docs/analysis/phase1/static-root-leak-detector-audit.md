# StaticRootLeakDetector — Phase 1 Audit

> Reviewed against: `phase1-analyzer-architecture-review.md`  
> Analyzer: `StaticRootLeakDetector` (`src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs`)  
> Date: 2026-08-03

---

## Components Reviewed

| Component | File |
|-----------|------|
| Analyzer | `src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs` |
| Options | `src/DumpDetective.Core/Options/StaticRootLeakAnalysisOptions.cs` |
| Domain result / models | `src/DumpDetective.Analysis/Models/StaticRootDomainResult.cs` |
| Section builder | `src/DumpDetective.Reporting/SectionBuilders/StaticRootSectionBuilder.cs` |
| Finding generator | `src/DumpDetective.Reporting/FindingGenerators/StaticRootFindingGenerator.cs` |
| Trend comparer | `src/DumpDetective.Analysis/Trend/Comparers/StaticRootTrendComparer.cs` |
| Traversal primitive | `src/DumpDetective.Analysis/Traversal/BoundedGraphWalk.cs` |
| Traversal primitive | `src/DumpDetective.Analysis/Traversal/RootPathFinder.cs` |
| Cache contract | `src/DumpDetective.Core/Abstractions/IHeapAnalysisCache.cs` |
| Helpers | `src/DumpDetective.Core/Utilities/TypeFilterHelper.cs` |
| Tests | `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/StaticRootLeakDetectorDiscrepancyTests.cs` |

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

The analyzer detects **static-field-rooted memory retention** — a critical class of .NET memory leaks where long-lived static fields directly or transitively prevent object graph teardown. It:

1. Identifies static roots from the shared `IHeapAnalysisCache.GetStaticRootedAddresses` result.
2. Performs a bounded forward BFS (`BoundedGraphWalk.CollectRetainedObjects`) per root to measure the retained object graph.
3. Classifies roots by total retained bytes, object count, presence of collections, and presence of delegate fields.
4. Runs `RootPathFinder` on the top-N roots to produce a human-readable GC root path.
5. Emits `StaticRootDomainResult` with a significance-filtered count and per-root snapshots.

The role is coherent: it is explicitly scoped to static roots, not all GC roots.

### Coverage Assessment

**Well-covered:**
- Retained-size measurement per static root.
- Top-N root surfacing ordered by impact.
- Shallow heuristics: `ContainsCollections`, `ContainsEventHandlers`.
- Root path evidence for the top roots.

**Coverage gaps:**

| Gap | Notes |
|-----|-------|
| No distinction between **static fields on live types vs. already-unloaded types** | A static root from an AssemblyLoadContext that failed to unload is a different severity class than a normal static. |
| No **field-level attribution** | The root description is `"<RootKind> @ 0x<addr>"` — the *field name* holding the reference is never surfaced. This is critical context for actionability. |
| No identification of **static root chains** — static A → static B → large graph | Chains are opaque; only the outermost address appears. |
| **Event handler / delegate** detection is sampled (first 100 objects) and shallow (only checks direct object fields, not traverses multi-cast delegate target lists) | False negatives are common for event-handler leaks in deeply nested graphs. |
| **GenX distribution** of retained objects is absent | Knowing whether retained objects are Gen2/LOH vs. Gen0 changes severity. |
| **Thread-static roots** not distinguished | `[ThreadStatic]` leaks have a different remediation path. |
| Static roots from **plugin / assembly-load contexts** not called out | These represent a distinct, high-impact failure mode. |

### Unexpected Functionality

The analyzer calls `GetOrBuildValidRoots` twice — once at the top of `Analyze` to obtain the root list for path-finding, and once again inside `AnalyzeStaticRoots` to iterate over roots for filtering. The comment `// OPT-#2` notes this but accepts it as a cache hit; it remains a structural smell (the same list is fetched, unpacked, and iterated twice).

### Adjacent Capabilities

- **Weak-reference coverage**: complement static root analysis with `WeakReference` fields that *should* be pruned but are not, indicating GC pressure without permanent retention.
- **Finalizer queue depth correlated to static roots**: a static root that keeps finalizable objects alive without finalizing them is a distinct severity class.
- **AppDomain / AssemblyLoadContext unload prevention**: static roots are the primary blocker; the analyzer is positioned to detect this.

### Architectural Observations

- The `IAnalyzer` interface default-implements `IsThreadSafe`, `Order`, and `Dispose`. `StaticRootLeakDetector` explicitly redeclares `Dispose() { }` at the bottom (as a file-level `NOTE`), which is redundant given the interface default.
- The public `Analyze(ClrHeap, IHeapAnalysisCache)` overload bypasses the `IAnalyzer` contract (it takes no `AnalysisContext` and no `CancellationToken`). It exists for test convenience but leaks the internal surface area.

---

## Audit Area 2 — Diagnostic & Report Quality

### Report Strengths

- The finding generator produces a single, clearly-worded `InsightFinding` with severity, evidence string, and recommendation.
- `EvidenceConfidence.Compute` is applied from the top root's evidence, propagating a confidence score to the finding.
- Trend metrics (`static.root.count`, `static.root.retained.bytes`, per-root named bytes) enable dump-to-dump comparison.
- The section builder correctly differentiates "no roots found" from "roots found" with distinct prose.

### Report Weaknesses

| Weakness | Location | Impact |
|----------|----------|--------|
| Root description is **address-only**: `"Static @ 0x7FF012345"` — no type name of the holding object, no field name | `AnalyzeStaticRoots` / `StaticRootAnalysis.RootDescription` | An engineer cannot act on an address without running a follow-up WinDbg query |
| `StaticRootSnapshot.TypeName` carries the **direct object's type**, not the declaring class of the static field | `StaticRootDomainResult.cs` | The retaining field's owning type is more diagnostic than the target object's type |
| The section builder creates a `CompactTable` with headers `["Root","Type","Retained Bytes","Roots Count"]` but the `rootRows` only populates **two cells** per row (`RootDescription`, `TotalMemoryImpact`) — the `Type` and `Roots Count` columns are always empty | `StaticRootSectionBuilder.cs:43–50` | HTML table renders with two blank columns every row; broken report |
| `TopRetainedTypes` is stored in `StaticRootAnalysis` (the internal model) but **never surfaced** in `StaticRootSnapshot` or the report | `StaticRootDomainResult.cs` vs. `StaticRootAnalysis` | The richest diagnostic data is fully discarded before reporting |
| `ContainsCollections` and `ContainsEventHandlers` are computed but **absent from the report** | `StaticRootSectionBuilder` | An event-handler leak requires a different fix from a cache-growth leak; the distinction is invisible |
| Severity threshold in `StaticRootFindingGenerator`: **10 roots = Critical, anything less = Warning** — a single root retaining 2 GB is `Warning` | `StaticRootFindingGenerator.cs` | Byte-magnitude roots are under-classified |
| `searchTruncated` is stored on `Evidence` but never rendered in the section builder | `Evidence` model vs. `StaticRootSectionBuilder` | Engineers see a path with no indication it may be incomplete |
| No per-root `ObjectsKeptAlive` in the compact table | `StaticRootSectionBuilder` | Object count is captured but hidden |

### Missing Diagnostics

- Root path rendered for only one root per root-analysis snapshot (`TryFindAnyRootPath` returns on first found path). No attempt to find the shortest or most interpretable path.
- No GC generation breakdown for retained objects.
- No indication whether the root is from a finalizable-kept chain.

### Missing Statistics

- Percentage of total heap memory retained by static roots.
- Ratio of static-root retained bytes to total live bytes.
- Per-root growth delta when trend data is available.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage

| Observation | Assessment |
|-------------|------------|
| `heap.GetObject(address)` is called twice per retained object: once in `AnalyzeStaticRoots` to get metadata, and again inside `HasDelegateFields` | Redundant; the `ClrObject` obtained for metadata should be passed through |
| `GetObjectMetadata` allocates a `record struct` on every call — cheap, but the pattern calls it once for the root and once per retained object | Acceptable overhead, though could be inlined |
| `obj.Type.Fields` is iterated to detect delegate fields but `obj.Type` is not cached between calls for the same `MethodTable` | `delegateFieldByMethodTable` caches the bool result per MT but still constructs the `ClrObject` and accesses `Type.Fields` on the first encounter; caching the `ClrType` reference directly would avoid repeated property accesses |
| `HasDelegateFields` checks only **direct instance fields** for delegate type; does not inspect static fields, properties exposing event backing stores, or compiler-generated event fields (`EventHandler<>` pattern) | Under-detection of event-handler leaks |
| `obj.EnumerateReferences(carefully: true)` in `BoundedGraphWalk.CollectRetainedObjects` is correct ClrMD usage | Good |
| `IHeapAnalysisCache.GetStaticRootedAddresses` already encapsulates the root enumeration | Good; avoids a third full-dump root walk |

### Platform Utilization

| Gap | Notes |
|-----|-------|
| `IHeapAnalysisCache.GetOrBuildTypeStatistics` is **not used** | Already has per-type counts and sizes; the local `typeStats` dictionary recomputes the same data from scratch for every static root's retained object set — this is correct for per-root attribution but a type statistics cache could accelerate candidate pre-filtering |
| `IHeapAnalysisCache.MethodTableHasOutgoingRefs` is **not used** | Could prune leaf objects during BFS before enumerating their (zero) references |
| `BoundedGraphWalk.CollectRetainedObjects` does not use the `IHeapAnalysisCache` object index for fast address-to-size resolution | Each `heap.GetObject(addr)` is a live ClrMD call; the disk or memory index could serve size directly |
| `RootPathFinder` is correctly reused from shared traversal infrastructure | Good |
| `RootPathSearchSupport.IsNoisyType` filter is applied during root path search but not during retained-object enumeration | Noise types inflate retained counts |

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Missing Diagnostics

| Diagnostic | Value | Implementation Cost |
|------------|-------|---------------------|
| **Static field name resolution** — use `ClrStaticField.Name` / `ClrType.StaticFields` to resolve the field owning the root address | Critical for actionability — turns `"Static @ 0xABCD"` into `"MyService._cache (System.Collections.Generic.Dictionary)"` | Medium |
| **Declaring type of the static field** | Identifies the leaking class directly | Medium (goes with field resolution) |
| **AssemblyLoadContext / AppDomain attribution** — detect whether the static root belongs to a non-default ALC | High: static roots in non-default ALCs that should have been collected indicate plugin-unload failures | Medium–High |
| **Finalizer queue cross-reference** — are any retained objects finalizable and sitting in the queue? | High: static root → finalizable object → not collected is a critical pattern | Medium |
| **[ThreadStatic] identification** — distinguish thread-static fields from regular static fields | Medium: remediation differs (per-thread cleanup vs. null assignment) | Low–Medium |
| **Top retained namespaces** instead of / alongside top retained types | Helps identify which library owns the leak | Low |
| **Retained LOH objects count and size** | LOH objects in a static-rooted graph are high-pressure | Low |
| **Gen2 percentage of retained objects** | Indicates long-lived retention vs. newly allocated graph | Low |
| **Duplicate root detection** — multiple statics pointing at the same subgraph | Today each root independently computes retained size; shared subgraphs are double-counted | Medium |
| **Cross-root overlap heuristic** — what fraction of retained objects is shared across top-N roots | Contextualizes cumulative impact figure | Medium |
| **Weak reference targets within the retained graph** | Identifies objects that the runtime *could* collect if the strong root were cleared | Medium |

### High-Value Statistics

- `static_roots_as_pct_of_live_heap` — puts the absolute byte count in context.
- Average and max retained size per root.
- Count of roots that triggered BFS cap (`MaxRetainedObjectsToScan` hit) — indicates how many roots have an unknown true retained size.

---

## Audit Area 5 — Performance, Memory & Scalability

### Performance Assessment

| Concern | Evidence | Impact |
|---------|----------|--------|
| **One full BFS per static root** using `BoundedGraphWalk.CollectRetainedObjects` | `AnalyzeStaticRoots` calls `CollectRetainedObjects` for every non-deduplicated static root | On a 10GB dump with hundreds of static roots, each BFS allocates a `HashSet<ulong>` (initial capacity 1000) and a `Queue`; at O(maxObjects) this is controllable but not shared |
| **Retained objects stored as `HashSet<ulong>` in memory** then iterated once | The set is discarded after the type aggregation loop but peak memory per root is `maxObjects * 8` bytes = 80 KB at default limits | Acceptable at default, but `Full` profile raises to 400 KB per root × 40 roots = 16 MB transient; manageable |
| **`heap.GetObject` called for every address in the retained set** (not using the disk index) | `GetObjectMetadata` in the retained-objects loop | On a large heap, each `heap.GetObject` involves a lookup into ClrMD's internal segment list; using the cached index's `EnumerateIndexedEntriesAsTuples` would eliminate this overhead for size/MT resolution |
| **`HasDelegateFields` accesses `obj.Type.Fields`** and iterates fields for uncached method tables | `HasDelegateFields` builds `delegateFieldByMethodTable` cache lazily | On first encounter per type, this traverses the field list; for types with many fields and a high sampling rate this could be measurable |
| **Progress reporting cadence**: every 50 roots | `AnalyzeStaticRoots` | Fine-grained enough for interactive use |
| **`CancellationToken` not checked inside inner BFS loops** | `AnalyzeStaticRoots` checks at entry; no check inside the per-root traversal | On a `Full` profile scan with 50,000 retained objects per root, cancellation can take seconds to respond |
| The `topRoots` LINQ `.OrderByDescending(...).Take(...).Select(...)` chain at the end of `Analyze` is acceptable — it operates on an already-small list | `Analyze` method | Negligible |

### Scalability Assessment

On a 25 GB dump:
- Number of static roots could be in the thousands (large enterprise application with many static caches).
- At `Full` profile: 40 roots × 50,000 BFS nodes = 2M `heap.GetObject` calls just for retained-size measurement, plus the same work again in `BuildSnapshot` via `RootPathFinder`.
- **The dominant bottleneck is ClrMD object-resolution cost per BFS node.** Using a pre-built address→size index would reduce this by ~90%.
- There is no parallelism; roots are processed sequentially.

### Optimization Roadmap

1. **Use cached index for size/MT resolution** inside the retained-object loop — eliminate `heap.GetObject` per address where size/MT suffice.
2. **Thread-safe parallel BFS** across independent static roots (roots are disjoint candidates; their BFS results are independent).
3. **Cancellation check inside BFS inner loop** — call `cancellationToken.ThrowIfCancellationRequested()` every N queue dequeues.
4. **Shared-subgraph deduplication** — a visited `HashSet<ulong>` shared across all roots would prevent re-traversing the same objects.

---

## Audit Area 6 — Correctness & Confidence

### Correctness Risks

| Risk | Location | Severity |
|------|----------|----------|
| **Retained-size double-counting** — multiple static roots pointing at overlapping graphs each count shared objects independently | `AnalyzeStaticRoots` | Medium — `TotalRetainedBytes` in `StaticRootDomainResult` sums all roots' retained bytes, so the aggregate overestimates true unique retained bytes |
| **`BoundedGraphWalk.CollectRetainedObjects` includes the root address itself** in the returned set (`retained.Add(rootAddress)` at initialization) | `BoundedGraphWalk.cs` | Low — the root object's size is included in `TotalMemoryImpact`, which is consistent but worth documenting |
| **BFS cap silently underestimates retained size** — when `MaxRetainedObjectsToScan` is hit, `TotalMemoryImpact` is a lower bound, but no flag is propagated to the caller or the report | `AnalyzeStaticRoots` | Medium — the report presents a potentially heavily truncated number without qualification |
| **`IsSignificant` filter happens after BFS** — all roots are BFS-walked regardless of significance | `Analyze` | Wasted work if a root has negligible retained size that could be detected cheaply before full BFS |
| **Sampling cutoff `SampleRetainedObjectsToInspect`** for `ContainsCollections` and `ContainsEventHandlers` uses `sampledCount` which counts only *valid* objects — invalid objects do not increment the counter, so the effective sample could be larger than the option value | `AnalyzeStaticRoots` | Low — minor semantic drift |
| **`HasDelegateFields` only checks instance fields** — event handler leaks via static event fields on a type are missed | `HasDelegateFields` | Medium — false negative for a common leak pattern |
| **`processedRoots` deduplication by address** is correct but `GetOrBuildValidRoots` may return non-static roots (the static filter is applied afterward by checking `staticRootedAddresses`) — the dedup set is therefore populated with all root addresses, not only static ones, on the first pass | `AnalyzeStaticRoots` — but this is immaterial since `staticRootedAddresses.Contains` gates the body | Negligible |
| **`RootDescription` is truncated to 90 characters twice** — once in `BuildSnapshot` (`FormatHelper.TruncateString`) and the underlying `analysis.RootDescription` is already `"<kind> @ 0x<addr>"`, which is already short | `BuildSnapshot` + `AnalyzeStaticRoots` | Negligible |

### False Positive / False Negative Assessment

- **False positives**: Low. Any root with retained bytes above threshold *is* retaining memory. The question is whether it is a *leak* (unintentional) vs. intentional cache. The analyzer makes no attempt to distinguish these, so "concerning static roots" may include intentional caches. No confidence qualifier is emitted for this ambiguity.
- **False negatives**: Moderate. The `MaxRetainedObjectsToScan` cap means large graphs are underestimated and may fall below `SignificantMemoryThresholdBytes`, causing them to be excluded from the significant-root count but still appearing in the `topRoots` list (since `topRoots` is built from the full `allStaticRootAnalysis` list ordered by impact, before the significance filter).

### Edge Cases Unsupported

- Dumps with zero static roots (e.g., purely native or early-stage process) — handled correctly; returns `RootCount = 0`.
- Dumps with thousands of static roots in a large plugin host — the `MaxRootsToReport` cap truncates to 15 (default) / 40 (Full), which may miss important roots if they don't rank in the top N by retained size.
- Mixed-architecture dumps (x86 process in a 64-bit analyzer) — ClrMD handles this; no specific concern here.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

SOS `!gcroot <address>` provides a complete root path with full field-name attribution, type names at every hop, and GC generation of the rooted object. It is slower (interactive, per-object) but more precise. `StaticRootLeakDetector` covers the discovery phase (finding which statics are significant) but falls well short of SOS on **root path fidelity** — no field names, no hop types, no generation information.

SOS `!dumpheap -stat` combined with `!gcroot` is the canonical workflow. DumpDetective should aspire to eliminate the need to drop into SOS for the common path.

### PerfView

PerfView's GC heap snapshot view shows retention trees with full type and field attribution. It surfaces the retaining field name at every node. This is the most actionable representation available in any .NET diagnostic tool. The absence of **retaining field names** in DumpDetective is the single largest gap against PerfView.

### Visual Studio Memory Usage

VS Memory Usage provides a "referenced by" tree with object type at each level. Limited to live processes, not dumps.

### JetBrains dotMemory

dotMemory's "Dominators" view identifies the object responsible for the most retained memory and traces the dominator tree. It distinguishes accidental retention from expected retention with retention path visualization and "shortest path to GC root" semantics.

**Key competitive gaps vs. dotMemory:**
- Dominator tree analysis is absent (related to `DominatorAnalyzer` if one exists, but not surfaced here).
- Shortest path semantics: dotMemory finds the shortest root path; DumpDetective finds *any* root path.
- Field-level attribution missing in DumpDetective.

### Competitive Opportunities

| Feature | Tool | Priority |
|---------|------|----------|
| Retaining field name at each hop | SOS, PerfView, dotMemory | P0 |
| Dominator tree / exclusive retained size | dotMemory | P1 |
| Shortest path to GC root | dotMemory, PerfView | P1 |
| GC generation of retained objects | SOS, PerfView | P2 |
| AssemblyLoadContext leak detection | PerfView (ALC events) | P1 |
| "Expected vs. unexpected retention" heuristic | dotMemory | P2 |

---

## Final Executive Summary

### Overall Assessment

**Score: 52 / 100**

**Production readiness: Partially — suitable for initial triage, not for deep investigation.**

**Major strengths:**
- Correctly consumes shared cache infrastructure; avoids redundant root walks.
- `BoundedGraphWalk` and `RootPathFinder` are properly reused shared primitives.
- Per-profile options with sensible defaults.
- Trend metrics and finding generation are consistent with the platform pattern.
- Significance filtering prevents noise from trivially small roots.

**Major weaknesses:**
- Root descriptions are address-only — zero actionability without a debugger.
- The section builder renders a broken table (4 headers, 2 populated columns).
- `TopRetainedTypes` is computed but completely discarded before reporting.
- BFS size-cap is silent — truncated retained sizes are presented without qualification.
- Severity classification ignores byte magnitude.
- Field-level attribution entirely absent.

---

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Impact | Difficulty | Confidence | Classification | Status |
|---|---------------|--------|------------|------------|----------------|--------|
| P0-1 | **Fix broken compact table** in `StaticRootSectionBuilder` — populate all 4 columns or reduce to 2 declared headers | Report correctness | Low | High | Improvement | ✅ DONE |
| P0-2 | **Surface `TopRetainedTypes`** in `StaticRootSnapshot` and the section builder — it is already computed | Diagnostic quality | Low | High | Improvement | ✅ DONE |
| P0-3 | **Propagate BFS-cap flag** — when `CollectRetainedObjects` hits `maxObjects`, set a flag on `StaticRootAnalysis` and surface `"(size estimate — scan capped)"` in the report | Correctness/Confidence | Low | High | Improvement | ✅ DONE |
| P0-4 | **Severity by retained bytes**: add Critical threshold (e.g., > 100 MB retained by any single root) independent of root count | Finding quality | Low | High | Improvement | ✅ DONE |

#### P1 — High

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---------------|--------|------------|------------|----------------|
| P1-1 | **Static field name resolution** — enumerate `ClrType.StaticFields`, match `field.ReadObject(…)` address to root address, surface `OwnerType.FieldName` in `RootDescription` | Actionability | Medium | High | Improvement | Pending |
| P1-2 | **Surface `ContainsCollections` / `ContainsEventHandlers`** in snapshot and report | Diagnostic quality | Low | High | Improvement | ✅ DONE |
| P1-3 | **Cancellation inside BFS inner loop** — check token every 256 dequeues | Scalability/UX | Low | High | Improvement | ✅ DONE |
| P1-4 | **AssemblyLoadContext attribution** — check `ClrRuntime.AppDomains` / ALC information for the root's declaring type | High-value diagnostic | Medium | Medium | Improvement | Pending |
| P1-5 | **Use object index for size/MT resolution** inside retained-object loop instead of `heap.GetObject` per address | Performance (25GB+) | Medium | High | Improvement | ✅ DONE — see note below |

**P1-5 note**: shipped differently than originally framed here. Investigating this recommendation
(`docs/cache/19-ObjectAddressLookupIndex.md`) found that `StaticRootLeakDetector`'s specific
retained-object loop didn't actually need a disk index lookup at all — `BoundedGraphWalk.CollectRetainedObjects`
already calls `heap.GetObject` once per node for its own BFS traversal, so capturing `(MethodTable, Size)`
from that existing call and returning it alongside the address (a zero-infrastructure change, done in
that doc's Phase 4) eliminated the redundant second resolution pass entirely — cheaper than building an
index just for this. The general-purpose disk-backed address index this recommendation originally asked
for (`SegmentIndex`/`IHeapAnalysisCache.TryGetObjectMetadata`) was still built, but as a platform
primitive for the ~20 other call sites across the codebase (`RootPathFinder`, `WeakReferenceAnalyzer`,
`GCHandleAnalyzer`, `CrashAnalyzer`, `DominatorAnalyzer`, `LockGraphAnalyzer`, ...) that resolve a known
address without an accompanying live BFS to piggyback on — see that doc's Phases 1–6 and Appendix B for
the full reasoning and the audit of which call sites actually needed it.

#### P2 — Medium

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---------------|--------|------------|------------|----------------|
| P2-1 | **Extend `HasDelegateFields` to check static fields** of the retaining type for event backing stores | Correctness | Low | Medium | Improvement |
| P2-2 | **Gen2/LOH retention percentage** in snapshot | Diagnostic quality | Medium | High | Improvement |
| P2-3 | **Cross-root overlap detection** — shared visited set across top-N roots to report unique retained bytes | Correctness/Quality | Medium | High | Improvement |
| P2-4 | **`static_roots_as_pct_of_live_heap`** metric in key metrics | Context | Low | High | Improvement |
| P2-5 | **Remove public `Analyze(ClrHeap, IHeapAnalysisCache)` overload** — leaks internal surface; tests should use `AnalyzeAsync` with a constructed `AnalysisContext` | Code hygiene | Low | High | Improvement |
| P2-6 | **Remove redundant `void Dispose() { }`** explicit declaration — the interface default already provides it | Code hygiene | Low | High | Improvement |
| P2-7 | **Per-root growth delta** in trend comparer when baseline is available | Investigation workflow | Medium | Medium | Improvement |

#### P3 — Low

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---------------|--------|------------|------------|----------------|
| P3-1 | **[ThreadStatic] field identification** in root description | Diagnostic | Low | Medium | Improvement |
| P3-2 | **Top retained namespaces** alongside top types | Diagnostic quality | Low | High | Improvement |
| P3-3 | **Finalizer queue cross-reference** — flag roots retaining finalizable objects | Diagnostic quality | High | Medium | Evolution |
| P3-4 | **Dominator-tree retained size** (exclusive vs. inclusive) | Deep analysis | High | High | Evolution |
| P3-5 | **Parallel BFS across roots** using `Parallel.For` with a shared cancellation token | Performance | Medium | Medium | Improvement |

---

### Final Verdict

1. **Production-ready for initial triage.** The analyzer reliably identifies static roots with high retained graphs and provides correct significance filtering. It is not production-ready for *actionable investigation* — an engineer hitting a `"Static @ 0x7FF01234"` finding still requires WinDbg or PerfView to determine what the root is and how to fix it.

2. **Highest-impact improvements**: P0-1 (broken table) and P0-2 (discard of `TopRetainedTypes`) are trivially small fixes with high-visibility report impact. P1-1 (field name resolution) closes the largest diagnostic gap against every competing tool.

3. **Platform evolution opportunities**: P3-3 (finalizer queue cross-reference) requires a shared platform primitive (finalizer queue index) that would benefit multiple analyzers. P3-4 (dominator tree) is a full platform capability; if a `DominatorAnalyzer` does not exist, this analyzer's retained-size data is the natural input.

4. **Highest engineering return**: Fix P0-1 through P0-4 (low effort, immediate report quality improvement), then P1-1 (field name resolution — closes the field-attribution gap that makes every other tool more useful), then P1-5 (large-dump size/MT resolution performance — shipped as a free-tuple capture in the shared BFS primitive rather than an index lookup for this analyzer specifically; see the P1-5 note above). These four changes raise the score from 52 to approximately 78.
