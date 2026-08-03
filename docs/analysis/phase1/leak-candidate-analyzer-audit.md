# LeakCandidateAnalyzer — Audit Report

> Protocol: `phase1-analyzer-architecture-review.md`
> Analyzer: `LeakCandidateAnalyzer` (`src/DumpDetective.Analysis/Analyzers/LeakCandidateAnalyzer.cs`)
> Supporting components: `LeakCandidateDomainResult`, `LeakCandidateRecord`, `LeakClass`,
> `LeakAnalysisSectionBuilder`, `LeakCandidateFindingGenerator`, `LeakCandidateTrendComparer`,
> `LeakCandidateAnalyzerDiscrepancyTests`, `LeakCandidateFindingGeneratorTests`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`LeakCandidateAnalyzer` is a **heuristic triage layer**: it consumes the pre-built
`TypeAggregates` index and the `StatisticsCache` to rank every type in the heap by
"suspicion of being leaked". It classifies candidates into seven `LeakClass` buckets
(`StaticRetention`, `EventLeak`, `CacheLeak`, `ThreadLocalLeak`, `FinalizerRetention`,
`GCHandleRetention`, `DependentHandleLeak`, `Unknown`) and scores each with a simple
additive formula. The top 30 are returned for display.

The analyzer runs as an `IDeferredAnalyzer`, deliberately after all other analyzers,
which allows it to consume the completed `GCHandleDomainResult` without creating an
ordering dependency. This is architecturally correct.

### Coverage Assessment

| Concern | Covered | Notes |
|---|---|---|
| Gen2 / LOH dominance | ✅ | `gen2Pct`, `LohCount` via aggregate |
| Finalizable types | ✅ | `TypeAggregateFlags.IsFinalizableType` |
| Static root retention | ✅ | `GetStaticRootedAddresses` |
| GC handle retention | ✅ | Piggybacked from `GCHandleDomainResult` |
| Dependent handle retention | ✅ | Piggybacked from `GCHandleDomainResult` |
| Event / delegate types | ✅ | `IsDelegateType` flag + name heuristic |
| Container / cache types | ✅ | Name substring heuristic |
| ThreadLocal retention | ✅ | Name substring heuristic |
| Deep retention graph | ❌ | No retained-size estimation; shallow size only |
| Per-candidate root confirmation | ❌ | No BFS; `sampleAddress` check is a proxy |
| Growth trend within a dump | ❌ | Not applicable (single-snapshot) |
| Duplication across analyzers | ⚠️ | EventLeak and ThreadLocal overlap with dedicated analyzers |

### Gaps & Expansion Opportunities

1. **Shallow-only sizing.** `TotalSize` is always shallow. A type with 1,000 instances
   each holding 10 MB of live strings scores identically to one holding 10 MB of ints.
   The `MemoryAnalyzer` estimates retained size via `EstimateRetained`; this analyzer
   should expose the same value for top suspects.

2. **Classification is single-label only.** A type may be *both* statically rooted
   *and* pinned. The first matching rule wins, discarding all secondary signals.
   `LeakClass` should be a `[Flags]` enum or the record should carry a
   `ClassificationSet`.

3. **`Unknown` class is a semantic void.** The `Unknown` bucket absorbs every type
   that fails all seven checks — including high-Gen2, high-size types that are genuine
   suspects. Nothing in the report distinguishes "Unknown with score 60" from "Unknown
   with score 5".

4. **Top-30 hard cap discards signal.** Only the top 30 are stored in
   `TopCandidates`; `TotalCandidates` reports the wider pool but the section builder
   and finding generator cannot act on items 31+. Classes like `CacheLeak` with many
   moderate-score entries are silently truncated.

5. **No intra-dump growth detection.** Because the index stores only aggregate
   counts, an analyzer that also consults the `Gen0Count` / `Gen1Count` split could
   flag types where nearly all instances are Gen0 (likely normal churn) vs. a type
   with 95 % Gen2 instances (strong retention signal). Gen0/Gen1 counts are present in
   `TypeAggregateIndexEntry` but not surfaced on `LeakCandidateRecord`.

6. **Adjacent analyzers that could feed data back.** `EventLeakAnalyzer` builds
   confirmed retention evidence. If its results were piggybacked (symmetrically to how
   `GCHandleDomainResult` is used), the `EventLeak` classification could be upgraded
   from a name heuristic to a confirmed classification.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- **`LeakCandidateCard`** — the per-candidate explanatory card in
  `LeakAnalysisSectionBuilder` provides human-readable text (`LeakExplainer.Explain`),
  an impact band, GC pressure notes, and an LOH risk note. This is notably better than
  a raw table.
- **Score factors are documented in the report** — the builder renders the exact
  scoring rules as a paragraph, so analysts can cross-check manually.
- **Dominant-class recommendation** — `LeakCandidateFindingGenerator` collapses the
  top-5 candidates into a single finding with the most frequent classification driving
  the recommendation text. Reduces noise in the `InsightFinding` stream.
- **Confidence band** — `ConfidenceScoring.Compute` correctly marks all output as
  heuristic-only (0.75 − 0.15 = 0.60 base), setting correct expectations.

### Weaknesses

1. **No root-path evidence.** Every card ends with "Investigate root paths in §A5". The
   analyzer never shows even the first hop of the GC root chain. The report tells the
   analyst *what* to look for but not *where*.

2. **`RootKind` is derived entirely from classification, not from ClrMD.** The mapping
   in `Analyze()` converts `LeakClass → string`. If the classification is wrong the
   root kind string is wrong. Engineers expecting `RootKind` to reflect a real
   `ClrRoot.RootKind` will be misled.

3. **Single aggregate finding.** `LeakCandidateFindingGenerator` always emits exactly
   one `InsightFinding` regardless of how many distinct high-severity candidates exist.
   A dump with three unrelated Critical leaks merges them into one finding, hiding
   severity diversity.

4. **`Gen2Pct` is truncated to 32 bits silently.** `aggregate.Gen2Count` is `int`
   (from `TypeAggregateIndexEntry.Gen2Count`), but `aggregate.Count` is `long`.
   The ratio `aggregate.Gen2Count * 100.0 / aggregate.Count` is correct numerically,
   but the Gen2 count itself is capped at `int.MaxValue` (~2B) — with very large heaps
   this overflows silently.

5. **`HeuristicOnly = true` is hardcoded on every result.** The flag exists to signal
   that no root traversal was performed, but the system never sets it to `false`. The
   confidence machinery therefore always applies the same −0.15 penalty even if (in a
   future revision) root traversal is added.

6. **`Unknown` class produces generic recommendations.** "Inspect root paths and
   retention owners" is valid advice but provides no starting point. High-score Unknown
   candidates deserve at least a discriminator: high-Gen2, high-size, high-Ref-ratio.

7. **LOH detection in `IsLargeObjectLike` is wrong.** It checks for `[]` or `string`
   in the type name; the actual LOH threshold is 85,000 bytes per *object*. A type
   named `MyHugeConfig` with 200 KB instances never triggers the LOH note.

8. **Missing: per-class total-size summary in the InsightFinding.** The finding
   reports combined shallow bytes across 5 candidates but not the per-class breakdown.
   This makes it impossible to prioritize remediation work from the executive summary
   alone.

---

## Audit Area 3 — ClrMD & Platform Utilization

### What Is Used Well

| Asset | Usage |
|---|---|
| `TypeAggregateIndexEntry` (Gen0/Gen1/Gen2 counts, TotalSize, LohCount, Flags) | ✅ Core of scoring |
| `TypeShapeEntry` (RefFields / ValFields) | ✅ `referenceFieldRatio` |
| `HeapAnalysisCache.GetStaticRootedAddresses` | ✅ Correct, cached |
| `HeapAnalysisCache.GetSampleInstanceAddress` | ✅ Correct, cached |
| `GCHandleDomainResult` piggyback | ✅ Avoids second handle scan |
| `IDeferredAnalyzer` ordering | ✅ Correct deferred pattern |

### What Is Suboptimal

1. **`GetSampleInstanceAddress` then `heap.GetObject()` to resolve `MethodTable`.**
   The `MethodTable` is already stored in `TypeAggregateIndexEntry.MethodTable`.
   The current flow fetches a sample address from the statistics cache, creates a
   `ClrObject`, and then checks `sample.Type.MethodTable` — only to look up the same
   `TypeAggregateIndexEntry` that already carries the value. This is unnecessary and
   risks a null-dereference if the `ClrObject` is invalid (the `IsValid` guard is
   present, but the approach itself is wasteful).

   Mitigation: join `typeStats` ↔ `aggregates` by `MethodTable` directly without
   going through a live `heap.GetObject()` call.

2. **Container detection uses string substring matching.** `typeName.Contains("Cache")`,
   `.Contains("Dictionary")`, etc. are fragile. `TypeAggregateFlags` already has an
   `IsArrayType` bit; adding `IsDictionaryLike` and `IsCacheLike` flags (settable during
   Phase 1 by checking the type implements `IDictionary` or carries "Cache" in its name)
   would centralise the heuristic and make it testable in isolation.

3. **`staticRoots.Contains(sampleAddress)` checks a single sample instance.** If the
   sample is not itself a statically rooted instance (but other instances of the same
   type are), the type is misclassified as `Unknown`. The correct check is
   "does any instance of this type appear in the static roots set", which requires
   iterating all instances of the type — not feasible inline. An alternative is to
   carry a `IsStaticRooted` bit on `TypeAggregateIndexEntry`, set during Phase 1 when
   a statically rooted address is encountered for that `MethodTable`.

4. **`LohCount` and `LohSize` from `TypeAggregateIndexEntry` are unused.** These are
   ideal signals for LOH-related scoring and the LOH fragmentation note in
   `LeakAnalysisSectionBuilder`, but neither is read by the analyzer or propagated to
   `LeakCandidateRecord`.

5. **`TypeAggregateIndexEntry.SampleAddress` is ignored.** The index already stores one
   sample address per type. `GetSampleInstanceAddress` is called anyway, adding a
   dictionary lookup through the statistics cache. Using `aggregate.SampleAddress`
   directly would be faster and more consistent.

6. **Event piggyback is type-name only, not address-based.** `pinnedTargetTypes` and
   `dependentTargetTypes` are sets of *type names* sourced from
   `GCHandleDomainResult.TopPinnedTargetTypes`. Any type whose name appears there is
   classified as pinned — but TopPinnedTargetTypes is a truncated list (usually top-N).
   Types not in the top-N are silently misclassified. A full `HashSet<ulong>` of
   pinned addresses stored on `GCHandleDomainResult` would give exact per-instance
   rather than per-name coverage.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

The following diagnostics exist in the dump but are not currently extracted.

### High Value

| Opportunity | Evidence Source | Expected Impact |
|---|---|---|
| **Retained size estimate per candidate** | `MemoryAnalyzer.EstimateRetained` already exists | Distinguishes 100K shallow objects with 0 retained from 100 objects with 10 GB retained |
| **First GC root hop** (field name + owner type) | `heap.EnumerateRoots()` + `ClrObject.EnumerateReferences()` for sample instance | Turns "investigate root paths" into an actionable starting point |
| **Gen0/Gen1/Gen2 distribution on candidate card** | Already in `TypeAggregateIndexEntry` | Shows whether growth is recent (Gen0 dominant) or entrenched (Gen2 dominant) |
| **LOH instance count and size per candidate** | `LohCount` / `LohSize` from index | Direct LOH fragmentation risk signal |
| **Instance size histogram** | `GlobalSizeBuckets` on `HeapIndexBuildResult` | Shows whether the type is uniformly large or has a bimodal distribution |
| **Type growth across trend snapshots** | `LeakCandidateTrendComparer` already tracks bytes; type-level delta per snapshot | Correlates leak rate with deployment time |

### Medium Value

| Opportunity | Evidence Source | Expected Impact |
|---|---|---|
| **Multi-label classification** | Extend `LeakClass` to flags | Surfaces "StaticRetention AND GCHandlePinned" combos |
| **Module attribution per candidate** | `TypeAggregateIndexEntry.ModuleId` + module name table | Identifies which assembly owns the leaking type |
| **Finalizer queue depth** | ClrMD `runtime.Heap.EnumerateFinalizableObjects()` | Distinguishes "finalizable type" from "currently queued for finalization" |
| **Container fill ratio** | Requires per-instance inspection; scoped to top suspects | Distinguishes a Dictionary at 2 % capacity from one at 200 % |
| **Cycle / self-reference detection** | Reference graph on top suspects only | Identifies object graphs that cannot be freed regardless of root |

### Lower Value

| Opportunity | Impact |
|---|---|
| Caller-stack correlation (if ETW events present) | Identifies allocation site for candidates |
| Interface implementation list | Richer type classification than name substrings |
| Thread-affinity detection | Complements ThreadLocal classification |

---

## Audit Area 5 — Performance, Memory & Scalability

### Current Behavior

The core loop iterates `typeStats` (one entry per unique type name) — **O(T)** where T
is the number of distinct types. On a 10 GB dump T rarely exceeds 50,000–100,000. The
work per entry is:
- one dictionary lookup in `aggregates` (O(1))
- one dictionary lookup in `shapes` (O(1))
- one `staticRoots.Contains(sampleAddress)` (O(1))
- two string-set lookups (O(1))
- five string-substring checks (linear in type name length; bounded)

This is fast and scales well to 100 GB dumps. The analyzer itself is not a
performance concern at its current scope.

### Performance Issues

1. **`heap.GetObject(sampleAddress)` is called inside the hot loop.** `ClrObject`
   resolution via `ClrHeap.GetObject` can trigger segment map lookups in ClrMD. On a
   live heap with many segments this is non-trivial. It is called **once per unique
   type**, not once per object, so the absolute cost is manageable today (T ≤ 100K),
   but it is unnecessary — see Area 3 point 1.

2. **`candidates.Sort()` on the full list before slicing to top-30.** With T = 100K
   candidates this sort is O(T log T). A `PriorityQueue<LeakCandidateRecord, int>`
   with capacity 30 would produce the top 30 in O(T log 30) ≈ O(T). For 100K entries
   the difference is roughly 17× fewer comparisons.

3. **`GetStaticRootedAddresses` iterates `roots` and builds a new `HashSet<ulong>` on
   first call.** This is lazy and cached; subsequent calls are O(1). No issue.

4. **`GetOrBuildTypeStatistics` may fall back to a full parallel heap walk** if the
   heap index is unavailable. When used as a fallback it allocates per-segment
   dictionaries merged under a lock. This fallback path is visible in the source and is
   expensive on large heaps, but the cause is upstream (index build failure), not
   `LeakCandidateAnalyzer` itself.

5. **Cancellation is only checked once at entry to the loop body.** For a type set
   of 100K entries this is adequate, but the cancellation check fires on *every
   iteration*, meaning 100K `ThrowIfCancellationRequested` calls that always succeed on
   a responsive token. This is a minor overhead; consider checking every 1,000
   iterations if profiling identifies the loop as a hot spot.

### Scalability Assessment

The analyzer is **O(T)** in both time and the `candidates` list allocation. T grows
slowly with dump size (bounded by distinct types in loaded assemblies). 100 GB dumps do
not materially increase T. The primary memory cost is `candidates` list capped at T
entries; even at 100K × ~200 bytes per `LeakCandidateRecord` this is ~20 MB — acceptable.

---

## Audit Area 6 — Correctness & Confidence

### Classification Correctness Risks

| Risk | Severity | Notes |
|---|---|---|
| **`staticRoots.Contains(sampleAddress)` misclassifies if the sample is not rooted** | Medium | The sample may be a Gen0 object. Most instances could be Gen2-static while the cached sample is a fresh Gen0 allocation |
| **`pinnedTargetTypes` is a top-N truncated list** | Medium | Types beyond top-N in `GCHandleDomainResult` are silently mis-classified |
| **String-substring container detection produces false positives** | Low-Medium | `MyCustomQueryCache` matches "Cache"; `ConcurrentQueueProcessor` matches "Queue" |
| **`typeName.Contains("Event")` matches non-event types** | Low-Medium | `EventArgs` in `System.EventArgs`, `LogEventLevel`, `RequestEventId` are not leaks |
| **Single-label classification hides compound retention** | Medium | A type both statically rooted and pinned only reports one label |
| **`gen2Pct` denominator is `aggregate.Count` (long) but numerator is `Gen2Count` (int)** | Low | Silently saturates at `int.MaxValue`; for large heaps (billions of instances per type) this underreports Gen2 percentage |
| **Score ceiling of 110 never triggers Critical severity (≥90)** | Low | Score can be at most 110 (all flags), and Critical requires ≥90. But most types cannot achieve ≥90 without being very large and Gen2-heavy simultaneously |

### False Positive Vectors

- Infrastructure types (`System.Threading.Tasks.Task`, `System.String`) are consistently
  Gen2-heavy across all .NET applications. They will often appear in the top-30 with
  high Gen2Pct scores despite not being leaks. There is no filtering for well-known
  system types.

- Framework delegate types (`System.EventHandler`, `System.Action<T>`) satisfy the
  `IsDelegateType` flag and receive +5, but event handler allocation churn is normal.
  The analyzer has no "expected baseline" concept.

### False Negative Vectors

- A type allocated entirely in the LOH with zero Gen2 objects (LOH bypass of
  generational tracking in some runtime versions) would score low on `gen2Pct` despite
  being a genuine memory leak candidate.

- A type that holds small instances (< 85K) but collectively occupies 10 GB would only
  score +20 from the size signal if `TotalSize > 100 MB`. There is no bonus for
  "moderate-size type with extreme instance count" (e.g., 50M instances of a 200-byte
  object).

### Confidence Assessment

The `HeuristicOnly = true` flag and the 0.60 effective confidence score in the report
are honest. The analyzer is a well-calibrated triage tool, not a root-cause confirmer.
The risk is that the report *looks* authoritative with its detailed cards and
explanations, which may lead analysts to treat heuristic output as confirmed findings.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS (`!dumpheap -stat`, `!gcroot`, `!finalizequeue`)

| WinDbg capability | DumpDetective equivalent | Gap |
|---|---|---|
| `!dumpheap -stat` ranked by size | Top-30 candidates table | DumpDetective adds scoring and classification; WinDbg is pure data |
| `!gcroot <address>` — full root path | Not provided — "see §A5" only | **Major gap**: engineers are redirected but the root path is not inlined |
| `!finalizequeue` — current finalizer queue depth | Not shown | Finalizer classification is flag-based, not queue-count-based |
| `!dumpheap -type <typename>` — per-type instances | Sample only | Only one representative address is stored |

### PerfView (Heap Snapshot diff)

PerfView's heap diff workflow shows retained-size trees between two snapshots.
DumpDetective's trend comparer tracks `SuspicionScore` and `TotalSize` over snapshots
but does not compute retained-size deltas. PerfView's retained-size model is
significantly more actionable for growth analysis.

### Visual Studio Diagnostic Tools / dotMemory

Both tools show object reference trees with "retained by" chains that name the concrete
field holding the reference. `LeakCandidateAnalyzer` produces a `RootKind` label
derived from classification rather than an actual field name. This is the largest
functional gap relative to commercial tools.

### Competitive Summary

DumpDetective's `LeakCandidateAnalyzer` is **ahead** of raw WinDbg in classification,
scoring, and report ergonomics. It is **behind** dotMemory and VS in:
1. Retained-size computation
2. Named root-chain evidence (field name + owning object type)
3. Per-instance inspection depth

---

## Final Executive Summary

### Overall Assessment

**Score: 62 / 100**

**Production readiness: Conditional.** Suitable as a triage tool to identify suspects
for further manual investigation. Not suitable as a root-cause confirmation tool.

**Major strengths:**
- Clean O(T) loop with no heap re-scan; scales to 100 GB+ without memory pressure.
- Correct `IDeferredAnalyzer` pattern enabling cross-analyzer signal consumption.
- Rich reporting: `LeakCandidateCard` with explanations, impact bands, GC notes.
- Honest confidence labeling (heuristic-only flagged in both result and report).

**Major weaknesses:**
- Zero root-path evidence despite the platform supporting BFS root traversal.
- Static-root classification tests only the sample address, not all type instances.
- `RootKind` is classification-derived, not ClrMD-sourced — misleading to engineers.
- Retained size missing; shallow-only sizing under-ranks high-reference-depth types.
- Single-label classification discards compound retention signals.

---

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P0-1 | Replace `staticRoots.Contains(sampleAddress)` with a `IsStaticRooted` bit on `TypeAggregateIndexEntry` (set during Phase 1 when a static root address matches the type's MethodTable). | High — eliminates the most common false negative in classification | Medium | High | Improvement |
| P0-2 | Remove the `heap.GetObject(sampleAddress)` call in the hot loop; resolve `MethodTable` directly from `TypeAggregateIndexEntry.MethodTable` and use `aggregate.SampleAddress` instead of calling `GetSampleInstanceAddress`. | Medium (perf + correctness) | Low | High | Improvement |

#### P1 — High

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P1-1 | Add retained-size estimate for top-10 suspects using `MemoryAnalyzer.EstimateRetained` (already exists). Store as `RetainedSize` on `LeakCandidateRecord`. | Very high — the most actionable signal for prioritization | Medium | High | Improvement |
| P1-2 | Emit one `InsightFinding` per Critical-severity candidate (up to 3) rather than always collapsing into a single aggregate finding. | High — multi-leak dumps lose severity diversity | Low | High | Improvement |
| P1-3 | Surface the first GC root hop (field name + owner type) for the top-3 suspects using a single-level `ClrObject.EnumerateReferences()` walk on the sample instance. | High — turns "investigate" into "here is where to look" | Medium | High | Improvement |
| P1-4 | Replace `pinnedTargetTypes` (top-N type names from `GCHandleDomainResult`) with a full `HashSet<ulong>` of pinned addresses stored on `GCHandleDomainResult`. Check `aggregate.SampleAddress` membership. | High — eliminates top-N truncation silently dropping pinned types | Medium | High | Improvement |

#### P2 — Medium

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P2-1 | Add `Gen0Pct` / `Gen1Pct` to `LeakCandidateRecord` from `TypeAggregateIndexEntry.Gen0Count` / `Gen1Count`. Display in candidate table. | Medium — lets analyst distinguish churn from entrenched retention | Low | High | Improvement |
| P2-2 | Propagate `LohCount` / `LohSize` to `LeakCandidateRecord` and use them in `GetImpactBand` and LOH risk note instead of `IsLargeObjectLike` name matching. | Medium — fixes incorrect LOH detection | Low | High | Improvement |
| P2-3 | Replace single-label `LeakClass` with a `[Flags]` enum (or a `ClassificationFlags` property on the record) to allow compound classifications. | Medium — more accurate for statically-pinned types | Medium | High | Improvement |
| P2-4 | Filter well-known infrastructure types (`System.String`, `System.Byte[]`, `System.Threading.Tasks.Task`, `System.EventHandler`) from the candidate list or cap their score at a lower ceiling. | Medium — reduces false-positive noise in top-30 | Low | Medium | Improvement |
| P2-5 | Replace O(T log T) `List.Sort` with a fixed-capacity `PriorityQueue<LeakCandidateRecord, int>(30)` to extract top-30 in O(T log 30). | Low-Medium (perf) | Low | High | Improvement |
| P2-6 | Add module attribution to `LeakCandidateRecord` from `TypeAggregateIndexEntry.ModuleId`. Display in candidate card. | Medium — identifies owning assembly immediately | Low | High | Improvement |

#### P3 — Low

| # | Recommendation | Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P3-1 | Move container heuristic strings (`"Cache"`, `"Dictionary"`, etc.) to a `TypeAggregateFlags` bit (`IsContainerLike`) set during Phase 1. | Low-Medium | Low | High | Evolution |
| P3-2 | Add `IsDictionaryLike` and `IsCacheLike` type-aggregate flags to `TypeAggregateFlags` to replace open-coded name substrings. | Low | Low | High | Evolution |
| P3-3 | Surface the finalizer queue count (from `ClrHeap.EnumerateFinalizableObjects()`) and compare it against `FinalizerRetention` candidates to confirm or downgrade that classification. | Medium | Low | High | Improvement |
| P3-4 | Add `HeuristicOnly = false` path: when `P1-3` (root hop) is implemented, set the flag to `false` to allow the confidence score to rise to 0.75. | Low | Low | High | Improvement |
| P3-5 | Trend comparer should track `Gen2Pct` per type across snapshots to detect entrenching leaks (low Gen2 → high Gen2 over time). | Low | Low | Medium | Improvement |

---

### Final Verdict

1. **Is the analyzer production-ready?**
   Yes, as a *triage layer*. It correctly identifies suspect types and classifies most
   common leak patterns. An engineer using DumpDetective for incident response will get
   a useful shortlist. However, the output cannot confirm any finding — every card
   concludes with "investigate root paths manually".

2. **Highest-impact improvements?**
   P0-1 (static-root classification correctness), P1-1 (retained size), P1-3 (first
   root hop). Together these would transform the analyzer from a suspect-lister into a
   partial root-cause tool.

3. **Platform evolution opportunities?**
   The `GCHandleDomainResult` piggyback pattern is clean and reusable. Extending it to
   carry a full `HashSet<ulong>` of pinned addresses (P1-4) is a platform improvement
   that benefits any future analyzer needing GC handle address coverage.
   Adding `IsStaticRooted` to `TypeAggregateIndexEntry` (P0-1) is a Phase 1 index
   enhancement that multiple analyzers would benefit from.

4. **Highest engineering return?**
   P0-2 (remove redundant `heap.GetObject()` in hot loop) — zero risk, immediate
   correctness improvement, and minor performance gain.
   P1-2 (per-Critical finding emission) — one-line change to `LeakCandidateFindingGenerator`
   that materially improves incident alert quality.
   P2-1 (Gen0/Gen1 on record) — two field additions, no logic change, immediately
   improves the candidate table.
