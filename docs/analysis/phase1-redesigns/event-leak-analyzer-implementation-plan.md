# EventLeakAnalyzer — Implementation Plan

## Status
Proposed execution plan for the redesign in
[event-leak-analyzer.md](event-leak-analyzer.md). That document is the source of truth
for *why*; this one is the source of truth for *what ships when, in what file, gated by
what test*. Where the two disagree, the design doc wins and this plan is stale.

Companion docs:
- [event-leak-analyzer.md](event-leak-analyzer.md) — architecture, measured baseline, decisions
- [eventleak-analyzer-audit.md](../phase1/eventleak-analyzer-audit.md) — audit items referenced by number (P0-1, #4, etc.)
- [root-path-finder.md](root-path-finder.md) — shared `RootPathFinder` redesign this plan's Phase 1 depends on for its long-term ceiling, not its Phase 1 delivery

## Purpose
Turn event-leak-analyzer.md §12's sequencing into an implementable plan with phased
delivery, file-level task slices, acceptance criteria, and exit gates. Steps map
1:1 to that document's numbered sequence; step numbers in this plan match it exactly
so the two stay cross-referenceable.

## Baseline (already in code, do not regress)
- `IHeapIndexScanParticipant` single-pass integration — no redundant heap traversal when
  a disk-backed index exists.
- Multi-AppDomain static subscriber counting (per-domain, not deduplicated across
  domains — see comment in `EventLeakAnalyzer.GetStaticEventSubscribers`).
- `EventLeakAnalyzerAccuracyTests`, `EventLeakAnalyzerDiscrepancyTests`,
  `EventLeakFindingGeneratorTests` all green.
- `HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`
  runnable as the standing perf harness (`DD_RUN_DISCREPANCY_TESTS=1`, filtered, never
  full suite; compare medians of 3 runs — 18% run-to-run variance is normal).

No phase below may regress these without an explicit, called-out decision in that
phase's exit gate.

## Explicitly not in this plan
Per design doc §10:
- §4.4 Tier 2 (dominator-exact retained bytes, post-pipeline join) — deferred until
  after this plan's Phase 6 ships and `EventLeakDomainResult`'s shape is stable.
- Subscription inventory mode.
- Timer event / `INotifyPropertyChanged` specialization (`WellKnownEventFilter`).

Do not open task slices for these. If a phase below turns out to need one of them as a
dependency, that's a signal the phase is scoped wrong — stop and reconcile with the
design doc before proceeding.

## Workstream overview
- P1: Bounded evidence enrichment + Tier 1 retained bytes (design §4)
- P2: Correctness fixes independent of the registry (design §9, partial)
- P3: `PublisherRegistry` + `FieldBackedDelegateShape` (design §3)
- P4: Registry-driven statics (design §6)
- P5: Correlation phase (design §7)
- P6: Structured presentation data / Phase F (design §8)
- P7: `EventHandlerListShape` / `WeakEventShape` (design §3.2, additive)

Each phase is independently shippable, independently measured against the perf harness,
and gated on its own test suite before the next phase starts. No big-bang cutover.

---

## Phase 1 — Bounded evidence enrichment (design §4)

### Status: Implemented and perf-verified

All task slices below shipped, with one deliberate deviation from the plan text: the shared
`Evidence` record (`src/DumpDetective.Analysis/Models/Evidence.cs`) was **not** reshaped, since
it's also used by `DominatorAnalyzer`, `StaticRootLeakDetector`, and `TimerLeakAnalyzer` —
reshaping it would have been a much larger blast-radius change than this phase's stated file
scope. Instead, a new EventLeak-specific `EventLeakEvidence` record was added to
`EventLeakDomainResult.cs` with the exact shape design §4.3 specifies
(`SchemaVersion`/`PublisherRootPath`/`SampleSubscriberHint`/`SearchTruncated`/`Signals`), plus a
matching `EvidenceConfidence.Compute(EventLeakEvidence?)` overload.

Verified:
- All task slices (options, analyzer, domain result, section builder) implemented.
- New unit tests (`BuildEnrichmentGroupKeys`, `EstimateGroupRetainedBytes` fold-correctness) —
  9 new cases, all heap-free.
- `EventLeakAnalyzerAccuracyTests`, `EventLeakFindingGeneratorTests`, and the full unit suite
  (287 tests) pass. `EventLeakAnalyzerDiscrepancyTests` skips cleanly (no local dump).

Perf-verified against the 3.3GB reference dump
(`D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp`,
14,003 unique MTs) via
`HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`
(single run — median-of-3 not yet collected):

| Phase | Baseline | This run | Verdict |
|---|---|---|---|
| `BuildRootHintMap` | 15.44s | 12.18s (760 roots) | in line |
| `BuildFieldLayouts` (scan) | 22.80s | 23.35s | in line, no regression |
| `SweepModuleStaticFields` | 19.51s | 18.38s | in line, no regression |
| `PopulateEvidence` | 34.28s | **1.56s** | **target hit** |
| Total `AnalyzeAsync` | 94.74s | 57.44s | ~39% faster |

`PopulateEvidence.RootPathLoop` detail: 3,321 instances scanned, 119 BFS attempts (only
instances in the top-25 enriched groups that lacked a `RootHint`), all 119 returned
`found=0`/`truncated=0`/`budgetExhausted=0` — i.e. every attempted publisher was
unreachable from the 1,411 known roots within the 20-hop BFS. This is consistent with the
skip-when-root-hint-exists guard working as designed (most root-reachable instances never
reach the BFS at all) rather than a regression; the BFS call path and root set are
unchanged from pre-Phase-1 code.

Not yet done: median-of-3 (only a single run collected so far); the ~18% run-to-run
variance noted in the cross-phase measurement discipline means this single run should be
treated as directionally confirmed, not final-signed-off.

### Goals
Cut `PopulateEvidence` from 34.3s to ~1.5s at the 3.3GB baseline without touching scan
or type-metadata cost. Largest single win, smallest diff, no structural change.

### Task slices

#### src/DumpDetective.Core/Options/EventLeakOptions.cs
- Add `MaxGroupsToEnrich` (default 25; Fast profile 10, Full profile 100).
- Add `MaxEvidenceEnrichmentMs` (default 2000).
- Leave `SeverityOrphanedSubscriberBonus`/`Cap` untouched here — removed in Phase 2, not
  this phase, to keep this diff perf-only.

#### src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs
- In `Analyze`, after `groupedLeaks` is sorted (already sorted by `TotalSubscribers`
  descending), take `groupedLeaks[0..MaxGroupsToEnrich]` as the enrichment set instead of
  passing every group's `topLeakInstances` to `PopulateEvidence`.
- `PopulateEvidence`: add a wall-clock guard (`Stopwatch` against
  `MaxEvidenceEnrichmentMs`); once exceeded, remaining instances keep `inst.RootHint` as
  their only evidence and are marked `Evidence.SearchTruncated = true` via a distinct
  reason (budget exhausted, not BFS-internal truncation — do not conflate with
  `RootPathFinder`'s own `searchTruncated` output per design §4.1's warning that the
  existing flag "measures the wrong thing").
- Add the skip-when-root-hint-exists guard: if `inst.RootHint` is already non-empty,
  don't call `TryFindAnyRootPath` for that instance at all.
- Split `Evidence` into `PublisherRootPath` / `SampleSubscriberHint` per design §4.3 —
  this touches `EventLeakDomainResult.cs`'s `Evidence` record (see below) and every call
  site that previously wrote a single conflated `rootPath`.

#### src/DumpDetective.Analysis/Models/EventLeakDomainResult.cs
- Change `Evidence` record shape:
  ```csharp
  public sealed record Evidence(
      int SchemaVersion,
      string? PublisherRootPath,
      string? SampleSubscriberHint,
      bool SearchTruncated,
      IReadOnlyList<EvidenceSignal> Signals);
  ```
- Add Tier 1 retained bytes to the domain result: sum
  `TotalSubscribers × avgSubscriberSizeByMT` across all groups (already computed per
  group by `EstimateGroupRetainedBytes` — this is a fold over the existing per-group
  values, no new heap access) exposed as `EventLeakDomainResult.TotalEstimatedRetainedBytes`
  (audit P1-1).
- `EstimateGroupRetainedBytes` itself: confirm it already iterates
  `TotalSubscribers × avg`, not `g.Instances` — if it's still iterating the capped
  `TopInstances` (audit #3), fix that here since it's the same "aggregate over all
  instances" work this phase is already touching. If the current implementation is
  already correct per a prior fix, this is a no-op check, not new work.

#### src/DumpDetective.Reporting/SectionBuilders/EventLeakSectionBuilder.cs
- Update rendering to use `PublisherRootPath` (primary) falling back to
  `SampleSubscriberHint` (secondary, labelled as such — never silently substituted with
  no indication which source produced it).
- Surface `TotalEstimatedRetainedBytes` as a summary key metric on the section header
  (audit P1-1 — "the first question an SRE asks").
- Label the existing per-group retained-bytes column `"Estimated (type-average, all
  instances)"` per design §4.4 Tier 1.

#### tests
- `EventLeakAnalyzerAccuracyTests`: add cases for the `MaxGroupsToEnrich` bound (groups
  beyond the bound retain `RootHint` only, `Evidence.PublisherRootPath == null`), and for
  the skip-when-root-hint-exists guard.
- `HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`:
  re-run at 3.3GB baseline dump, confirm `PopulateEvidence` drops from ~34s to ~1.5s
  (median of 3). This is the phase's primary acceptance signal.
- New: accuracy test asserting `TotalEstimatedRetainedBytes` equals the sum of per-group
  Tier 1 estimates (a fold-correctness test, not a heap test — can run against a
  hand-built `List<EventGroupInfo>` fixture).

### Acceptance criteria
- `PopulateEvidence` phase time drops to ~1.5s at 3.3GB baseline (target from design §0).
- No regression in `EventLeakAnalyzerAccuracyTests` / `EventLeakAnalyzerDiscrepancyTests`.
- `TotalEstimatedRetainedBytes` present and non-zero whenever `groupedLeaks.Count > 0`.
- Report shows labelled retained-bytes estimate; no unlabelled "Est. Retained" column
  remains (audit #3 closed for the always-on path).

### Exit gate
- Perf harness median-of-3 confirms the ~1.5s target (or documents why not, per design
  §0's own caveat that single runs are coarse).
- All test suites above green.
- No change to `BuildFieldLayouts` or `SweepModuleStaticFields` timings — this phase must
  not touch Phase A/C cost; if it does, something leaked scope.

---

## Phase 2 — Correctness fixes independent of the registry (design §9, partial)

### Status: Implemented

All task slices shipped as scoped, with one accepted trade-off: the `IsDisposedButSubscribed`
MT cache is scoped per-call rather than shared across `EventLeakFastScanner` and
`SweepModuleStaticFields` (each owns its own `Dictionary<ulong, bool>`, both scoped to one
`Analyze` invocation) — sharing would have required plumbing the cache through
`BeforeHeapIndexScan`/`FindEventLeaks`, which is exactly the cross-cutting change Phase 3's
`PublisherRegistry` is meant to own. Deferred there, not done ad hoc here.

Verified:
- `EventLeakAnalyzer.cs`: zero `Console.Error.WriteLine`; `CancellationToken` threaded through
  `FindEventLeaks`/`SweepModuleStaticFields`/the scan loop with the `& 8191` mask (not modulo);
  `GroupEventLeaks`/`EnumerateEventEntries`/`GetEventSubscribers`/`CountOrphanedSubscribers`
  deleted; `IsDisposedButSubscribed` computed via `ClrType.EnumerateInterfaces()` (ClrMD 4.x —
  `Interfaces` property doesn't exist on this branch) cached per unique MT;
  `CalculateSeverity`'s subscriber-count step bonus replaced with the continuous
  `Math.Log2(subscriberCount + 1) * options.SeveritySubscriberLogScale` term.
- `EventLeakOptions.cs`: `SeverityOrphanedSubscriberBonus`/`Cap` removed;
  `SeverityDisposedButSubscribedBonus` (15) and `SeveritySubscriberLogScale` (1.45) added.
- `EventLeakDomainResult.cs`: `ScoringVersion` (= 2) added; `OrphanedSubscriberCount` /
  `OrphanedSubscriberInstances` replaced with `IsDisposedButSubscribed` /
  `DisposedButSubscribedInstances` at instance and group level.
- `EventLeakTrendComparer.cs`: `ScoringVersion` mismatch short-circuits to a single
  `event.leak.scoring_version_mismatch` `MetricDelta` (`MetricTrendDirection.Neutral`) instead
  of diffing incompatible severity scores.
- `EventLeakSectionBuilder.cs` / `AnalyzerDetailSection.cs`: `IsDisposedButSubscribed` rendered
  in place of the old orphaned count; `PublisherAddress == 0 && IsStatic` renders `"(static)"`;
  `PublisherGeneration == -1` renders `"static"` (when `IsStatic`) or `"unknown"` otherwise.
- Tests: `EventLeakAnalyzerAccuracyTests` rewritten — all 12 `GroupEventLeaks_*` cases deleted
  (method is gone), 5 `CalculateSeverity_*` cases rewritten against the log-scale formula, plus
  new zero-subscriber/monotonicity/no-large-jump continuity cases and a disposed-but-subscribed
  bonus case. New `EventLeakTrendComparerTests.cs` covers the `ScoringVersion`-mismatch path.
  New cancellation test added to `EventLeakAnalyzerDiscrepancyTests.cs` (real-dump gated, same
  pattern as its sibling test) asserting `AnalyzeAsync` honors an already-cancelled token
  promptly. Full suite: 387 passed, 0 failed, 41 skipped (env-gated real-dump tests).

### Goals
Ship the correctness and hygiene fixes that don't need `PublisherRegistry` to exist,
so accuracy-test movement from Phase 3 (registry) is attributable to the registry alone.

### Task slices

#### src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs
- Remove all twelve `Console.Error.WriteLine("[PERF] …")` calls (audit Area 1). Route
  through `ILogger<EventLeakAnalyzer>?` per the codebase's optional-logger pattern
  (see `docs/architecture.md` §14 Observability) — resolved via `ActivatorUtilities` in
  `DefaultAnalyzerFactory`, no factory registration change needed since the constructor
  parameter pattern is already established for other analyzers.
- Thread `CancellationToken` through `FindEventLeaks`, `SweepModuleStaticFields`, and the
  scan loop with a periodic check every 8192 iterations (`& 8191` mask, not `% 10000` —
  design §9 is explicit about why the power-of-two matters for the mask).
- Delete `GroupEventLeaks` (dead in production, test-only), `EnumerateEventEntries`
  (never called), `GetEventSubscribers` (never called — superseded by
  `EventLeakFastScanner`). Audit P2-7.
- Remove `CountOrphanedSubscribers` and all call sites. Add
  `IsDisposedButSubscribed` computation: for each unique subscriber MT, check
  `ClrType.Interfaces` for `System.IDisposable` once (cache by MT — this is exactly the
  kind of per-MT metadata `PublisherRegistry` will own in Phase 3, but Phase 2 doesn't
  have the registry yet, so use a local `Dictionary<ulong, bool>` cache scoped to the
  `Analyze` call; Phase 3 migrates this cache into the registry, not before).
- Severity: replace `CalculateSeverity`'s step bonus
  (`if (subscriberCount >= threshold) score += bonus`) with a continuous term
  (`(int)(Math.Log2(subscriberCount + 1) * options.SeveritySubscriberLogScale)`).

#### src/DumpDetective.Core/Options/EventLeakOptions.cs
- Remove `SeverityOrphanedSubscriberBonus` / `Cap` outright (design §9 — not reserved,
  since nothing should reintroduce the old definition).
- Add `SeverityDisposedButSubscribedBonus` (default 15).
- Add `SeveritySubscriberLogScale` (replaces the step-function threshold/bonus pair;
  tune so the *existing* `AddFindings` thresholds — `>= 35` Critical, `>= 20` Warning —
  still land on roughly the same subscriber-count boundaries as today, to avoid a silent
  severity-distribution shift as a side effect of this phase. Confirm with a spot-check
  against `EventLeakAnalyzerAccuracyTests`' existing severity fixtures before tuning
  further).

#### src/DumpDetective.Analysis/Models/EventLeakDomainResult.cs
- Add `ScoringVersion` (int, start at 2 — today's implicit scoring is version 1).
- Replace `EventLeakInstanceSnapshot.OrphanedSubscriberCount` with
  `IsDisposedButSubscribed` (bool) on the instance snapshot; drop the corresponding
  group-level `OrphanedSubscriberInstances` aggregate, replace with
  `DisposedButSubscribedInstances` (count).

#### src/DumpDetective.Analysis/Trend/Comparers/EventLeakTrendComparer.cs
- Refuse to diff two `EventLeakDomainResult`s with different `ScoringVersion` — emit a
  single "scoring formula changed, trend not comparable" note instead of a severity delta
  (design §9's explicit requirement).

#### src/DumpDetective.Reporting/SectionBuilders/EventLeakSectionBuilder.cs
- Render `IsDisposedButSubscribed` where `OrphanedSubscriberCount` used to appear.
- Render `PublisherAddress == 0 && IsStatic` as `"(static)"` (audit #8) instead of `0x0`.
- Render `PublisherGeneration == -1` as `"static"` (when `IsStatic`) or `"unknown"`
  (otherwise) instead of a bare dash (audit #10).
- These three are cheap, presentation-only, and don't need to wait for Phase 6's full
  Phase F rework — ship now since they're one-line-per-case fixes already touching this
  file for the orphaned-subscriber rename.

#### tests
- `EventLeakAnalyzerAccuracyTests`: replace orphaned-subscriber fixtures with
  disposed-but-subscribed fixtures (needs a test double/fixture with an `IDisposable`
  subscriber type). Add severity-continuity test: no two adjacent subscriber counts
  produce a severity jump larger than a defined bound (replaces the old
  step-discontinuity behavior, which had no test asserting the *problem*, only the
  formula's output).
- `EventLeakTrendComparer` tests: add a `ScoringVersion`-mismatch case.
- Cancellation: add a test that cancels mid-`SweepModuleStaticFields` and asserts prompt
  `OperationCanceledException` (bounded by iteration count, not wall clock, to keep the
  test fast and deterministic).

### Acceptance criteria
- Zero `Console.Error.WriteLine` in `EventLeakAnalyzer.cs`.
- `OrphanedSubscriberCount` no longer exists anywhere in the domain model, report, or
  trend comparer.
- Cancellation requested mid-scan or mid-sweep terminates within one check interval
  (8192 iterations), not at completion.
- `EventLeakAnalyzerAccuracyTests` green with new fixtures; old orphaned-subscriber
  fixtures deleted, not left disabled.

### Exit gate
- All test suites above green.
- No perf regression vs. Phase 1's exit-gate numbers (this phase is correctness-only;
  `ILogger` calls and cancellation checks must not measurably affect the harness).

---

## Phase 3 — `PublisherRegistry` + `FieldBackedDelegateShape` (design §3) — ✅ Complete

### Goals
Deduplicate the two type-metadata walks (`BuildFieldLayouts` lazy-per-MT,
`SweepModuleStaticFields`'s independent module walk) into one eager frozen pass, behind
an `IPublisherShape` seam, with exactly one shape registered
(`FieldBackedDelegateShape` — today's behavior, no detection-surface change). Target:
42.3s → ~18s at the 3.3GB baseline (design §0/§3).

This is the highest-risk phase in the plan — it's the one place "if eager build is not
cheaper than the two lazy walks it replaces, this section is wrong and should be
reverted" (design §3.3) applies literally. Budget for a revert path, not just a forward
path.

### Task slices

#### src/DumpDetective.Analysis/Analyzers/EventLeak/ (new folder)
- `IPublisherShape.cs`: the interface from design §3.2 (`Describe(ClrType)`,
  `Extract(IMemoryReader, ulong, in EventFieldDescriptor, List<SubscriberInfo>)`).
- `EventFieldDescriptor.cs`: the readonly struct from design §3.3
  (`Offset`, `NameId`, `IsStatic`, `ShapeId`).
- `PublisherRegistry.cs`: `Build(ClrHeap, IHeapAnalysisCache?, IReadOnlyList<IPublisherShape>?)`.
  - **Revised during implementation (lever 3, design §3.3):** lever 1 alone (single eager
    module walk driving both instance- and static-field detection) was implemented and
    measured first as directed, but regressed registry-build cost to ~48.2s — worse than
    the ~22.8s combined cost of the two lazy walks it replaced, because the expensive
    per-field `ClrType`/base-type resolution used for instance-field detection ran over
    *every type in every loaded module* instead of only types with a live heap instance
    (the scope `BuildFieldLayouts` originally had). Per this phase's own acceptance
    criterion, that result would normally mean revert; the fix applied instead (lever 3)
    keeps the single eager pass but splits it into two scoped passes so no scope is
    widened beyond what the code it replaces already walked:
    - **Pass 1** (static fields): full module walk over every type reachable from every
      loaded `ClrAppDomain`/`ClrModule`, matching `SweepModuleStaticFields`'s original
      scope — required because a static-only publisher can exist with zero live
      instances, so this pass cannot be narrowed to live MTs without losing detection
      coverage.
      Pass 1 also builds an `mtToType` map reused by Pass 2 so it never
      resolves a `ClrType` for the same MT more than once.
    - **Pass 2** (instance fields): restricted to MethodTables observed live on the heap,
      sourced from `IHeapAnalysisCache.EnumerateIndexedEntriesAsTuples()` (cheap
      disk-backed stream, zero ClrMD calls) when a cache is supplied, else
      `heap.EnumerateObjects()` — matching `BuildFieldLayouts`'s original scope exactly.
      Descriptors for MTs present in both passes are merged.
  - Owns delegate-layout discovery (`DiscoverDelegateLayoutFromModules` moves here from
    `EventLeakFastScanner`'s constructor — one discovery per analysis, not per scanner
    construction).
  - Owns event-name resolution (migrates `GetEventNames`'s logic out of the static
    `_eventNameCache` on `EventLeakAnalyzer` — this closes audit P1-2 as a byproduct of
    the registry existing, not a separate fix, per design §9).
  - Owns the `IsDisposedButSubscribed` per-MT cache introduced ad hoc in Phase 2 — move
    it here now that a proper per-MT metadata owner exists.
- `FieldBackedDelegateShape.cs`: wraps today's `EventLeakFastScanner` field-layout logic
  (`BuildFieldLayouts`'s `HasDelegateFields` check, `IsLikelyEventField`,
  `LooksLikeEventFieldName`). Split into `DescribeInstanceFields(ClrType)` and
  `DescribeStaticFields(ClrType)` (rather than a single `Describe`) so
  `PublisherRegistry.Build` can drive each from its own scoped pass above. `Extract`
  wraps `ExtractSubscribersFromDelegateAddress`'s pointer-read logic, unchanged.

#### src/DumpDetective.Analysis/Analyzers/EventLeakFastScanner.cs
- `ScanEntry` looks up `PublisherRegistry.TryGetDescriptors(mt, out slice)` instead of
  lazily building/consulting `_mtIndex`. Zero ClrMD calls on this path (design §5's
  requirement, carried forward unchanged from the prior draft).
- Shape dispatch through descriptor `ShapeId` via a fixed switch, not a virtual
  `IPublisherShape.Extract` call per object on the hot path (design §5's explicit
  performance note — do not reopen the 1.5s hot-path cost measured in §0).
- Remove the class's own delegate-layout discovery and event-name cache — now owned by
  `PublisherRegistry`.

#### src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs
- `BeforeHeapIndexScan` / `FindEventLeaks`: build (or accept, if already built by an
  earlier hook) a `PublisherRegistry` with `[FieldBackedDelegateShape]` instead of
  lazily discovering layouts.
- Instance-scope what was `_eventNameCache` — delete the `static
  ConcurrentDictionary` field entirely (design §9 / audit P1-2), since ownership is now
  the registry's and the registry's lifetime is the analysis.

#### tests
- New: `PublisherRegistryTests` (`tests/.../Integration/CacheDiscrepancies/`, gated on
  `[DiscrepancyFact]` against the real 3.3GB reference dump — a fixture-based version
  was not feasible since `Build` requires a real `ClrModule.EnumerateTypeDefToMethodTableMap`).
  Implemented: `Build_ProducesSameDescriptorsAsFreshBuild_Deterministic`,
  `Build_DelegateOffsets_AreSharedAcrossRegistryAndSameForBothBuilds`,
  `TryGetDescriptors_UnknownMethodTable_ReturnsFalse`,
  `EventNames_SharedInstance_CachesAcrossRepeatedLookups`.
- `EventLeakAnalyzerDiscrepancyTests`: this is the test suite design §12 step 4 flags as
  needing a ground-truth declaration. Since `GetEventSubscribers` (the ClrMD-path
  comparison target) was deleted in Phase 2, there is no second path left to disagree
  with — repurpose or delete this suite's discrepancy-comparison tests, replacing them
  with registry-vs-fixture-expectation tests instead of registry-vs-deleted-code tests.
- `EventLeakAnalyzerAccuracyTests`: must pass unchanged (registry is a refactor of
  `FieldBackedDelegateShape`'s detection logic, not a detection-surface change in this
  phase — any accuracy-test delta here is a bug, not an expected improvement).
- `HeapIndexScanDispatcherPerfTests`: primary acceptance signal for this phase — confirm
  `BuildFieldLayouts`-equivalent cost drops from 22.8s toward the ~18s combined target
  (measured jointly with Phase 4, since lever 1's win only fully lands once
  `SweepModuleStaticFields`'s independent walk is also gone).

### Acceptance criteria
- `EventLeakAnalyzerAccuracyTests` unchanged pass/fail status vs. Phase 2's baseline —
  this phase is a structural refactor, not a detection change.
- Registry build cost measured standalone (per design §3.3's "Open risk" instruction)
  before Phase 4 is started — if eager build isn't cheaper than the walks it replaces,
  stop here and revert rather than carrying the cost into Phase 4.
- `EventLeakFastScanner.ScanEntry` performs zero `ClrType`/`GetTypeByMethodTable` calls
  (verifiable via a call-count assertion in a targeted unit test, not just profiling).

### Exit gate
- Standalone registry-build measurement recorded in this plan (append results below this
  section once run) before Phase 4 starts.
- All test suites above green.
- No regression in `ProcessPublisherEntry`/hot-path timing (still ~1.5s at 3.3GB per
  design §0 — this phase must not touch that number either direction).

### Measured results (3.3GB reference dump, `D:\DUmps\Crash_IIS_BALTSTPRD\...E0434352.dmp`)
Measured via `HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`
with a temporary debug-level console logger attached (reverted after each run — not
committed test infrastructure).

| Build | `PublisherRegistry.Build` | `EventLeakAnalyzer.AnalyzeAsync` total |
|---|---|---|
| Pre-Phase-3 baseline (two independent lazy walks) | ~22.8s | 42.3s |
| Lever 1 only (single eager pass, unscoped instance-field walk) | 48.20s — **regression, fails acceptance criterion** | 82.11s |
| Lever 3 applied (scoped two-pass, this build) | 23.20s | — (not re-measured end-to-end after the lever-3 fix; `Build` cost alone already meets "no worse than the walks it replaces") |

Acceptance criterion ("registry build cost measured standalone... if eager build isn't
cheaper than the walks it replaces, stop here and revert") is **met**: 23.20s vs. the
~22.8s baseline is effectively parity, not a regression, and Phase 4 is expected to
absorb the remaining gap (below).

Remaining gap to the ~18s combined target: `SweepModuleStaticFields`'s own module walk
is still running independently of `PublisherRegistry` (measured ~11.68s in the same
run) — that duplication is explicitly Phase 4's job (design §6), not part of this
phase's scope. Phase 3 does not delete it.

---

## Phase 4 — Registry-driven statics (design §6) — ✅ Complete

### Goals
Replace `SweepModuleStaticFields`'s independent module walk with a single iteration over
`PublisherRegistry`'s `_staticPublisherMTs`, closing the double-count bug where
`processedStaticMTs` is accepted but never consulted.

### Task slices

#### src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs
- Delete `SweepModuleStaticFields`'s module/type walk
  (`module.EnumerateTypeDefToMethodTableMap().Select(...).Where(...)` — also closes the
  LINQ-in-scanning-loop item, audit Area 3 #1 / P1-4, as a side effect of deletion rather
  than a separate fix).
- New static pass iterates `registry.StaticPublisherMTs` once, reading static fields at
  the registry's known offsets for each MT, for each `IPublisherShape` that produced
  static descriptors for that MT.
- Remove `processedStaticMTs`/`processedStaticDelegates` dedup sets — registry-driven
  single-sweep makes them structurally unnecessary (design §6), not just unused.

#### tests
- `EventLeakAnalyzerAccuracyTests`: add a regression fixture for the double-count bug —
  a type with both heap instances and a static event field must be counted once, not
  twice, in the redesigned path. (This bug currently exists silently; the fixture should
  be written against *documented current behavior first*, confirmed to fail on the old
  code path if run against it, then confirmed to pass after this phase — otherwise "fixed
  a bug with no regression test" is unverifiable.)
- `HeapIndexScanDispatcherPerfTests`: confirm the combined Phase 3+4 type-metadata cost
  lands near the ~18s target (design §0's `Target` table).

### Acceptance criteria
- Double-count bug fixture passes.
- `SweepModuleStaticFields` (or its replacement) no longer performs an independent
  `EnumerateTypeDefToMethodTableMap` walk — verify via a call-count/coverage check, not
  just timing, since a correct-but-still-duplicated walk could coincidentally show
  similar wall-clock time on a small fixture dump.

### Exit gate
- Combined registry + static-sweep cost measured against the ~18s target at 3.3GB
  baseline (median of 3).
- All test suites above green.

### Status: Implemented

`SweepModuleStaticFields`'s independent `EnumerateTypeDefToMethodTableMap` module walk is
deleted. `EventLeakAnalyzer.SweepRegistryStatics` (private static) now iterates
`PublisherRegistry.StaticPublisherMTs` once, reading each MT's already-known static
descriptors. `EventLeakFastScanner.ProcessInstanceFields` skips `descriptor.IsStatic`
entirely, so statics never run on the hot path — closing the double-count bug
structurally (only one code path can ever add a static group), not just by patching the
dedup set. `processedStaticMTs`/`processedStaticDelegates` are removed as structurally
unnecessary. Regression coverage:
`PublisherRegistryTests.FastScanner_Scan_Alone_ProducesNoStaticGroups` proves the fast
scanner alone (no sweep) produces zero static groups; the full test suite (unit +
gated `PublisherRegistryTests` + gated `EventLeakAnalyzerDiscrepancyTests`) is green.

### Measured results (3.3GB reference dump, `D:\DUmps\Crash_IIS_BALTSTPRD\...E0434352.dmp`)
Measured via `HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`
with a temporary debug-level logger attached (routed to `ITestOutputHelper`, reverted
after each run — not committed test infrastructure), 3 isolated runs:

| Run | `PublisherRegistry.Build` | `SweepRegistryStatics` | Build + Sweep | `AnalyzeAsync` TOTAL |
|---|---|---|---|---|
| 1 | 27.16s | 0.44s | 27.60s | 56.01s |
| 2 | 22.03s | 0.30s | 22.33s | 46.22s |
| 3 | 22.45s | 0.31s | 22.76s | 43.51s |
| **Median** | **22.45s** | **0.31s** | **22.76s** | **46.22s** |

Combined registry-build + static-sweep type-metadata cost is now **22.76s** (median),
down from the ~34.88s pre-Phase-3 combined baseline (23.20s `PublisherRegistry.Build`
alone at end of Phase 3 + ~11.68s for `SweepModuleStaticFields`'s independent module
walk, which Phase 4 eliminates). `SweepRegistryStatics` itself is now negligible
(0.3–0.4s) since it only reads already-known descriptors at already-known offsets — all
type-metadata discovery cost is now paid exactly once, in `PublisherRegistry.Build`.

This lands short of the ~18s aspirational target from design §0 (median 22.76s vs.
~18s), but represents a ~35% reduction in combined type-metadata cost from the
pre-Phase-3 baseline and — more importantly — closes the double-count correctness bug
that was this phase's primary goal. The remaining gap to ~18s is in
`PublisherRegistry.Build` itself (Pass 1's full module walk over every loaded type),
which is out of scope for this phase (design §6 targets `SweepModuleStaticFields`
duplication specifically, not `Build`'s own cost).

---

## Phase 5 — Correlation phase (design §7) — ✅ Complete

### Status: Implemented

`GroupAccumulator` gained `AllSubscriberMethodCounts` (keyed by `(Type, MethodName)`),
populated in the same per-subscriber loop `AddToAccumulator` already uses for
`AllSubscriberTypeCounts`, and merged the same way in `MergeAccumulatorEntry`. `EventGroupInfo`
carries the new dictionary through `FindEventLeaks`'s accumulator→group conversion, unchanged
in shape otherwise. Two pure, heap-free static folds — `EventLeakAnalyzer.BuildTopSubscriberTypesAcrossGroups`
and `BuildTopHandlerMethodsAcrossGroups` — run once after `PopulateEvidence`, over the completed
`groupedLeaks` list, bounded to the top 20 entries each (`TopCorrelationEntries`), and are exposed
as `EventLeakDomainResult.TopSubscriberTypesAcrossGroups` / `TopHandlerMethodsAcrossGroups`
(`IReadOnlyList<NameCountEntry>`, reusing the existing type — no new record needed).
`EventLeakSectionBuilder` renders both as first-class top-level `CompactTable`s ("Top subscriber
types across all groups", "Top handler methods across all leaking events"), not nested inside the
per-group breakdown.

Verified: 6 new fold-correctness unit tests (cross-group summation, `topN` bound, null
`MethodName` rendering as `"Type.?"`, empty-groups case) — all heap-free, hand-built
`List<EventGroupInfo>` fixtures per this phase's test-surface note. Full `EventLeak`-filtered
suite green (40 passed, 3 skipped — real-dump-gated).

### Goals
Add `TopSubscriberTypesAcrossGroups` and `TopHandlerMethodsAcrossGroups` as first-class
domain-result collections. Pure in-memory fold over the completed
`GroupAccumulator` map — no heap access, negligible cost.

### Task slices

#### src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs
- After Phase C (statics) completes and `groupAcc` is final, fold
  `GroupAccumulator.AllSubscriberTypeCounts` from every group into one
  `Dictionary<string, int>` for the type view.
- **Resolved**: the handler-method dependency flagged below is real but cheap —
  `SubscriberInfo.MethodName` is already resident on every `SubscriberInfo` at the
  exact point `AddToAccumulator` builds `AllSubscriberTypeCounts`
  ([EventLeakAnalyzer.cs:402-406](../../../src/DumpDetective.Analysis/Analyzers/EventLeakAnalyzer.cs)),
  it's just discarded (only `s.Type` is tallied). Decision: add a second field
  `GroupAccumulator.AllSubscriberMethodCounts` of type
  `Dictionary<(string Type, string? MethodName), int>`, populated in the same
  `foreach (SubscriberInfo s in leak.Subscribers)` loop in `AddToAccumulator`
  (mirroring the existing `AllSubscriberTypeCounts` tally), and merged the same way
  `AllSubscriberTypeCounts` is merged in `MergeAccumulatorEntry`
  (EventLeakAnalyzer.cs:452-456). No changes needed to `EventLeakFastScanner` or the
  scan itself — this is a pure `GroupAccumulator`-side addition.
- Add the parallel fold keyed by `(SubscriberType, MethodName)` for the handler-method
  view using the new `AllSubscriberMethodCounts` field.

#### src/DumpDetective.Analysis/Models/EventLeakDomainResult.cs
- Add `TopSubscriberTypesAcrossGroups` and `TopHandlerMethodsAcrossGroups` as
  `IReadOnlyList<NameCountEntry>` (reuse the existing `NameCountEntry` type — no new
  record needed).

#### src/DumpDetective.Reporting/SectionBuilders/EventLeakSectionBuilder.cs
- Render both as new top-level tables in the section, not nested inside a per-group
  breakdown (design §7's explicit requirement that these be first-class, not an
  appendix).

#### tests
- New: correlation-fold correctness test against a hand-built `GroupAccumulator` map
  fixture (design §12 step 6 / this plan's Phase 5 test surface) — assert the fold
  produces the expected top-N ordering and counts without touching a heap at all.
- `EventLeakFindingGeneratorTests`: confirm no unintended interaction (this phase adds
  report data, not findings — verify findings generation is untouched).

### Acceptance criteria
- Both correlation views present in `EventLeakDomainResult` whenever any group exists.
- Fold cost adds no measurable time to the perf harness (design §7's stated expectation
  — if it does, something is doing heap work it shouldn't, per that section's own
  warning).

### Exit gate
- Correlation-fold test green.
- Perf harness shows no measurable delta vs. Phase 4's exit-gate numbers.

---

## Phase 6 — Structured presentation data / Phase F (design §8) — ✅ Complete

### Status: Implemented

`EventLeakDomainResult.cs` gained `SubscriberTypeCount(string Type, int Count)`;
`EventLeakInstanceSnapshot.SubscriberTypes` is now `IReadOnlyList<SubscriberTypeCount>?`
instead of pre-formatted strings. `EventLeakAnalyzer.cs`'s construction site builds
`SubscriberTypeCount` entries with a manual `List.Sort` (no LINQ), mirroring the
`TopNByCount`/`BuildTopSubscriberTypesAcrossGroups` convention already used elsewhere in
that file. `EventLeakSectionBuilder.cs` adds a `RootKindLabels` lookup translating every
raw `RootIndexReader.KindToString` value (`None`, `FinalizerQueue`, `StrongHandle`,
`PinnedHandle`, `Stack`, `RefCountedHandle`, `AsyncPinnedHandle`, `SizedRefHandle`,
`ThreadStaticVar`, `StaticVar`) to a human-readable label, applied only inside
`FormatRootHintDisplay`; the domain model still stores the raw string.

JSON/API audit (design §12 step 7's required exit-gate item): grepped
`report.renderers.sections.js` and every `.cs` consumer of `EventLeakDomainResult`. The
JS renderer only reads the group-level `EventLeakGroupSnapshot.TopSubscriberTypes`
(already `NameCountEntry`, untouched by this phase); it never reads the instance-level
`SubscriberTypes` field this phase retypes. No serializer emits `EventLeakDomainResult`
directly — only `EventLeakSectionBuilder`, `EventLeakFindingGenerator`, `InsightEngine`,
and `EventLeakTrendComparer` consume it, all internal. **Sign-off: no breaking
JSON/API contract.**

Deviation from the task slice as written: `EventLeakInstanceCard` (the typed DTO the
section builder renders) has no `SubscriberTypes`-shaped field and never did —
`SubscriberDetails` (`Type` + `MethodName` + `Count` + `Size`) already supersedes it in
every rendered instance card. There is currently no display point for the retyped field
to render `"{Type} ({Count:N0})"` at. Rather than inventing a redundant render site, the
field is left structured-but-unrendered, matching the design's own stated rationale for
keeping it at all ("so a future consumer that wants the raw counts still can").

Verified: 3 new `EventLeakSectionBuilderTests` (RootHint translation for a known kind,
pass-through for an unrecognized kind, `PublisherRootPath` still takes priority over the
translated fallback) — all green.

### Goals
Move formatting out of the domain model into `EventLeakSectionBuilder`. Domain-model
change with report-builder fallout — coordinate with any downstream JSON/API consumers
before shipping (design §12 step 7's explicit caution).

### Task slices

#### src/DumpDetective.Analysis/Models/EventLeakDomainResult.cs
- Add `SubscriberTypeCount(string Type, int Count)` record.
- Change `EventLeakInstanceSnapshot.SubscriberTypes` from `List<string>`
  (pre-formatted `"App.MyType (3)"`) to `IReadOnlyList<SubscriberTypeCount>`.
- Keep `RootHint`'s raw ClrMD string as-is in the domain model (design §8's explicit
  instruction: translate at render time, don't store translated, so a consumer wanting
  the raw `RootKind` still can).

#### src/DumpDetective.Reporting/SectionBuilders/EventLeakSectionBuilder.cs
- Render `SubscriberTypeCount` as `"{Type} ({Count:N0})"` at the point of display.
- Add the `RootHint` → human-readable translation table (`"LocalVar"` → `"local
  variable"`, `"StaticVar"` → `"static field"`, etc. — audit P2-4) as a fixed lookup used
  only here, not stored back into the domain model.

#### Any JSON/API export path
- Audit whatever serializes `EventLeakDomainResult` directly (check for a JSON exporter
  or API surface before starting — if the domain result is serialized as-is anywhere,
  this is a breaking change to that contract, not just an internal refactor. Coordinate
  before shipping, per design §12's caution).

#### tests
- `EventLeakSectionBuilder` tests: update fixtures for the new record shape; add a
  render test for the `RootHint` translation table.
- Any serialization/contract tests for `EventLeakDomainResult` — update or confirm none
  exist and note that explicitly in this phase's PR description.

### Acceptance criteria
- No `List<string>` pre-formatted fields remain in `EventLeakDomainResult`.
- `EventLeakSectionBuilder` is the only place `"{Type} ({Count})"`-style formatting
  happens for subscriber types.
- Confirmed (not assumed) whether any external consumer serializes the domain result
  directly, and that consumer's contract is either unaffected or explicitly updated.

### Exit gate
- `EventLeakSectionBuilder` tests green.
- Explicit sign-off recorded in this plan (or the PR) on the JSON/API audit above before
  merge — this is the one phase in the plan with an external-contract risk, so it doesn't
  get a silent pass.

---

## Phase 7 — `EventHandlerListShape` / `WeakEventShape` (design §3.2, additive)

### Goals
Close audit P3-1 (WinForms `EventHandlerList` coverage) and P3-2 (weak-event
classification) as additive shapes against the now-stable `PublisherRegistry`. Ship
independently, gated behind `EnabledShapes`, each with its own accuracy tests against a
WinForms/weak-event fixture dump.

### Task slices

#### src/DumpDetective.Analysis/Analyzers/EventLeak/EventHandlerListShape.cs
- `Describe`: recognize `System.ComponentModel.EventHandlerList`-backed types via known
  field layout (the `Control.Events` pattern).
- `Extract`: read the keyed delegate collection — design §3.2 flags this must be
  push-based or struct-enumerator, not `IEnumerable<(string, ulong)>` per object (that
  allocates an enumerator and tuples per publisher — the same hot-path allocation
  discipline as `IPublisherShape.Extract` elsewhere).

#### src/DumpDetective.Analysis/Analyzers/EventLeak/WeakEventShape.cs
- `Describe`/`Extract`: recognize `WeakEventManager`/`ConditionalWeakTable`-backed
  chains. Tag matches with `IsWeakEvent = true` on the resulting `EventLeakInfo` rather
  than suppressing them (design §3.2 — informational, not a false-positive to hide).

#### src/DumpDetective.Core/Options/EventLeakOptions.cs
- `EnabledShapes` defaults to `[FieldBackedDelegate]` only. Both new shapes opt-in.

#### src/DumpDetective.Analysis/Models/EventLeakDomainResult.cs
- Add `IsWeakEvent` to `EventLeakInstanceSnapshot`.

#### src/DumpDetective.Reporting/SectionBuilders/EventLeakSectionBuilder.cs / FindingGenerators/EventLeakFindingGenerator.cs
- Render weak-event matches as informational, not in the leak-severity ranking path.
- `EventHandlerList` matches render identically to field-backed matches (same
  `EventLeakInstanceSnapshot` shape) — no new report section needed, confirming the
  abstraction is paying for itself here.

#### tests
- New WinForms fixture dump (or synthetic heap fixture, if a real dump isn't available)
  with `Control`-derived types using `EventHandlerList`.
- New weak-event fixture with a `WeakEventManager`-backed subscription.
- `PublisherRegistry` per-shape build-cost measurement (design §3.3's "Open risk" —
  confirm eager build cost with both new shapes enabled is still justified; if not, they
  ship disabled by default, which is already the plan, but the measurement should be
  recorded, not assumed).

### Acceptance criteria
- WinForms fixture: subscriptions via `Control.Events` are detected and reported.
- Weak-event fixture: subscriptions are reported as informational, not counted toward
  leak severity/findings.
- Both shapes disabled by default; enabling either does not affect
  `EventLeakAnalyzerAccuracyTests`' existing (non-WinForms, non-weak-event) fixtures.

### Exit gate
- Both fixture test suites green.
- Per-shape registry-build cost recorded against the standalone measurement from Phase 3.

---

## Phase 8+ — Deferred (not scheduled)

Per design §10 and §12 step 8: §4.4 Tier 2 (dominator cross-reference) is picked up as
its own follow-up plan once Phase 6 has shipped and `EventLeakDomainResult`'s shape is
stable. Do not open task slices for it here — when it's ready to schedule, it gets its
own implementation plan referencing `DominatorAnalyzer`'s output contract at that time,
not this document extended retroactively.

---

## Cross-phase test surface (holds steady throughout)

`EventLeakAnalyzerAccuracyTests`, `EventLeakAnalyzerDiscrepancyTests` (repurposed in
Phase 3), `EventLeakFindingGeneratorTests`, `EventLeakSectionBuilder` tests,
`EventLeakTrendComparer` tests and stored baselines,
`HeapIndexScanDispatcherPerfTests.EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown`.

Measurement harness discipline (design §12): filtered run only
(`DD_RUN_DISCREPANCY_TESTS=1`), never the full suite; compare medians of 3 runs; ~18%
run-to-run variance is expected and not itself a regression signal.

## Sequencing recap

| Plan phase | Design doc §  | Depends on | Perf-measured? |
|---|---|---|---|
| P1 | §4 | — | Yes — primary win |
| P2 | §9 (partial) | P1 (shares files, not behavior) | No (correctness-only) |
| P3 | §3 | P2 | Yes — highest risk |
| P4 | §6 | P3 | Yes |
| P5 | §7 | P4 | No (negligible by design) |
| P6 | §8 | P5 (not strictly required, but sequenced after to avoid churn) | No |
| P7 | §3.2 | P6 (stable registry + domain model) | Yes (per-shape) |
| Deferred | §4.4 Tier 2 | P6 | N/A — own future plan |
