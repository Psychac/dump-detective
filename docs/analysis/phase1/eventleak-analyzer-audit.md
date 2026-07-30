# EventLeakAnalyzer — Phase 1 Audit

> Reviewed against: `EventLeakAnalyzer.cs`, `EventLeakFastScanner.cs`,
> `EventLeakOptions.cs`, `EventLeakDomainResult.cs`,
> `EventLeakSectionBuilder.cs`, `EventLeakFindingGenerator.cs`,
> `EventLeakTrendComparer.cs`, accuracy tests, discrepancy integration test.

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

Detects C# event subscription leaks: patterns where subscriber objects remain
registered with publisher events and are therefore prevented from GC collection.
Covers instance events, static events, and pure-static publisher types with no
heap instances.

Key pipeline integration: implements `IHeapIndexScanParticipant` so the heap
index pass is shared with other analyzers; no redundant full-heap scan in the
common disk-backed-index path.

### Coverage Assessment

**Well-covered:**
- Instance delegate-typed event backing fields on non-system, non-compiler types
- Static event fields (two passes: once per heap instance via FastScanner, once via `SweepModuleStaticFields` for instance-free types)
- MulticastDelegate chain traversal (single-cast and multi-cast)
- Static method subscriptions (null `_target` path — delegate object used as token)
- Publisher generation, duplicate subscriptions, orphaned subscribers, lifetime mismatch
- Root path evidence via `RootPathFinder` for top instances
- Subscriber type + method name resolution
- Cross-run trend metrics via `EventLeakTrendComparer`

**Gaps — coverage:**

1. `EventHandlerList` pattern (WinForms): `System.ComponentModel.EventHandlerList`
   stores delegates in a keyed collection, not as named fields. Zero detection
   today; WinForms `Control` subclasses use this exclusively. The entire WinForms
   subscriber population is invisible.

2. Weak event patterns (`WeakEventManager`, `ConditionalWeakTable`-backed patterns)
   are not distinguished from hard leaks. Reporting a weak event as a leak is a
   false positive. No detection or suppression exists.

3. Timer events: `System.Timers.Timer.Elapsed`, `System.Windows.Forms.Timer.Tick`,
   `DispatcherTimer.Tick` — these are among the most commonly leaked events in
   production .NET and share the same delegate-field structure. No specialized
   detection or surfacing.

4. `INotifyPropertyChanged.PropertyChanged` — highest-frequency MVVM event, most
   commonly retained across WPF/Blazor applications. Covered generically but not
   ranked or called out separately.

5. The `IncludeNonLeakingEvents = false` default means zero-subscriber events are
   silently omitted. There is no "clean events" summary confirming which publisher
   types were checked and found clean — an engineer cannot distinguish "clean" from
   "not scanned".

### Unexpected Functionality

- `GroupEventLeaks` (`internal`) is exercised by tests but dead in all production
  paths — the production accumulator-based grouping in `FindEventLeaks` replaces it.
- `EnumerateEventEntries` is defined but never called — unconditional dead code.
- `GetEventSubscribers` (instance field path with `ClrObject` construction) is never
  called; all callers use `EventLeakFastScanner`.
- Twelve `Console.Error.WriteLine("[PERF] ...")` statements scattered across the
  production class — perf-investigation artifacts not gated on `EnableDiagnostics`.

### Expansion Opportunities

- **[Evolution]** Subscription inventory mode: scan ALL delegate fields
  (`IncludeNonLeakingEvents = true`) and emit a full event registration graph,
  enabling "which types subscribe to what" queries independent of leak detection.
- **[Evolution]** Weak event detection as a companion analyzer or classification flag.
- **[Evolution]** `EventHandlerList` extractor for WinForms coverage.
- **[Improvement]** Promote timer-event findings to their own named category.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Publisher type + event field shown consistently; engineers can map directly to source.
- Per-instance `PublisherAddress` enables `!do <addr>` in WinDbg without further lookup.
- Subscriber type breakdown (with method name) identifies which component is the
  subscriber — the most actionable piece of information in most investigations.
- Root hint and full root path evidence allow answering "what keeps this publisher alive".
- Lifetime mismatch flag surfaces cross-generational retention (Gen2 publisher, Gen0/1
  subscribers) — rare but high-confidence signal when triggered.
- Duplicate subscription count catches double-registration bugs directly.
- Publisher generation distribution table in the section builder.

### Weaknesses

1. **Retained bytes estimate is silently wrong for large groups.**
   `EstimateGroupRetainedBytes` iterates only `g.Instances`, which is capped at
   `TopDetailedInstancesPerGroup` (default 5). A group with 10,000 publisher instances
   reports retained bytes from at most 5 of them. No warning or caveat is emitted. The
   key metric `estimated_retained_bytes` in the report is therefore misleading.

2. **No total retained bytes metric in summary.**
   `EventLeakDomainResult` and `EventLeakSectionBuilder.keyMetrics` have no aggregate
   "total estimated heap retained by event leaks" value. For SRE triage — deciding
   whether this is worth investigating — the total byte impact is the first question.

3. **Report caps at 10 groups / 10 instances.**
   `MaxGroupsToShow = 10`, `MaxInstancesToShow = 10` in the section builder. For
   applications with hundreds of leaking event types (common in large WPF apps), 90% of
   findings are invisible. No truncation warning is emitted for instances.

4. **Orphaned subscriber count is inflated by design.**
   `CountOrphanedSubscribers` marks any subscriber whose address is absent from
   `rootHints` as orphaned. `rootHints` contains only direct GC root objects, not
   transitively reachable live objects. Almost every subscriber will appear orphaned,
   making the signal indistinguishable from noise.

5. **`RootHint` is a raw ClrMD root kind string** (e.g. `"LocalVar"`, `"StaticVar"`).
   Not translated to human language. An engineer unfamiliar with ClrMD terminology gets
   no useful guidance.

6. **Evidence is built only for `TopLeakInstances`** — group snapshots carry no
   evidence block. A group with 10,000 instances where the stored 5 all have empty root
   hints shows `rootPath: null` for evidence even if the publisher type itself is a
   well-known static.

7. **`SubscriberTypes` in `EventLeakInstanceSnapshot` is pre-formatted strings**
   (`"App.MyType (3)"`). Not structured data. Downstream code cannot sort or filter by
   count without re-parsing the string.

8. **Static leaks show `PublisherAddress = 0x0`** in the report. Confusing; should be
   omitted or replaced with the literal `"(static)"`.

9. **No recommendation text per finding card.** The section builder produces
   `EventLeakGroupCard` / `EventLeakInstanceCard` but neither contains a remediation
   suggestion. The finding generator does include a generic recommendation but it is the
   same for every finding.

10. **`PublisherGeneration = -1` shown as `"-"`** — the section builder renders this but
    it will appear for static publishers and for dumps where generation metadata is
    unavailable. Should read `"static"` or `"unknown"` rather than a bare dash.

### Missing Statistics

- Total estimated retained bytes (aggregate)
- Subscription churn rate (available only via trend diff, not per-snapshot)
- Count of unique subscriber types across all leak groups
- Maximum delegate chain depth observed

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD — Well Used

- `IMemoryReader.ReadPointer` for hot-path field reads — correct, minimal allocation.
- Proactive `DiscoverDelegateLayoutFromModules` in constructor avoids layout-unknown
  state during scan; hardcoded .NET 6+ fallback provides resilience.
- Interior-offset correction (`field.Offset + _ptrSize`) applied consistently in
  `BuildFieldLayouts` and `TryDiscoverDelegateLayout`.
- `ClrArray.GetObjectValue()` used for multicast delegate array traversal — correctly
  defers to ClrMD for authoritative array length rather than reading raw `_invocationCount`.
- `runtime.GetMethodByInstructionPointer` with per-scanner instruction-pointer cache —
  avoids repeated DAC calls for the common case where many publishers share the same
  handler.
- `SegmentKindMapper.ResolveGeneration` for generation lookup during instance field scan.

### ClrMD — Gaps and Misuse

1. **`SweepModuleStaticFields` uses LINQ in a scanning loop.**
   `module.EnumerateTypeDefToMethodTableMap().Select(...).Where(t => t is not null)`
   allocates an iterator chain per module per domain. Contradicts the codebase's
   explicit prohibition on LINQ in hot paths. Replace with a foreach with inline null
   check.

2. **`_eventNameCache` is a `static ConcurrentDictionary`** on `EventLeakAnalyzer`.
   It is never cleared. In a server hosting multiple dump-analysis sessions, the cache
   accumulates stale type metadata across different dumps. `ConcurrentDictionary` also
   adds unnecessary synchronization overhead — the analyzer runs single-threaded.
   Should be instance-scoped or keyed by `(MT, dumpId)`.

3. **`CountIncomingRefs` enumerates the full heap with `EnumerateReferences(carefully: true)`
   per subscriber.** Disabled by default, but the option exists and is operator-settable.
   Even with `maxScan = 500`, the combination of per-subscriber invocation and per-object
   reference enumeration is O(N×M) — catastrophic on 10 GB+ dumps. No documentation
   warning exists on `EnableLowIncomingRefsCheck` beyond a brief comment.

4. **No cancellation propagation into `SinglePassScan` or `SweepModuleStaticFields`.**
   `CancellationToken` is checked once at the top of `AnalyzeAsync`, then abandoned.
   A cancellation request will not interrupt in-progress heap traversal.

5. **`ExtractSubscribersDirect` falls back to `heap.GetObject` for multicast arrays.**
   Comment correctly explains why raw MT lookup is unreliable. However, for the
   single-target path, the code correctly stays within `ReadPointer`. The mixed mode
   (ReadPointer for fields, GetObject for arrays) means multicast events are slightly
   more expensive than single-cast.

### Infrastructure — Well Used

- `IHeapIndexScanParticipant` — avoids second heap traversal in the index path.
- `HeapAnalysisCache.GetOrBuildValidRoots` — correct cache reuse for root map.
- `RootPathFinder` with limits (`MaxCandidateNodes = 5000`, `MaxCandidateDepth = 8`) —
  bounded BFS prevents runaway searches.
- `TypeFilterHelper` shared helpers used consistently for delegate, system, and
  compiler-generated type checks.

### Infrastructure — Gaps

- **`BuildTypeSizeMap` may trigger a full O(N) heap scan** if
  `HeapAnalysisCache.GetOrBuildTypeStatistics` has not been warmed by an earlier
  analyzer. This is an unguarded dependency on execution order; no documentation or
  ordering constraint enforces it.
- **The `_participantBuf` list** allocated in `BeforeHeapIndexScan` is passed to
  `ScanEntry` but is a single shared list reused across entries (cleared per field).
  This is correct for single-threaded scan but would be unsafe if the dispatcher ever
  went parallel.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

Listed in priority order.

### High Value

1. **Total estimated retained bytes in the summary key metric.**
   An SRE's first question is always "how much memory is this costing?" Currently
   absent from `EventLeakDomainResult` and the section header.
   *Difficulty: Low. Impact: High.*

2. **Cross-subscriber-type correlation.**
   If type `App.MyViewModel` appears as a subscriber across 50 different publisher
   events, `MyViewModel` instances are the retention problem — not any single event.
   A top-subscriber-types table across ALL groups would surface this pattern immediately.
   Currently only per-group subscriber type breakdown exists.
   *Difficulty: Low. Impact: High.*

3. **Top subscriber methods across all leaking events.**
   Which handler methods appear most frequently? A single lambda or method subscribed
   to many publishers (factory pattern, framework wiring) is trivially identified this
   way.
   *Difficulty: Low. Impact: High.*

4. **Publisher retention chain (not just subscriber root path).**
   `PopulateEvidence` builds a root path from the *publisher address*. The root kind
   hint tells why the publisher is alive — but the evidence `rootPath` field in the
   instance card conflates both sources (subscriber hint and publisher root path).
   Separate "publisher root path" and "sample subscriber root path" would make it
   actionable.
   *Difficulty: Medium. Impact: High.*

5. **`EventHandlerList` detection.**
   `Control.Events` in WinForms stores delegate chains in a keyed dictionary — never
   exposed as named fields. A specialized extractor reading `EventHandlerList` entries
   via known field names would cover all WinForms components.
   *Difficulty: Medium. Impact: High for WinForms applications.*

### Medium Value

6. **IDisposable subscriber detection.**
   Flag subscriber types that implement `IDisposable`. A `Dispose`d subscriber
   remaining in an event chain is a distinct leak pattern (object is disposed but not
   GC'd because of the event reference). Requires one `ClrType.Interfaces` check per
   unique subscriber MT — inexpensive.
   *Difficulty: Low. Impact: Medium.*

7. **Weak event detection / classification.**
   Detect `WeakReference<T>` or `ConditionalWeakTable`-backed delegate chains and
   emit them as informational rather than leaks. Avoids false positives on applications
   using the weak event pattern deliberately.
   *Difficulty: Medium. Impact: Medium.*

8. **Subscription count histogram.**
   Distribution of subscriber counts per publisher instance (e.g. 80% have 1–2
   subscribers, 5% have >50). Helps distinguish "one giant leaking publisher" from
   "many small leaks adding up".
   *Difficulty: Low. Impact: Medium.*

9. **Timer event specialization.**
   `System.Timers.Timer`, `DispatcherTimer`, `System.Windows.Forms.Timer` — tag their
   `Elapsed` / `Tick` event findings with "timer-based leak" category. Timers that are
   not stopped/disposed retain their subscriber chains indefinitely; this is the #1
   source of process-lifetime leaks.
   *Difficulty: Low. Impact: Medium.*

### Lower Value

10. **Maximum delegate chain depth.**
    The deepest observed `_invocationList` array length; a proxy for "how much time
    does each event fire take". Informational.

11. **Subscriber-to-publisher ratio per group.**
    Already implied by `AverageSubscribers` but could be surfaced more explicitly.

12. **`INotifyPropertyChanged` specialized ranking.**
    Detect `PropertyChanged` fields specifically and bucket them separately for MVVM
    applications.

---

## Audit Area 5 — Performance, Memory & Scalability

### Scalability Assessment (1 GB – 100 GB)

The fast path (disk-backed index + `IHeapIndexScanParticipant`) is well-designed:
one `ReadPointer` per field per object, MT index built lazily. At 100M objects with
an average 1.2 event fields per publisher type, this is ~120M pointer reads —
approximately 2–4 seconds on modern NVMe, negligible on RAM.

The bottleneck is not the scan; it is the ancillary steps executed post-scan.

### Performance Issues

1. **`BuildRootHintMap` called twice on non-participant path.**
   `BeforeHeapIndexScan` calls `BuildRootHintMap` and stores result in
   `_participantRootHints`. If the participant scan is then skipped (no disk index),
   `FindEventLeaks` calls `BuildRootHintMap` again from scratch. The second call
   enumerates all GC roots redundantly. For a large dump this is a ~500 ms–1 s
   wasteful call.

2. **`BuildTypeSizeMap` triggers `GetOrBuildTypeStatistics` on demand.**
   Called unconditionally after `FindEventLeaks` whenever at least one group is found.
   If the type statistics cache was not warmed by an earlier analyzer, this triggers a
   full O(N) heap scan to aggregate object sizes by type. On a 25 GB dump with 87M
   objects this is ~30–60 seconds of additional work not attributed to the event leak
   scan.

3. **`SweepModuleStaticFields` LINQ chain.**
   `module.EnumerateTypeDefToMethodTableMap().Select(pair => heap.GetTypeByMethodTable(pair.MethodTable)).Where(t => t is not null)`
   allocates iterator objects per module per domain. On a dump with hundreds of modules
   and thousands of types per module this creates significant GC pressure. Replace with
   explicit foreach.

4. **`EstimateGroupRetainedBytes` operates on stored TopInstances only.**
   Silent undercount for groups with many instances. The computation is also O(G × I × S)
   where G = groups, I = stored instances per group, S = subscribers per instance — all
   small, so not a performance issue, but produces incorrect output silently.

5. **No cancellation in hot paths.**
   `SinglePassScan` and `SweepModuleStaticFields` iterate to completion regardless of
   `CancellationToken` state. On a 100 GB dump a cancellation request could wait
   minutes for the scan to finish.

6. **`_eventNameCache` is never evicted.**
   In a long-running service that analyzes many dumps sequentially, the static
   `ConcurrentDictionary` grows without bound. Each unique `MethodTable` adds one
   entry; with millions of unique types across sessions this becomes a memory leak.

7. **`PopulateEvidence` runs `RootPathFinder` serially per instance.**
   Evidence is built for ALL `TopLeakInstances`, not just the top-K most severe.
   `RootPathFinder` with `MaxCandidateNodes = 5000` can be expensive per call.
   On a report with 200+ instances this adds minutes. Should be bounded (e.g. top 20
   instances by severity) or parallelised with appropriate thread-safety guards.

8. **`topLeakInstances` construction iterates ALL groups × ALL stored instances.**
   Then sorts. For 5000 groups × 5 instances each = 25,000 snapshot objects allocated
   and sorted. Allocation pressure at end of analysis is high, though not critical.

### Memory Issues

- All `EventLeakInfo` objects for stored instances are kept alive until the
  `EventLeakDomainResult` is consumed by the report builder — then GC'd. Peak memory
  for a large dump with 50,000 leaking groups × 5 instances × average 20 subscribers
  = ~5M `SubscriberInfo` objects. Acceptable but worth documenting.
- `_mtIndex` in `EventLeakFastScanner` stores `DelegateFieldLayout[]?` per MT. For a
  dump with 500k unique MTs, this is 500k dictionary entries. 99% map to `null` (no
  delegate fields). Consider a `HashSet<ulong>` for the no-match set to reduce entry
  size.

---

## Audit Area 6 — Correctness & Confidence

### Risks

1. **`LooksLikeEventFieldName` underscore prefix rule is overly broad.**
   Any private delegate-typed field starting with `_` is accepted. While the
   `IsDelegateType` gate reduces noise, callback and factory fields named `_onComplete`,
   `_factory`, `_callback` will be treated as event backing fields. The underscore rule
   was added for explicit backing-field patterns (`_myEvent` for `event MyEvent`) but
   captures far more than intended. No easy fix without a more precise pattern (e.g.
   require that a matching `add_` / `remove_` pair exists when the field starts with `_`
   and the type declares no events).

2. **`CountOrphanedSubscribers` definition is incorrect.**
   Marks a subscriber as "orphaned" if its address is absent from `rootHints`. But
   `rootHints` contains only the addresses of direct GC root objects (stack vars, statics,
   handles). The vast majority of live objects are NOT GC roots — they are reachable
   transitively. This means nearly every subscriber will be flagged as orphaned,
   including fully live, healthy subscribers. The metric as defined produces misleading
   counts and should either be removed or re-implemented using the reverse reference
   index.

3. **`EstimateGroupRetainedBytes` is documented nowhere as an approximation.**
   The section builder column is labeled `"Est. Retained"` without any indication that
   only TopN instances contribute. For a group with `InstanceCount = 10,000` and
   `TopDetailedInstancesPerGroup = 5`, the reported estimate is 0.05% of the true
   value with no caveat.

4. **Severity score step-function discontinuity.**
   `CalculateSeverity` adds `SeveritySubscriberBonus = 5` when
   `subscriberCount >= SeveritySubscriberThreshold = 10`. A publisher with 10
   subscribers gets score 15; one with 9 gets score 9. A 1-subscriber difference
   produces a 67% score difference. This affects severity classification
   (`Critical >= 35`, `Warning >= 20`) when the bonus pushes a group across a threshold.
   No engineering justification for a step vs. continuous scale.

5. **`_participantScanSucceeded` guard is insufficient.**
   `_participantScanSucceeded = true` is set in `OnHeapIndexScanCompleted(succeeded: true)`.
   However, `succeeded` is set by the dispatcher — if the dispatcher marks the scan
   succeeded but the `OnHeapEntry` callback threw on some entries (silently swallowed
   in caller error handling), the partial accumulator will be used as if complete.

6. **Static-only type coverage via `SweepModuleStaticFields` may miss dynamic assemblies.**
   `module.EnumerateTypeDefToMethodTableMap()` only covers types with a TypeDef token
   (statically loaded assemblies). Types emitted via `Reflection.Emit` or
   `AssemblyLoadContext` dynamic assemblies may not appear. Edge case for most apps,
   but common in plugin architectures.

7. **`GetEventNames` empty-set means "all-pass"** — this convention is documented in
   code comments but is the opposite of what an empty allow-list normally means. A type
   with `ownEvents.Count == 0` accepts any field matching `LooksLikeEventFieldName`.
   This increases false-positive risk for types that have delegate callback fields but
   no events.

8. **Delegate layout fallback offsets are hardcoded for .NET 6+ 64-bit.**
   The fallback in `TryDiscoverDelegateLayout` uses `_ptrSize` multipliers derived from
   the .NET 6+ CoreCLR source. These offsets differ in .NET Framework 4.x (32-bit and
   64-bit). Analyzing a .NET Framework 4.x dump on a 64-bit host with incomplete symbols
   will silently produce incorrect delegate target reads without any error signal.

### False Positive Risks

- Non-event delegate callbacks (`_action`, `_selector`, `_predicate` patterns) captured
  by the `_` prefix heuristic.
- Weak-event-pattern types reported as leaks.
- Multi-AppDomain dumps where the same static event is correctly populated in all domains
  (legitimate design, not a leak).

### False Negative Risks

- WinForms `EventHandlerList` pattern — completely missed.
- Dynamic assembly types — missed in `SweepModuleStaticFields`.
- Events backed by `volatile` field patterns or manual `Interlocked.CompareExchange`
  implementations — field shape differs from standard C# event backing field.

---

## Audit Area 7 — Industry Benchmark

### vs. WinDbg + SOS

| Capability | SOS/WinDbg | EventLeakAnalyzer |
|---|---|---|
| Delegate chain walkthrough | `!dumpdelegate` — detailed | Not provided as raw dump |
| Object retention path | `!gcroot` — full chain | `RootPathFinder` BFS — bounded depth |
| Semantic grouping by publisher+field | Manual | Automatic |
| Subscriber type ranking | Manual | Automatic |
| Severity scoring | None | Composite score |
| Static vs instance classification | Manual | Automatic |
| Lifetime mismatch heuristic | None | Present |
| Retained bytes accuracy | Exact (via `!objsize`) | Approximate (type-average based) |

**Gap:** `!dumpdelegate <addr>` in SOS shows the full invocation list with target types
and method names without any filtering. EventLeakAnalyzer's subscriber detail rows cover
this but only for stored TopN instances, and the details are not directly dumpable for
arbitrary addresses.

### vs. PerfView

PerfView's GC Heap Stacks shows full object retention paths with sizes aggregated by
type and can identify event subscription trees via ref-path patterns. EventLeakAnalyzer's
retained size estimate is heap-average-based; PerfView's sizes are exact retained sizes
from dominator trees.

**Gap:** EventLeakAnalyzer has no dominator-tree integration. Accurate retained bytes per
event group would require `DominatorAnalyzer` output to be cross-referenced — an
architectural enhancement.

**Gap:** PerfView allows user-defined grouping patterns by namespace, module, and type
name. EventLeakAnalyzer groups are fixed to publisher type + event field.

### vs. JetBrains dotMemory

dotMemory has an explicit **"Event Handlers"** view that:
- Shows all delegate chains found in the heap
- Provides exact retained size per subscription
- Supports snapshot diffing for growth detection

**Gap:** Retained size accuracy. dotMemory computes retained size via dominators;
EventLeakAnalyzer uses type-average size, which may be off by 10×–100× for types with
deep object graphs.

**Gap:** dotMemory's "new in snapshot" classification for subscriptions added between two
snapshots. EventLeakAnalyzer's trend comparer tracks totals but not which specific
subscriptions are new.

**Advantage over all tools:** EventLeakAnalyzer's severity scoring, duplicate subscription
detection, lifetime mismatch heuristic, and cross-analyzer evidence integration are not
present in any of the benchmarked tools. The `IHeapIndexScanParticipant` integration for
single-pass heap traversal is a significant architectural advantage for large dump performance.

---

## Final Executive Summary

### Overall Assessment

**Score: 68 / 100**

**Production readiness:** Conditionally ready. Fast path is production-grade. Several
correctness issues (orphaned-subscriber metric, retained-bytes accuracy, delegate layout
fallback for .NET Framework) and one misleading metric (`CountOrphanedSubscribers`) should
be addressed before the output is used to make rollback or incident-response decisions.

**Major strengths:**
- `EventLeakFastScanner` hot path: single `ReadPointer` per field, O(1) per entry.
- `IHeapIndexScanParticipant` integration eliminates redundant heap traversal.
- Comprehensive report model: groups, instances, subscriber types, method names, heuristics.
- Accurate multi-domain static subscriber counting (explicit fix documented in comments).

**Major weaknesses:**
- `CountOrphanedSubscribers` is incorrectly defined — nearly all subscribers appear
  orphaned, signal is noise.
- `EstimateGroupRetainedBytes` silently uses only TopN instances — report column is
  misleading for large groups.
- No total retained bytes in summary — the most actionable SRE metric is absent.
- Static `_eventNameCache` never cleared — memory leak in long-running services.
- No cancellation in hot scan loops.
- `Console.Error.WriteLine` perf logging in production code.

---

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P0-1 | Fix `CountOrphanedSubscribers` — currently marks all non-root-direct objects as orphaned; should use reverse reference index or be removed | Eliminates high-noise false-positive metric from all reports | Medium | High | Improvement |
| P0-2 | Fix `EstimateGroupRetainedBytes` — use `acc.TotalSubscribers × avgSize` instead of iterating capped TopInstances; add "estimate covers TopN only" caveat to report | Makes retained-bytes column truthful | Low | High | Improvement |
| P0-3 | Remove or gate `Console.Error.WriteLine` perf logging behind `EnableDiagnostics` option | Eliminates noise in production output | Low | High | Improvement |

#### P1 — High

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P1-1 | Add total estimated retained bytes to `EventLeakDomainResult` and section key metrics | Provides the single most actionable SRE metric | Low | High | Improvement |
| P1-2 | Clear or scope `_eventNameCache` per dump session (remove `static`, make instance field or key by dump identifier) | Prevents memory accumulation and stale data in long-running services | Low | High | Improvement |
| P1-3 | Add `CancellationToken` checks inside `SinglePassScan` and `SweepModuleStaticFields` (every ~10k iterations) | Enables responsive cancellation on large dumps | Low | High | Improvement |
| P1-4 | Replace LINQ chain in `SweepModuleStaticFields` with explicit foreach | Eliminates GC pressure in type-scan loop, consistent with codebase conventions | Low | High | Improvement |
| P1-5 | Document `EnableLowIncomingRefsCheck` as "catastrophic on large heaps" and add a runtime guard preventing it on dumps > configurable size threshold | Prevents accidental O(N×M) scan | Low | High | Improvement |
| P1-6 | Bound `PopulateEvidence` to top-K instances by severity (e.g. 20) | Prevents multi-minute evidence phase on large reports | Low | High | Improvement |

#### P2 — Medium

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P2-1 | Add cross-subscriber-type correlation table to result: top subscriber types aggregated across ALL event groups | Surfaces the "one type subscribing to everything" pattern | Low | High | Improvement |
| P2-2 | Add "top handler methods across all leaking events" to result | Identifies factory/wiring methods responsible for bulk subscriptions | Low | High | Improvement |
| P2-3 | Separate `EstimateGroupRetainedBytes` from stored-instance limitation by computing from `acc.TotalSubscribers × avgSubscriberSize` | Accurate aggregate retained estimate without requiring stored instances | Low | High | Improvement |
| P2-4 | Translate `RootHint` to human-readable strings in section builder (e.g. `"LocalVar"` → `"local variable"`, `"StaticVar"` → `"static field"`) | Reduces interpretation burden on engineers | Low | Medium | Improvement |
| P2-5 | Suppress `PublisherAddress = 0x0` for static leaks in section builder; render as `"(static)"` | Eliminates misleading addresses in report | Low | High | Improvement |
| P2-6 | Add `IDisposable` subscriber detection: flag subscriber types implementing `IDisposable` | Identifies disposed-but-not-unsubscribed pattern | Low | Medium | Improvement |
| P2-7 | Remove dead code: `GroupEventLeaks` (production dead), `EnumerateEventEntries` (never called), `GetEventSubscribers` (never called) | Reduces maintenance surface | Low | High | Improvement |
| P2-8 | Fix delegate layout fallback for .NET Framework 4.x — add framework version detection or document the 32-bit offset difference | Prevents silent wrong reads on Framework dumps | Medium | Medium | Improvement |

#### P3 — Low

| # | Recommendation | Expected Impact | Difficulty | Confidence | Class |
|---|---|---|---|---|---|
| P3-1 | `EventHandlerList` extractor for WinForms `Control.Events` coverage | Covers WinForms application class entirely | High | Medium | Evolution |
| P3-2 | Weak event pattern detection / classification (WeakEventManager, CWT) | Eliminates false positives for apps using weak events deliberately | Medium | Medium | Evolution |
| P3-3 | Timer event specialization: tag `Elapsed`/`Tick` findings separately | Adds high-signal category for the most common process-lifetime leak | Low | High | Improvement |
| P3-4 | Subscription count histogram in domain result | Adds distribution insight for triage | Low | Medium | Improvement |
| P3-5 | Raise `MaxGroupsToShow` / `MaxInstancesToShow` in section builder, or make configurable | Exposes findings currently invisible in reports | Low | High | Improvement |
| P3-6 | Replace severity score step-function with continuous scale for smoother severity distribution | Improves finding ranking accuracy | Low | Medium | Improvement |
| P3-7 | Integrate with `DominatorAnalyzer` for accurate per-event retained bytes | Closes accuracy gap vs. dotMemory | High | Low | Evolution |

---

### Final Verdict

1. **Production-ready?** Yes for the fast-path scan and finding detection. Not
   recommended for retained-byte reporting or orphaned-subscriber metrics until P0-1
   and P0-2 are addressed.

2. **Highest-impact improvements:** P0-1 (orphaned count correctness), P0-2 (retained
   bytes accuracy), P1-1 (total retained bytes in summary), P1-4 (LINQ removal in sweep).

3. **Platform evolution opportunities:** `EventHandlerList` extractor (WinForms
   coverage), dominator-backed retained size, subscription inventory mode.

4. **Highest engineering return (effort vs. value):** P1-2 (fix static cache), P1-3
   (cancellation), P1-6 (bound evidence phase), P2-5 (static address rendering) — all
   are one-line or single-method changes with immediate production quality improvement.

---

## Redesign from Scratch

> What would a ground-up rewrite look like, given the project's hard constraints:
> streaming-only heap traversal, no materialization, bounded memory, disk-backed indices,
> single allocation budget on the hot path?

The current implementation accreted incrementally — the fast scanner was added on top of
a ClrMD-first design, the participant pattern was bolted on later, orphaned-subscriber and
retained-bytes estimates were added without correcting their conceptual foundations. A clean
design starts from the data model and works outward.

---

### 1. Separate the Three Jobs That Are Currently Mixed Together

The analyzer currently conflates three fundamentally different responsibilities inside a
single class and scan loop:

- **A. Field discovery** — which types have event backing fields? (metadata, done once)
- **B. Subscription counting** — for each publisher instance, how many subscribers does each
  field hold? (hot scan, runs on every object)
- **C. Evidence enrichment** — where is this publisher retained? who are the subscribers?
  (expensive, runs post-scan on a small candidate set)

In the current implementation all three interleave. The fast scanner does B and initiates
part of C (root hints, subscriber type resolution). Evidence is then enriched again in
`PopulateEvidence`. The result is that the cost of C is paid per-object instead of per-candidate.

**Redesign: strict three-phase pipeline.**

```
Phase A  (pre-scan, once)   →  EventFieldRegistry       (MT → FieldDescriptor[])
Phase B  (heap scan, hot)   →  GroupCounterTable         (GroupKey → RawGroupCounter)
Phase C  (post-scan, top-K) →  EvidenceEnricher          (top-K candidates only)
```

Each phase has a clear interface contract and no knowledge of the others.

---

### 2. Ground the Group Key in Numbers, Not Strings

The current group key is `(string PublisherType, string EventFieldName, bool IsStatic)`.
This means every publisher object encountered on the hot path either looks up or allocates
two strings. On a heap with 500k publisher instances the string interning cost is non-trivial.

**Redesign: key by `(ulong PublisherMT, int FieldOffset, bool IsStatic)`.**

These are the exact values available from the pre-built `EventFieldRegistry` without any
string access. All human-readable names (type name, field name) are resolved lazily in
Phase C, after candidates are selected by count and severity.

```csharp
internal readonly struct GroupKey : IEquatable<GroupKey>
{
    public readonly ulong PublisherMT;
    public readonly int   FieldOffset;   // uniquely identifies the field within the type
    public readonly bool  IsStatic;
}
```

The registry maps `GroupKey → (string TypeName, string FieldName)` and is only consulted
when building the final result — never during the scan.

---

### 3. Replace GroupAccumulator with a Fixed-Size Value-Type Counter

`GroupAccumulator` is a class with `List<EventLeakInfo> TopInstances` — a heap-allocated
linked structure per group. The top-instance replacement logic (linear scan for minimum)
runs per publisher object on the hot path.

**Redesign: split into two tiers.**

**Tier 1 — hot-path counter (value type, fixed size):**
```csharp
internal struct RawGroupCounter
{
    public int  InstanceCount;
    public int  TotalSubscribers;
    public int  MinSubscribers;
    public int  MaxSubscribers;
    public ulong TotalSubscriberMTSum;   // for avg-size estimate without type lookup
}
```

No `List<>`, no strings, no allocation. Updated via `Interlocked` ops if parallelism
is ever introduced.

**Tier 2 — detail capture (reference type, allocated only for top-K candidates):**

After Phase B completes, select the top-K groups by `TotalSubscribers`. Only then
allocate `EventLeakGroup` objects with full subscriber detail. The detail pass re-reads
the heap for only those groups — a tiny fraction of the full scan.

This eliminates the current min-heap replacement logic from the hot path entirely.

---

### 4. A Single, Honest Retained-Bytes Strategy

Two options; pick one and be explicit about it.

**Option A — type-average estimate, clearly labelled.**
Compute `TotalSubscribers × avgSubscriberSizeByMT` across ALL instances (not just TopN).
The `avgSubscriberSizeByMT` comes from the pre-built type statistics cache. Label the
metric `"Estimated (type-average, all instances)"` in the report. Never suggest it is
accurate.

**Option B — dominator-backed, opt-in.**
If `DominatorAnalyzer` has run and its result is present in the `AnalysisContext`,
look up each unique subscriber MT in the dominator index and use its average
dominated-set size. Much closer to the real retained cost. Label as
`"Estimated (dominator-average)"`. Fall back to Option A silently when dominators are
unavailable.

The current design uses Option A but applies it only to stored TopN instances and calls
the result `"Est. Retained"` without caveat. That is worse than either option.

---

### 5. Fix the Orphaned-Subscriber Model or Drop It

The `CountOrphanedSubscribers` definition — "subscriber address not in rootHints" — is
wrong. Nearly every live object fails this test because `rootHints` contains only
direct GC root objects.

A correct "orphaned subscriber" means a subscriber whose only retention path runs
through the delegate chain — i.e., if the event were unwired, the subscriber would
become unreachable. That requires a reverse reference check, not a root-presence check.

**Redesign option A (correct, expensive):** After selecting top-K candidate groups,
use the existing `ReverseReferenceIndex` (if available) to count incoming references to
each subscriber address from outside the delegate chain. A subscriber with zero
non-delegate inbound references is genuinely orphaned.

**Redesign option B (correct, cheap heuristic):** A subscriber is "likely orphaned" if:
- It is NOT in the root map AND
- Its type is NOT a known long-lived type (services, singletons, registered DI components).

This is still a heuristic but it excludes the 99% of live objects that are correctly
retained transitively.

**Redesign option C (honest):** Remove the metric. Replace with "subscriber types not
seen as static roots" — a much narrower claim that is defensible.

---

### 6. Static Field Handling in One Pass

Currently static fields are processed twice: once per heap instance of the type
(via `ProcessPublisherEntry`) and once via `SweepModuleStaticFields` for instance-free
types. The deduplication using `processedStaticMTs` works but the two-pass structure
is fragile — if the order of operations changes, deduplication can break.

**Redesign: one static sweep, unconditionally after the heap scan.**

Phase A builds the `EventFieldRegistry` from all module types (including instance-free
ones). At the end of Phase B, iterate every MT in the registry that has static event
fields — regardless of whether heap instances were seen. This is a single foreach over
the registry's static-publisher entries:

```
foreach MT in registry.StaticPublisherMTs:
    if already processed: continue
    read static delegate fields
    accumulate into GroupCounterTable
```

No `processedStaticMTs` hash set needed during the heap scan. The scan only touches
instance fields.

---

### 7. Instance-Scoped Caches With Explicit Lifetimes

The current implementation has two scope-leaking caches:

- `static ConcurrentDictionary<ulong, HashSet<string>> _eventNameCache` on
  `EventLeakAnalyzer` — lives for the process lifetime.
- `_mtIndex` inside `EventLeakFastScanner` — scoped to the scanner instance (correct),
  but the scanner is created inside `BeforeHeapIndexScan` and inside `FindEventLeaks`,
  so its lifetime depends on call path.

**Redesign: all caches owned by `EventFieldRegistry`**, which is constructed once per
analysis context and disposed with it:

```csharp
internal sealed class EventFieldRegistry : IDisposable
{
    // MT → FieldDescriptor[] : built once, immutable after construction
    private readonly Dictionary<ulong, EventFieldDescriptor[]?> _byMT;
    // MT → (TypeName, FieldName) for lazy string resolution
    private readonly Dictionary<ulong, string> _typeNames;
    private readonly Dictionary<ulong, string> _fieldNames;
    // EventName sets per MT
    private readonly Dictionary<ulong, HashSet<string>> _eventNames;

    public void Dispose() { _byMT.Clear(); _typeNames.Clear(); ... }
}
```

The registry is built in Phase A, passed into Phase B (read-only), and disposed after
Phase C. No static state. No accumulation across dump sessions.

---

### 8. Cancellation as a First-Class Contract

Every phase accepts `CancellationToken` and checks it at a fixed granularity:

```csharp
private const int CancelCheckInterval = 10_000;  // objects

// inside the hot loop:
if ((++_scanned & (CancelCheckInterval - 1)) == 0)
    cancellationToken.ThrowIfCancellationRequested();
```

The bitwise mask check (`& mask`) avoids a modulo per iteration. This is one
branch per 10k objects — negligible, and it makes the analyzer responsive to
pipeline cancellation on large dumps.

---

### 9. EventHandlerList Support as a First-Class Code Path

WinForms components store delegate chains in `System.ComponentModel.EventHandlerList`
— a linked list of `(object key, Delegate handler)` pairs held in `Control.Events`.
This pattern is completely invisible to field-offset scanning.

**Redesign: `IEventExtractorStrategy` abstraction with two implementations.**

```csharp
internal interface IEventExtractorStrategy
{
    bool Matches(ClrType type);
    IEnumerable<(string FieldName, ulong DelegateAddr)> ExtractEvents(
        ulong objectAddress, IMemoryReader reader, int ptrSize);
}
```

- `DelegateFieldStrategy` — current logic, covers standard C# event backing fields.
- `EventHandlerListStrategy` — reads `Control.Events` linked list by walking the
  `System.ComponentModel.EventHandlerList` chain at known offsets.

Phase A registers all strategies; Phase B dispatches per type. Adding new patterns
(e.g. `Reactive.Subject` wrapper leaks, `DispatcherObject.Events`) is a one-class
addition.

---

### 10. Severity as a Continuous Score

The current formula is:

```
score = subscriberCount
      + (subscriberCount >= threshold ? bonus : 0)   ← step function
      + isStatic ? bonus : 0
      + ...
```

A publisher with 10 subscribers scores 15 (`10 + 5`). One with 9 scores 9. A 67% score
jump for 1 subscriber is not defensible.

**Redesign: log-scaled subscriber score + continuous bonuses.**

```csharp
double baseScore = Math.Log2(subscriberCount + 1) * 10.0;  // ~10 pts per doubling
double staticBonus    = isStatic          ?  8.0 : 0.0;
double gen2Bonus      = pubGen == 2       ?  5.0 : 0.0;
double dupBonus       = dupCount > 0      ? Math.Min(dupCount * 2.0, 10.0) : 0.0;
double mismatchBonus  = lifetimeMismatch  ?  6.0 : 0.0;
int    score = (int)Math.Round(baseScore + staticBonus + gen2Bonus + dupBonus + mismatchBonus);
```

| Subscribers | Old score | New score |
|---|---|---|
| 1 | 1 | 10 |
| 9 | 9 | 31 |
| 10 | 15 (+67%) | 33 (+6%) |
| 50 | 55 | 57 |
| 1000 | 1010 | 100 |

The new formula is capped at reasonable values (log₂(1000) × 10 ≈ 100), making
severity thresholds meaningful across the full range of subscriber counts.

---

### 11. What the Redesigned Class Surface Looks Like

```csharp
public sealed class EventLeakAnalyzer : IAnalyzer, IHeapIndexScanParticipant, IDisposable
{
    // Phase A: built once, immutable
    private EventFieldRegistry? _registry;

    // Phase B: hot-path state, value types only
    private GroupCounterTable? _counters;    // Dictionary<GroupKey, RawGroupCounter>

    // Phase B → C handoff
    private bool _scanCompleted;

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(AnalysisContext ctx)
    {
        _registry = EventFieldRegistry.Build(ctx.Heap, ctx.AnalysisOptions.EventLeak);
        _counters = new GroupCounterTable(capacity: 4096);
        _scanCompleted = false;
    }

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
        => _counters!.Accumulate(entry, _registry!);   // no allocations

    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded)
        => _scanCompleted = succeeded;

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context, CancellationToken cancellationToken)
    {
        // Use participant counters if the shared scan ran; otherwise run a fresh scan.
        GroupCounterTable counters = _scanCompleted && _counters is not null
            ? _counters
            : RunFreshScan(context, cancellationToken);

        // Static sweep (once, unconditional)
        counters.AccumulateStaticFields(_registry!, context.Heap, context.AnalysisOptions.EventLeak, cancellationToken);

        // Phase C: enrich top-K candidates
        var topK = counters.SelectTopK(context.AnalysisOptions.EventLeak.MaxGroupsToEnrich);
        var enriched = EvidenceEnricher.Enrich(topK, context, cancellationToken);

        return ValueTask.FromResult(BuildResult(counters, enriched).Stamp(this));
    }

    public void Dispose() { _registry?.Dispose(); _counters?.Dispose(); }
}
```

No static fields. No `Console.Error.WriteLine`. Cancellation threaded through every
phase. The hot path (`OnHeapEntry`) is a single method call with no allocation.

---

### Summary of Key Design Decisions

| Decision | Current | Redesigned |
|---|---|---|
| Group key type | `(string, string, bool)` | `(ulong MT, int offset, bool)` |
| Hot-path allocation | `EventLeakInfo`, `List<SubscriberInfo>` per publisher | Zero — only `RawGroupCounter` update |
| String resolution | During scan | Post-scan, top-K only |
| Type name cache scope | Static, process-lifetime | Instance, disposed with analyzer |
| Static field sweep | Two passes, interleaved | One pass, post-scan, registry-driven |
| OrphanedSubscribers | Incorrect (non-root = orphaned) | Reverse-ref check (opt-in) or removed |
| Retained bytes | TopN instances only, no caveat | All-instances aggregate, explicit label |
| Severity formula | Step function | Log-scaled, continuous |
| Cancellation | Entry point only | Every phase, every 10k iterations |
| EventHandlerList | Not detected | `IEventExtractorStrategy` plugin slot |
| WinForms coverage | Zero | `EventHandlerListStrategy` implementation |
| Dominator integration | None | Optional `DominatorAnalyzer` result lookup |
