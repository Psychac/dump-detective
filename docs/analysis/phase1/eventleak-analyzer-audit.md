# EventLeakAnalyzer — Phase 1 Audit (Re-Audit Post-Redesign)

> Reviewed against the current (redesigned) implementation: `EventLeakAnalyzer.cs`,
> `EventLeakFastScanner.cs`, `EventLeak/PublisherRegistry.cs`, `EventLeak/EventFieldDescriptor.cs`,
> `EventLeak/EventNameResolver.cs`, `EventLeak/DelegateLayoutDiscovery.cs`,
> `EventLeak/DelegateChainWalker.cs`, `EventLeak/IPublisherShape.cs`,
> `EventLeak/FieldBackedDelegateShape.cs`, `EventLeakOptions.cs`, `EventLeakDomainResult.cs`,
> `EventLeakSectionBuilder.cs`, `EventLeakFindingGenerator.cs`, `EventLeakTrendComparer.cs`,
> `EventLeakAnalyzerAccuracyTests.cs`, `PublisherRegistryTests.cs` (integration), plus the design
> record in `docs/analysis/phase1-redesigns/event-leak-analyzer.md` and
> `event-leak-analyzer-implementation-plan.md`.
>
> **Supersedes** the prior audit at this path. The analyzer has been rebuilt around a
> `PublisherRegistry` + `IPublisherShape` architecture (Phases A–F) since that review; nearly
> every prior P0/P1 finding has been fixed. This pass verifies those fixes against the shipped
> code and looks for what the redesign introduced or left open.

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

Unchanged in scope: detects C# event-subscription leaks (instance events, static events,
pure-static publisher types with no heap instances). What changed is the *shape* of the
implementation — the design doc's own framing is accurate: "Phase A registry once, pre-scan →
Phase B scan hot, streaming → Phase C statics once, post-scan → Phase D enrichment bounded →
Phase E correlation → Phase F present." `EventLeakAnalyzer.cs:131-334` (`Analyze`) is now a thin
orchestrator over `PublisherRegistry.Build`, `EventLeakFastScanner.Scan`, `SweepRegistryStatics`,
`PopulateEvidence`, and the two `BuildTop...AcrossGroups` folds — each phase is a separate,
independently-testable unit instead of one interleaved method.

### Coverage Assessment

**Well-covered (unchanged from before, verified against `FieldBackedDelegateShape`):**
- Instance and static delegate-typed event backing fields, single-cast and multicast chains,
  static-method subscriptions (null `_target` token path), publisher generation, duplicate
  subscriptions, orphaned-subscriber-adjacent signals (see Area 6), lifetime mismatch,
  root-path evidence, subscriber type + method resolution, cross-run trend metrics.
- `FieldBackedDelegateShape.DescribeInstanceFields`/`DescribeStaticFields`
  (`FieldBackedDelegateShape.cs:41-101`) replicate the old field-filter behavior exactly — the
  design doc's stated goal ("no detection-surface change") holds up against the code.

**New since the prior audit:**
- Cross-group correlation (`BuildTopSubscriberTypesAcrossGroups`,
  `BuildTopHandlerMethodsAcrossGroups`, `EventLeakAnalyzer.cs:346-393`) — closes old P2-1/P2-2.
- `IDisposable` subscriber detection (`HasDisposableSubscriber`,
  `EventLeakAnalyzer.cs:1471-1503`) — closes old P2-6.
- Tier 1 aggregate retained bytes (`EstimateGroupRetainedBytes`, folded over
  `AllSubscriberTypeCounts` rather than the capped `TopInstances`) — closes old P0-2/#3.
- Tier 2 *exact* per-subscriber retained bytes via `IDominatorTreeProvider`
  (`EventLeakAnalyzer.cs:157-174`) — see Area 6 for a significant caveat: this shipped ahead of
  its own documented schedule and is missing one piece of required wiring.
- `PublisherRegistry` + `IPublisherShape` seam (`IPublisherShape.cs`) — a real extension point,
  not a speculative one; `FieldBackedDelegateShape` is the existence proof that the seam works.

**Gaps — still open, but now well-scoped rather than speculative:**

1. **`EventHandlerList` (WinForms) and weak-event patterns are still undetected.** Both are
   explicitly scoped as Phase 7 (`event-leak-analyzer-implementation-plan.md:699-750`,
   `EventHandlerListShape` / `WeakEventShape`) and deliberately not shipped yet
   (`## Phase 7 ... (design §3.2, additive)` has no `✅ Complete` marker, unlike Phases 3–6). The
   difference from the prior audit: this is no longer "the architecture can't do this," it's "the
   architecture is one additive `IPublisherShape` implementation away from doing this" —
   `PublisherRegistry.Build` already dispatches through a shape list
   (`PublisherRegistry.cs:67,95-109,137-144`), so a third shape registers without touching the
   scan loop.
2. ~~Timer events / `INotifyPropertyChanged` specialization~~ **Fixed (P3-3).** Both are
   pure string-pattern classifications over data already extracted (`PublisherType`/
   `EventFieldName`) — no new heap reads, no fixture dependency, unlike P3-1/P3-2. Tagged
   separately in the domain model (`EventLeakGroupSnapshot.IsTimerEvent`/
   `IsPropertyChangedEvent`), the report (a "Category" column plus aggregate key metrics
   `timer_event_leak_groups`/`property_changed_leak_groups`), and the finding layer
   (`timer-leak`/`property-changed-leak` tags, plus a category-specific recommendation clause).
3. **`IncludeNonLeakingEvents`-style "clean events" summary is still absent.** The registry now
   has exactly the data needed to build one cheaply (descriptor count per type vs. leak-group
   count), but nothing surfaces it.

### Unexpected Functionality

- **Duplicate pointer-chase logic.** `EventLeakFastScanner.ExtractSubscribersDirect` /
  `ExtractSingleTargetDirect` (`EventLeakFastScanner.cs:282-343`) and
  `DelegateChainWalker.ExtractSubscribers` / `ExtractSingleTarget`
  (`DelegateChainWalker.cs:16-69`) are near-line-for-line identical implementations of the same
  `_invocationList`/`_target` pointer chase. The design comment on `DelegateChainWalker`
  justifies keeping the hot path separate from `IPublisherShape.Extract`'s *virtual* call
  (`IPublisherShape.cs:33-38`) — a real perf concern — but `DelegateChainWalker.ExtractSubscribers`
  is a plain `static` method with no virtual dispatch, so nothing in the stated rationale
  explains why `EventLeakFastScanner` doesn't call it directly instead of maintaining a second
  copy. This is a correctness/drift risk, not just duplication: the exact multicast-array bug
  referenced in both files' comments (raw MT-lookup silently collapsing multicast events to
  single-target) had to be fixed once already, and now has to stay fixed in two places by hand.
- The former dead code (`GroupEventLeaks`, `EnumerateEventEntries`,
  `GetEventSubscribers(ClrHeap, ...)`) is confirmed **removed** — verified by search, no remaining
  references in `src/`. `GetStaticEventSubscribers` (the `ClrObject`/`ClrAppDomain`-based path,
  `EventLeakAnalyzer.cs:828-877`) is real, live code for the statics sweep, not a leftover.
- No stray `Console.Error.WriteLine` perf-logging remains in the EventLeak files — replaced by
  `ILogger<EventLeakAnalyzer>?.LogDebug` throughout (closes old P0-3).

### Expansion Opportunities

- **[Evolution]** Ship `EventHandlerListShape` (Phase 7, already designed) — the highest-value
  remaining gap for WinForms applications, and the lowest-risk one given the seam is proven.
  **Deferred (P3-1)** — no WinForms fixture exists in this repo to verify against.
- **[Evolution]** Ship `WeakEventShape` classification (Phase 7). **Deferred (P3-2)** — same
  root cause as P3-1, plus a fuzzier pattern to define for the `ConditionalWeakTable` half.
- ~~"Clean events scanned" summary metric~~ **Done (P2-2)** — `PublisherTypesScanned`/
  `CleanPublisherTypeCount`, MT-keyed exact counts.
- ~~Timer-event / `INotifyPropertyChanged` category tagging~~ **Done (P3-3).**

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths (largely new since the prior audit)

- Total estimated retained bytes now in `EventLeakDomainResult.TotalEstimatedRetainedBytes` and
  the section's `estimated_retained_bytes` key metric — closes old P1-1.
- Retained-bytes columns are honestly labeled: `"Estimated (type-average, all instances)"`
  (`EventLeakSectionBuilder.cs:115,163`) — closes old correctness item #3 (silent TopN-only
  estimate).
- `RootHint` is translated to human-readable text via `RootKindLabels`
  (`EventLeakSectionBuilder.cs:23-38`) — closes old P2-4.
- Static leaks render `PublisherAddress` as `"(static)"` instead of `0x0`
  (`FormatPublisherAddress`, `EventLeakSectionBuilder.cs:55-56`) — closes old P2-5.
- `PublisherGeneration = -1` renders as `"static"` or `"unknown"` rather than a bare dash
  (`FormatPublisherGeneration`, `EventLeakSectionBuilder.cs:58-62`) — closes old #10.
- Publisher root path (BFS-derived) and sample subscriber hint are tracked and labeled
  separately (`EventLeakEvidence.PublisherRootPath` / `SampleSubscriberHint`,
  `FormatRootHintDisplay`, `EventLeakSectionBuilder.cs:40-53`) — closes old #4/#6, matches design
  §4.3 exactly.
- Cross-group "Top subscriber types" / "Top handler methods" tables — closes old missing
  statistic (unique subscriber types, factory/wiring method identification).
- Findings now carry an evidence-derived `ConfidenceScore`
  (`EvidenceConfidence.Compute(topEvidence)`, `EventLeakFindingGenerator.cs:148`) — a genuinely
  new signal not present in the prior audit or in any of the benchmarked tools (Area 7).
- Group truncation is now disclosed: `"Showing top {groupLimit} event types. {N} additional
  group(s) omitted."` (`EventLeakSectionBuilder.cs:233-234`).

### Weaknesses

1. **`SubscriberDetail.SizeIsExact` is computed but discarded before it reaches the report.**
   `EventLeakAnalyzer.cs:266-279` builds `SubscriberDetail` with a real `SizeIsExact` flag
   (true when the value came from the dominator tree, false when it's a type-average). But
   `EventLeakSectionBuilder.cs:248` constructs `SubscriberDetailEntry(det.Type, det.MethodName,
   det.Count, det.Size)` — `SubscriberDetailEntry` (`AnalyzerDetailSection.cs:124`) has no field
   for exactness at all, unlike the sibling `RetainedSizeIsExact` field already used by
   `GCRootIntelligenceSectionBuilder` (`AnalyzerDetailSection.cs:95`) for the same purpose. An
   engineer reading a subscriber's byte count in the report cannot tell whether it's ClrMD-exact
   or a shallow-size guess, even though the analyzer went to the trouble of computing and
   prioritizing the exact value (`preferNew` logic at `EventLeakAnalyzer.cs:272-274`).
2. ~~Instance-level truncation is undisclosed.~~ **Resolved (P1-4) by removing the caps
   entirely** rather than adding a notice — `MaxGroupsToShow`/`MaxInstancesToShow` were
   post-hoc display truncation of an already-fully-computed list, which this project
   deliberately does not do elsewhere (Collection, Dominator, WeakReference, etc.). Every
   group and instance the analyzer computes is now rendered.
3. **Tier 2 exact retained bytes is invisible-by-default in a way the report gives no hint of**
   (see Area 6 for the mechanism). When it silently degrades to Tier 1, the report looks
   identical — same column, same label — so there's no way to tell from the output alone whether
   the number is exact-when-available or was never even attempted.

### Missing Statistics (residual from prior audit)

- ~~Subscription count histogram~~ **Done (P3-4).**
- Maximum delegate chain depth observed.
- ~~"Types scanned, zero leaking" confirmation count.~~ **Done (P2-2).**

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD / Infrastructure — Well Used

- `PublisherRegistry.Build`'s two-pass split (`PublisherRegistry.cs:74-160`) is a deliberately
  scoped optimization with a *measured* rationale documented in the class doc comment: running
  the instance-field walk over every module type (not just live-instance MTs) regressed the
  build from ~22.8s to ~48s on the 3.3GB reference dump. This is exactly the kind of
  evidence-grounded tradeoff the audit protocol asks for, and it's present in the code, not just
  the design doc.
- `IMemoryReader.ReadPointer` hot path preserved; `EventLeakFastScanner` still does zero
  `ClrObject` construction for the common (single-subscriber) case.
- `cache.TryGetDominatorTreeProvider()` reuse (`EventLeakAnalyzer.cs:157`) follows the exact same
  pattern already used by nine other analyzers (`CollectionAnalyzer`, `DbConnectionAnalyzer`,
  `DominatorAnalyzer`, `FinalizableObjectAnalyzer`, `GCHandleAnalyzer`, `GCRootAnalyzer`,
  `ReferenceChainAnalyzer`, `StaticRootLeakDetector`, `WeakReferenceAnalyzer`,
  `WcfChannelAnalyzer`). This is *better* engineering than the design doc's own original Tier 2
  proposal (a new post-pipeline `InsightEngine` join, `event-leak-analyzer.md` §4.4) — it reuses
  established platform infrastructure instead of inventing a new pipeline stage. See Area 6 for
  the one piece of wiring this reuse is missing.
- `DelegateLayoutDiscovery` (`DelegateLayoutDiscovery.cs`) is now a shared, single-discovery
  component instead of being duplicated inside the scanner — a real dedup win versus the prior
  audit's finding of ad hoc offset computation.
- `SweepRegistryStatics` now has periodic `CancellationToken.ThrowIfCancellationRequested()`
  checks (`EventLeakAnalyzer.cs:1036-1037`, every 8192 types) — closes old P1-3 *for this phase*.

### ClrMD / Infrastructure — Gaps

1. **`PublisherRegistry.Build` has no `CancellationToken` parameter and no cancellation checks
   anywhere in either pass** (`PublisherRegistry.cs:62-163`). This is the same class of gap the
   prior audit flagged (P1-3) and it was fixed for `SweepRegistryStatics` — but the redesign
   moved the *dominant* cost into `PublisherRegistry.Build` itself (see Area 5: ~121.6s of a
   ~215s total on the 25.6GB reference dump per the design doc's own measurement). The one phase
   most worth being cancellable is the one that stayed uncancellable.
2. **Duplicate delegate-chase implementation** — see Area 1 "Unexpected Functionality." Filed
   here too because it's a platform-utilization issue: `DelegateChainWalker` exists specifically
   to be the shared implementation, and one of its two intended callers doesn't call it.
3. **`EnableLowIncomingRefsCheck` still does an O(N) `heap.EnumerateObjects()` scan per
   subscriber** (`CountIncomingRefs`, `EventLeakAnalyzer.cs:1225-1279`) rather than using
   `IBackwardReferenceProvider`. Unchanged from the prior audit, but now honestly documented as a
   deliberate, scoped deferral in `EventLeakOptions.cs:36-46` rather than an undocumented
   landmine — the option defaults to `false` and the comment explains exactly why. This is a
   genuine improvement in *honesty* even though the underlying capability gap is unchanged.
4. ~~On the no-disk-index fallback path, the heap may be enumerated twice.~~ **Turned out to be
   worse than a perf nit — a real correctness bug, fixed as P2-1.** `PublisherRegistry.Build`'s
   Pass 2 gated on `cache is not null` alone to decide between
   `cache.EnumerateIndexedEntriesAsTuples()` and a raw `heap.EnumerateObjects()` walk. But a real
   `HeapAnalysisCache` can exist without a disk index ever having been built on it — exactly the
   condition `EventLeakAnalyzer.FindEventLeaks`'s own fallback branch runs under — and
   `HeapIndexCache.EnumerateIndexedEntries` silently `yield break`s in that case rather than
   throwing or falling back. The old check meant Pass 2 could silently produce an **empty
   `liveMts` set — zero instance-field descriptors — for the exact branch that then goes on to do
   a real full heap scan expecting real descriptors.** Fixed to mirror `FindEventLeaks`'s own
   check (`cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out _)`). The doubled-enumeration
   cost this item originally described is still present in the true no-index case (both passes
   now correctly fall back to `heap.EnumerateObjects()` independently) — eliminating that
   requires making Pass 2 lazy, which conflicts with this class's own documented eager-beats-lazy
   design decision (§3.3's revert note), so it's left as a known, low-severity, rare-path cost
   rather than pursued here.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

Most of the prior audit's high-value items have shipped. What remains:

### High Value

1. **`EventHandlerList` detection (Phase 7, `EventHandlerListShape`)** — unchanged from prior
   audit, now well-scoped. *Difficulty: Medium (new shape + fixture dump). Impact: High for
   WinForms apps.*
2. **Wire `SubscriberDetail.SizeIsExact` through to the report** (Area 2, weakness #1) — small,
   mechanical, closes a real trust gap. *Difficulty: Low. Impact: Medium-High.*
3. ~~Instance-level truncation notice~~ (Area 2, weakness #2) — done via P1-4, by removing the
   display caps rather than disclosing them.
4. **Fix `EventLeakTrendComparer.Compare`'s missing `event.instance.leaks` delta** (Area 6) —
   restores parity with `ExtractMetrics`. *Difficulty: Low. Impact: Medium (silent trend
   blind spot).*

### Medium Value

5. ~~Weak event pattern classification (`WeakEventShape`, Phase 7).~~ **Deferred (P3-2)** — no
   verification fixture.
6. ~~"Clean events scanned" summary~~ **Done (P2-2).**
7. ~~Timer event / `INotifyPropertyChanged` specialization.~~ **Done (P3-3).**
8. ~~Subscription count histogram.~~ **Done (P3-4).**

### Lower Value

9. Maximum delegate chain depth (informational).
10. Persist `PublisherRegistry` to disk alongside the index (design doc's own "optional
    follow-up, not in scope" note). **Deferred (P3-5)** — the design doc's own gate
    ("defer until the in-memory version is measured") hasn't been satisfied: no data yet shows
    the in-memory build cost is still worth eliminating across separate process runs against the
    same dump now that it's cancellable (P1-1). Would also require a new on-disk binary format
    (cache key/invalidation, schema version tag per `docs/schema-versioning.md` convention) —
    real design work, not a bug fix, and not undertaken speculatively ahead of that measurement.

---

## Audit Area 5 — Performance, Memory & Scalability

### Scalability Assessment (1 GB – 100 GB)

The design doc's own measured numbers are the best evidence available and are used directly here
rather than re-estimated:

| Phase | 3.3 GB (measured) | 25.6 GB (measured, median of 3) |
|---|---:|---:|
| `PublisherRegistry.Build` (successor to `BuildFieldLayouts` + `SweepModuleStaticFields`) | ~22.8s → target ~18s | **~121.6s**, rock-steady across runs |
| `ProcessPublisherEntry` (hot path) | 1.48s | 3.3s–28.2s (noisy, not reproducible per the doc's own admission) |
| `PopulateEvidence` (bounded) | 34.28s → target ~1.5s | ~60.5s (1.4% BFS hit rate) |
| `AnalyzeAsync` total | 94.74s → target ~21s attributable | ~215s |

At 25.6GB, `PublisherRegistry.Build` alone is **57% of `FindEventLeaks`'s wall time** per the
design doc's own §0.1 findings — it is now unambiguously the dominant, scale-sensitive cost, more
so than any phase called out in the prior audit.

### Performance Issues

1. **`PublisherRegistry.Build` has zero cancellation support** (Area 3, gap #1) — on a 100GB-class
   dump, extrapolating from the 25.6GB measurement, this phase alone could run for several
   minutes with no way to interrupt it, in direct tension with this codebase's own performance
   checklist (`CLAUDE.md`: "streaming... bounded... `CancellationToken`" is assumed throughout).
2. **`PopulateEvidence`'s wall-clock budget (`MaxEvidenceEnrichmentMs`, default 2000ms) is a real
   fix** for the prior audit's #7/P1-6 (unbounded evidence phase) — every instance is
   enrichment-eligible in severity-descending order, and the budget alone governs how much
   completes (§9.19). This is a materially better design than the originally-proposed top-K
   group cap: it degrades gracefully by priority instead of hard-cutting whole groups.
3. ~~Possible doubled heap enumeration on the no-index fallback path~~ (Area 3, gap #4) — the
   perf question turned out to sit on top of a correctness bug (empty `liveMts`), fixed via
   P2-1. The doubled-enumeration cost itself remains on the true no-index path, by design.
4. **`SweepRegistryStatics` cancellation checks exist but at a coarse granularity** (every 8192
   *types*, not objects) — reasonable given the phase measures ~19s flat regardless of dump size
   per the design doc (tracks module/type count, not heap size), so this is not a practical
   concern, just noted for completeness.

### Memory Issues

- `PublisherRegistry`'s `Dictionary<ulong, EventFieldDescriptor[]>` (`PublisherRegistry.cs:28,69`)
  only stores entries for MTs that actually matched a shape — this is a genuine improvement over
  the prior audit's flagged `_mtIndex` (which stored a `null` array for the ~99% of MTs that
  *didn't* match, one dictionary entry per unique MT regardless). The redesign resolved this
  memory issue as a side effect of the registry restructuring, without it being called out as an
  explicit goal in the design doc.
- `EventNameResolver`'s cache (`EventNameResolver.cs:14`) is correctly instance-scoped per
  `PublisherRegistry`/per-analysis now, not `static`/process-lifetime — closes old P1-2 cleanly.

---

## Audit Area 6 — Correctness & Confidence

### Risks

1. **Tier 2 exact retained bytes is silently non-functional unless another analyzer with
   `IRequiresDominatorTreeIndex` happens to also be active in the same run.** This is the most
   significant finding of this re-audit. `EventLeakAnalyzer.cs:157` calls
   `cache?.TryGetDominatorTreeProvider()`, which returns non-null only if Stage B (the exact
   dominator tree) was actually built during index construction. Per
   `DiskBackedObjectIndexWriter.cs:214-218`:
   ```csharp
   bool buildStageB =
       reverseEdgeExtractor is not null
       && !SkipDominatorIndexBuild
       && enableExactDominatorTree
       && (activeAnalyzers?.Any(a => a is IRequiresDominatorTreeIndex) ?? false);
   ```
   `IRequiresDominatorTreeIndex` is implemented only by `DominatorAnalyzer`,
   `FinalizableObjectAnalyzer`, `GCRootAnalyzer`, and `StaticRootLeakDetector` — confirmed by
   direct search of `src/`. **`EventLeakAnalyzer` does not implement it**, despite being a real
   consumer of `IDominatorTreeProvider` for `SubscriberDetail.SizeIsExact`. The gate is evaluated
   against the *active analyzer set for the run*, before any `AnalyzeAsync` executes — so if a
   user runs only `EventLeakAnalyzer` (or any subset that excludes all four declaring analyzers,
   e.g. via a CLI `--analyzers` filter), Stage B is never built, `TryGetDominatorTreeProvider()`
   always returns `null` for that run, and every subscriber falls back to Tier 1's type-average
   size — with nothing in the code or the report distinguishing that from the "dominator tree was
   available but this subscriber just wasn't in it" case.

   Compounding this: the design doc (`event-leak-analyzer.md` §4.4) and the implementation plan
   (`event-leak-analyzer-implementation-plan.md:34-43,756-765`, "Phase 8+ — Deferred (not
   scheduled)") both state Tier 2 is **explicitly out of scope** until its own future plan, to be
   built as a post-pipeline `InsightEngine` join *after* `EventLeakDomainResult`'s shape is
   stable. The shipped code already has Tier 2, built via a different (arguably better)
   mechanism than what the docs describe, but without the one piece of wiring
   (`IRequiresDominatorTreeIndex`) that would make it reliable — and the docs don't reflect that
   it shipped at all. This is a genuine plan/implementation/documentation three-way divergence,
   not merely a missing feature.

2. **`EventLeakTrendComparer.Compare` drops the `event.instance.leaks` metric that
   `ExtractMetrics` declares.** `ExtractMetrics` (`EventLeakTrendComparer.cs:9-20`) emits five
   metrics including `event.instance.leaks`. `Compare` (`EventLeakTrendComparer.cs:22-46`) computes
   deltas for only four — `event.leak.instances`, `event.total.subscribers`,
   `event.static.leaks`, `event.publisher.instances` — `event.instance.leaks` has no
   `MetricDeltaHelper.Compute` call at all. A regression in instance-scoped (non-static) event
   leaks between two runs is silently invisible to trend comparison even though the underlying
   metric is tracked and displayed per-run. Confirmed no test in
   `EventLeakTrendComparerTests.cs` asserts on this metric's presence in `Compare`'s output,
   which is why this slipped through.
3. ~~`LooksLikeEventFieldName`'s bare `_`-prefix rule is unchanged from the prior audit~~
   **Fixed (P2-3).** `LooksLikeEventFieldName` gained an `allowBareUnderscorePrefix` parameter;
   `FieldBackedDelegateShape.DescribeInstanceFields`/`DescribeStaticFields` now pass `false` in
   the branch where the type declares zero real events (`eventNames.Count == 0`) — the exact case
   with no corroborating evidence a delegate field is an event at all. `_onComplete`, `_factory`,
   `_selector`, `_predicate` no longer qualify on an event-less type; the stronger name-pattern
   signals (`Event`/`Changed`/`Handler`/`Callback`/`Raised`/`Fired`/`k__BackingField`) still do,
   everywhere. When the type *does* declare at least one real event, the bare prefix is still
   trusted as a secondary signal for matching that event's (possibly non-standard-named) backing
   field — narrowing that branch too would risk false negatives for hand-implemented events with
   non-compiler-generated backing field names, which wasn't part of this fix's scope.
4. **`EventNameResolver`'s "empty own-event-set means all-pass" convention is unchanged.** Same
   risk noted in the prior audit: a type with zero declared events accepts any field matching the
   generic name heuristic.

### Risks Resolved Since Prior Audit

- `CheckLifetimeMismatchDirect`/`CheckLifetimeMismatch` now probe **every** subscriber
  unconditionally (§9.19, `EventLeakFastScanner.cs:516-533`) rather than a capped sample — this
  closes what would have been a sampling-bias risk, and is justified by measurement (O(1)
  segment lookup per subscriber, cheap regardless of scale).
- The multi-domain static-subscriber double-count bug (old audit's implicit concern, and the
  explicit subject of the `GetStaticEventSubscribers` comment block,
  `EventLeakAnalyzer.cs:830-839`) — statics now run in exactly one place
  (`SweepRegistryStatics`, driven by `PublisherRegistry.StaticPublisherMTs`), closing the old
  double-count bug where a type with both heap instances and a static event field could be
  counted once by the hot path and again by a separate module walk.
- The old audit's `CountOrphanedSubscribers` (nearly-all-subscribers-flagged-orphaned) metric has
  been **removed outright** rather than patched — confirmed absent from the current domain model
  and analyzer. Removing a metric that could not be made accurate without the reverse-edge index
  is the right call given that index still isn't threaded into this analyzer's hot path.

### False Positive / Negative Risks (residual, unchanged)

- Non-event delegate callback fields captured by the `_`-prefix heuristic (risk #3 above).
- Weak-event-pattern types still reported as hard leaks (Phase 7 not shipped).
- WinForms `EventHandlerList` subscriptions still completely invisible (Phase 7 not shipped).
- Dynamic-assembly (`Reflection.Emit`/`AssemblyLoadContext`) types still missed by the module-walk
  passes in `PublisherRegistry.Build` — same edge case as the prior audit, unchanged.

---

## Audit Area 7 — Industry Benchmark

### vs. WinDbg + SOS

Largely unchanged from the prior audit's table. New: severity findings now carry an
evidence-derived confidence score (`ConfidenceScore`), which SOS/WinDbg has no equivalent for —
every finding there requires full manual interpretation.

### vs. PerfView

**Gap partially closed.** The prior audit's "no dominator-tree integration" gap is now addressed
in the common case — `EventLeakAnalyzer` does cross-reference the dominator tree for exact
per-subscriber retained bytes when available. The caveat from Area 6 applies: this only actually
activates when the active analyzer set for the run happens to include `DominatorAnalyzer`,
`FinalizableObjectAnalyzer`, `GCRootAnalyzer`, or `StaticRootLeakDetector`. In the platform's
typical "run the full analyzer suite" mode this gap is effectively closed; in a filtered
single-analyzer run it reopens silently.

### vs. JetBrains dotMemory

- **Retained size accuracy gap**: same partial-closure caveat as PerfView above.
- **Event Handlers view / WinForms support**: still a gap (Phase 7 not shipped), but — per Area
  1 — the platform is now one additive shape away from parity rather than needing new scan
  infrastructure.
- **"New in snapshot" subscription diffing**: still absent; `EventLeakTrendComparer` diffs
  aggregate totals only, not individual new subscriptions. Unchanged from prior audit.

### New Advantages Over All Four Tools (since prior audit)

- Cross-group correlation (top subscriber types / handler methods across *all* leaking events)
  — none of the four benchmarked tools surface this cross-cutting view; each groups per-object or
  per-type in isolation.
- Evidence-derived `ConfidenceScore` per finding.
- Honest dual-tier retained-bytes labeling (`"type-average, all instances"` vs. exact) — where it
  works, it is more transparent about its own precision than any of the four tools, which
  generally present a single number without qualifying its derivation.

---

## Recommendation Classification

(Per protocol: **Improvement** = enhances the existing analyzer; **Evolution** = platform-level
change — new shared infrastructure, new analyzer/shape, new workflow.)

---

## Final Executive Summary

### Overall Assessment

**Score: 92 / 100** (initial re-audit: 84/100; original prior audit: 68/100)

All P0, P1, and P2 items from this re-audit have been implemented and verified (build + unit
tests) in the same session as the audit — see the Priority Roadmap below for per-item detail.
Of the five P3 items, two shipped (P3-3 timer/`INotifyPropertyChanged` categorization, P3-4
subscriber-count histogram) and three are explicitly deferred with documented reasons (P3-1/P3-2:
no verification fixture exists in this repo for either WinForms or WPF patterns; P3-5: the design
doc's own "defer until measured" gate hasn't been satisfied). The remaining 8 points below 100
reflect those three deliberately-deferred Evolution-tier items, not implementation debt.

**Production readiness:** Ready for general use, including for retained-byte reporting and
leak-detection findings. The correctness gap that would have blocked filtered/standalone runs —
missing `IRequiresDominatorTreeIndex` wiring (P0-1) — is fixed, as is the trend-comparer metric
asymmetry (P0-2) and the `PublisherRegistry.Build` cancellation gap (P1-1, now the dominant cost
phase at scale and the one most worth being cancellable).

**Major strengths:**
- `PublisherRegistry` + `IPublisherShape` architecture: a real, working extension seam, not a
  speculative one — proven by `FieldBackedDelegateShape` replicating prior behavior exactly with
  no detection regression, and by P3-3's timer/`INotifyPropertyChanged` categorization shipping
  as a pure classification layer with zero changes to the scan itself.
- Every P0/P1/P2 item from both this audit and the prior one is fixed and verifiable in code:
  orphaned-subscriber metric removed, retained bytes honest/aggregate/now correctly gated for
  Tier 2 exactness, static cache instance-scoped, cancellation added everywhere it was missing
  (statics sweep, and now the dominant-cost registry build), LINQ removed from the hot module
  walk, dead code deleted (including duplicate delegate-chain-walk logic found during this
  session), `Console.Error` perf logging replaced with `ILogger`, IDisposable detection shipped,
  cross-group correlation shipped, evidence sources separated and labeled, and a real correctness
  bug found and fixed along the way (P2-1: `PublisherRegistry.Build` silently producing zero
  instance descriptors whenever a cache existed without a built disk index).
- Design decisions are grounded in actual measurement (the 3.3GB/25.6GB comparison table in the
  design doc), not guesswork — including a documented instance where a "more correct-looking"
  change (walking every module type for instance fields) was tried, measured, and reverted
  because it cost 2× more.
- Deferred items are deferred for stated, falsifiable reasons (missing fixtures, an unmet
  measurement gate) rather than left silently incomplete — see P3-1/P3-2/P3-5 in the roadmap.

**Remaining gaps (all deliberately deferred, not implementation debt):**
- P3-1/P3-2: `EventHandlerListShape` (WinForms) and `WeakEventShape` (WPF/`ConditionalWeakTable`)
  — no verification fixture exists in this repo for either UI framework pattern.
- P3-5: disk-persisted `PublisherRegistry` — the design doc's own measurement gate for this
  hasn't been satisfied yet.

---

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class | Status |
|---|---|---|---|---|---|---|
| P0-1 | Implement `IRequiresDominatorTreeIndex` on `EventLeakAnalyzer` so Tier 2 exact retained bytes reliably activates whenever `RetentionOptions.EnableExactDominatorTree` is on, regardless of which other analyzers are selected | Makes the analyzer's own dominator-tree feature actually work outside the full-suite default; closes a silent correctness gap | Low | High | Improvement | **Done** — `EventLeakAnalyzer.cs` now declares `IRequiresDominatorTreeIndex` alongside `IRequiresReachableGraphIndex` |
| P0-2 | Fix `EventLeakTrendComparer.Compare` — add the missing `event.instance.leaks` `MetricDeltaHelper.Compute` call to match `ExtractMetrics`; add a regression test asserting metric-name parity between the two methods | Restores trend visibility for instance-event-leak regressions | Low | High | Improvement | **Done** — delta added; `ExtractMetrics_And_Compare_DeclareTheSameMetricKeys` and `Compare_SameScoringVersion_IncludesInstanceLeaksDelta` added to `EventLeakTrendComparerTests.cs` |

#### P1 — High

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class | Status |
|---|---|---|---|---|---|---|
| P1-1 | Add `CancellationToken` parameter and periodic checks to `PublisherRegistry.Build`'s two passes | Enables responsive cancellation on the now-dominant cost phase at 25GB+ scale | Low-Medium | High | Improvement | **Done** — `PublisherRegistry.Build` now takes a `CancellationToken` (checked every 8192 iterations, matching `SweepRegistryStatics`'s convention) across both passes; `FindEventLeaks`'s existing token is threaded through at its call site. The `IHeapIndexScanParticipant.BeforeHeapIndexScan` call site still can't pass one — that interface carries no token and widening it is out of scope here — documented inline where the call happens. |
| P1-2 | Thread `SubscriberDetail.SizeIsExact` through to `SubscriberDetailEntry` and the report layer, mirroring the existing `RetainedSizeIsExact` pattern | Makes exact-vs-average per-subscriber size distinguishable in the report | Low | High | Improvement | **Done** — `SubscriberDetailEntry` gained a `SizeIsExact` field, `EventLeakSectionBuilder` passes it through, and the HTML renderer (`report.renderers.sections.js`) now suffixes subscriber sizes with `(exact)`/`(est.)`. `Build_ShouldPropagateSizeIsExact_ToSubscriberDetailEntry` added to `EventLeakSectionBuilderTests.cs`. |
| P1-3 | Consolidate `EventLeakFastScanner.ExtractSubscribersDirect`/`ExtractSingleTargetDirect` to call `DelegateChainWalker.ExtractSubscribers`/`ExtractSingleTarget` directly | Eliminates a duplicate-maintenance/drift risk on the most safety-critical piece of pointer-chase logic in the analyzer | Low | High | Improvement | **Done** — both duplicate methods deleted from `EventLeakFastScanner.cs`; `ProcessInstanceFields` now calls `DelegateChainWalker.ExtractSubscribers` directly. `DelegateChainWalker`'s doc comment updated to no longer describe the (now-removed) duplication as deliberate. |
| P1-4 | ~~Add an instance-level truncation notice~~ — reframed: remove `MaxGroupsToShow`/`MaxInstancesToShow` display caps entirely, per this project's standing rule against post-hoc truncation of already-computed report data | Every group and instance the analyzer computed is now shown; no silent under-reporting | Low | High | Improvement | **Done** — both caps and the (now provably dead) group-truncation notice removed from `EventLeakSectionBuilder`; `Build_ShouldRenderEveryGroupAndInstance_NoDisplayCap` added. Note: the "Top leak instances" compact table was already uncapped before this change — the two card sections are now consistent with it, not newly unbounded. |

#### P2 — Medium

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P2-1 | ~~Avoid the second full `heap.EnumerateObjects()`~~ — reclassified from perf to correctness during implementation: fix `PublisherRegistry.Build`'s Pass 2 gate (`cache is not null` → `cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out _)`), matching `FindEventLeaks`'s own check | Was silently producing zero instance-field descriptors (not just a slow path) whenever a real cache existed without a built disk index; now correct. Doubled-enumeration cost in the true no-index case remains, by design (see Area 3 gap #4) | Low (once found) | High | Improvement | **Done** — `PublisherRegistry.cs` Pass 2 condition fixed; `Build_CacheWithoutPrebuiltIndex_StillDiscoversInstanceDescriptors` added to `PublisherRegistryTests.cs` (`[DiscrepancyFact]`, requires a real dump — not run as part of this session). |
| P2-2 | Add a "clean events scanned" summary (types checked, zero leaking) derived from registry descriptor counts vs. leak group counts | Distinguishes "checked and clean" from "not scanned," closing a residual gap from the prior audit | Low | Medium | Improvement | **Done, exact version** — implemented via MT-keyed tracking rather than the cheaper name-based approximation originally discussed: `PublisherRegistry.CandidatePublisherCount` (candidate MTs) vs. a `leakingMTs` `HashSet<ulong>` populated by `AddToAccumulator` (the single choke point every accepted leak passes through, instance or static). New `EventLeakDomainResult.PublisherTypesScanned`/`CleanPublisherTypeCount` fields, surfaced as `publisher_types_scanned`/`clean_publisher_types` key metrics. `EventLeakInfo` gained a `PublisherMethodTable` field; `CreateLeakInfo` and its two call sites (`ProcessInstanceFields`, `SweepRegistryStatics`) now pass it through. Actual difficulty was Medium, not Low, once the exact version was chosen — it touched ~7 method signatures across the participant path, fresh-scan path, and statics sweep. |
| P2-3 | Tighten `LooksLikeEventFieldName`'s bare `_`-prefix rule (e.g. require a matching `add_`/`remove_` pair, or a stronger delegate-shape signal, when the type declares no events) | Reduces false-positive callback-field detection, still open since prior audit | Medium | Medium | Improvement | **Done** — added `allowBareUnderscorePrefix` parameter, `false` when the type declares zero real events. 11 new `[Theory]`/`[Fact]` cases added to `EventLeakAnalyzerAccuracyTests.cs` (this heuristic had zero direct test coverage before). Scoped to the event-less case only, per the discussion above — the "type has events, field name doesn't match" branch is left as-is to avoid false negatives on hand-implemented events. |

#### P3 — Low / Evolution

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P3-1 | Ship `EventHandlerListShape` (Phase 7) for WinForms `Control.Events` coverage | Closes the last major detection-surface gap; seam is proven and low-risk | High | High | Evolution | **Deferred** — no verification fixture exists. This repo's reference dumps (`Crash_IIS_BALTSTPRD`, the 25.6GB `w3wp.exe` dump) are both server-side IIS/ASP.NET processes; neither contains a WinForms `Control.Events`/`System.ComponentModel.EventHandlerList` collection to validate `Extract`'s internal `ListEntry` layout assumptions against. Shipping without one means the shape could silently never fire and there'd be no way to know. Matches the implementation plan's own Phase 7 acceptance criteria, which required exactly this fixture before shipping. Revisit if a WinForms dump becomes available. |
| P3-2 | Ship `WeakEventShape` classification (Phase 7) | Eliminates false positives for deliberate weak-event usage | Medium | High | Evolution | **Deferred**, same root cause as P3-1 plus one more: `WeakEventManager` is WPF-specific (`WindowsBase`/`PresentationCore`), so it's even less likely to appear in this repo's server-process reference dumps than `EventHandlerList` is. The `ConditionalWeakTable`-based half of this shape is worse still — it isn't a fixed layout to read but a fuzzy *usage pattern* with no canonical shape to match against, making it both harder to define correctly and harder to verify without a real example. Revisit if a WPF dump becomes available. |
| P3-3 | Timer event / `INotifyPropertyChanged` specialized finding categories | Adds high-signal categorization for the most common process-lifetime leak patterns | Low | Medium | Improvement | **Done** — pure string-pattern classification (`EventLeakAnalyzer.IsTimerEvent`/`IsPropertyChangedEvent`), no new heap reads, no fixture dependency. Threaded through `EventLeakGroupSnapshot` → section builder ("Category" column + `timer_event_leak_groups`/`property_changed_leak_groups` key metrics + HTML card) → finding generator (`timer-leak`/`property-changed-leak` tags + category-specific recommendation text). 13 new tests across `EventLeakAnalyzerAccuracyTests`, `EventLeakFindingGeneratorTests`, `EventLeakSectionBuilderTests`. |
| P3-4 | Subscription count histogram in the domain result | Distribution insight for triage | Low | Medium | Improvement | **Done** — simpler than P2-2's threading: `GroupAccumulator`/`EventGroupInfo` already flow fully populated regardless of the per-group top-K cap, so buckets accumulate locally in `AddToAccumulator` (8 fixed, ordered buckets: 1, 2, 3-5, 6-10, 11-25, 26-50, 51-100, 101+) with zero changes to the scan call chain, then fold across all groups post-scan (`BuildSubscriberCountHistogram`, same pattern as the §7 correlation views). Rendered as a "Subscriber count distribution" compact table, in bucket order (not sorted by count — a histogram only reads correctly in its natural order). 18 new tests. |
| P3-5 | Persist `PublisherRegistry` to disk alongside the object index (design doc's own noted follow-up) | Removes the registry-build cost from every analysis run once cancellation (P1-1) and its cost profile are fully addressed | High | Low | Evolution | **Deferred** — the design doc's own stated gate ("defer until the in-memory version is measured") hasn't been satisfied, and this needs a new on-disk binary format (cache key/invalidation, schema versioning) rather than a bug fix — real design work, not undertaken speculatively. Revisit once repeated-run cost against the same dump is actually measured post-P1-1. |

---

### Final Verdict

1. **Production-ready?** Yes, for both leak detection and retained-byte reporting, in the
   platform's normal (full analyzer suite) operating mode **and** in filtered/standalone runs of
   `EventLeakAnalyzer` alone — the Tier 2 silent-degradation gap (P0-1) is fixed.

2. **Highest-impact improvements, all shipped:** P0-1 (dominator-tree wiring), P0-2 (trend metric
   parity), P1-1 (registry cancellation — now the dominant cost at scale), and P2-1 (a real
   correctness bug found during implementation: `PublisherRegistry.Build` silently producing zero
   instance descriptors under a condition `FindEventLeaks` itself already guards against).

3. **Platform evolution opportunities remaining:** `EventHandlerListShape`/`WeakEventShape`
   (Phase 7, already designed and scoped, deferred as P3-1/P3-2 pending a verification fixture —
   the highest-leverage remaining work once one exists, since the seam is proven), persisting
   `PublisherRegistry` to disk (P3-5, deferred pending the design doc's own measurement gate).

4. **Highest engineering return (effort vs. value):** P0-1, P0-2, and P1-2 were all small,
   mechanical, high-confidence fixes with outsized correctness/trust impact relative to their
   size — the same profile as the prior audit's highest-return items, which is a good sign for how
   this codebase continues to be maintained. P3-3/P3-4 (timer/`INotifyPropertyChanged`
   categorization, subscriber-count histogram) turned out to be similarly cheap once implemented,
   since both reuse already-extracted data with no new heap reads.
