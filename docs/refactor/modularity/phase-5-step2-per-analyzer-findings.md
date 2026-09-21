# Phase 5 Step 2 — Per-Analyzer Migration Findings

Part of [../modularity-plan.md](../modularity-plan.md), split out of
[phase-5-observations-synthesis.md](phase-5-observations-synthesis.md) on 2026-09-21 once that
doc's own size (1300+ lines, two-thirds of it this section) made it hard to read as a plan. See
that doc's "Before step 2" prerequisite section for the playbook (steps a–f) each entry below
follows, and its "Per-analyzer migration status" table for the compact pass/fail summary. This
file is the detailed record of what step c's audit found and step d's fix was, analyzer by
analyzer, in migration order within each package — historical record of completed work, not part
of the live plan.

## Contents

- [Plugins.Types](#pluginstypes) — Array, Boxing, Collection, ObjectShape, String
- [Plugins.Memory](#pluginsmemory) — LohFragmentation, AllocationPattern, GCGeneration, Memory, SegmentReservation
- [Plugins.Async](#pluginsasync) — AsyncStateMachine, AsyncTask
- [Plugins.Gc](#pluginsgc) — FinalizableObject, GCHandle, GCRoot, WeakReference
- [Plugins.Infra](#pluginsinfra) — SqlConnectionPool, SqlCommand, SqlTransaction, DbConnection, HttpObject, TimerLeak, WcfChannel
- [Plugins.Leaks](#pluginsleaks) — Dominator, EventLeak, ReferenceChain, StaticRoot
- [Plugins.Runtime](#pluginsruntime) — Crash, HeapTopology, Jit, Module
- [Plugins.Threads](#pluginsthreads) — Hang, LockGraph, Thread, ThreadStackCluster

---

## Plugins.Types

### `ArrayFindingGenerator`

- Step c's audit found `ArrayAnalyzer` only backed 2 of 4 finding branches with observations
  (LOH pressure, multi-dim, pinned-array accumulation had none) — despite its own doc comment
  already flagging the gap.
- Step d's fix: extended `type.array-composition` with exhaustive per-type `loh.*`/`pinned.*`
  measures (summed in the rule, not a new whole-run scalar), plus two new bounded per-entry
  types (`heap.large-array`/`heap.pinned-array`).
- Writing the rule surfaced two things this playbook hadn't anticipated: `Finding` itself was
  missing `Analyzer`/`Category`/`Tags`/`MetricValue`/`MetricUnit` — fields the report pipeline
  (`ActionPriorityService`, `CorrelationBuilder`, several section builders) actually reads —
  and the old generator trusted `ArrayDomainResult`'s pre-sorted lists rather than sorting
  itself, so the rule's initial explicit re-sort was a real (if invisible-on-real-data)
  behavior change, caught by the parity test itself.

### `BoxingFindingGenerator`

- `type.boxing` is the complete, unfiltered boxed-type population (bounded by distinct-type
  count, never heap-object-scale), so `TotalBoxedObjects`/`TotalBoxedBytes`/`BoxedEnumCount`/
  `BoxedEnumBytes`/`NullableBoxedCount`/`NullableBoxedBytes`/`TotalGen2BoxedCount` are all, in
  principle, re-derivable by summing matched `type.boxing` observations. Deliberately *not* done
  that way, though: `BoxingFindingGenerator`'s summary finding fires unconditionally, even over
  `Analyze`'s all-zero `Empty()` result (returned when generation data isn't exact) — a case with
  zero `type.boxing` observations to sum at all. A new whole-run `type.boxing-pressure`
  observation, unconditionally emitted regardless of whether any per-type list is empty, carries
  these scalars directly from the domain result's own arithmetic instead, guaranteeing the
  always-present summary finding a non-empty `DerivedFrom` even over a fully empty dump.
- `is-nullable` (new per-type flag on `type.boxing`) is the same `System.Nullable<...>`
  name-pattern check `Analyze` already ran internally — surfaced as a raw structural fact, same
  treatment as the pre-existing `is-enum`/`has-reference-fields`/`has-iequatable` flags.
- `OversizedValueTypeInstanceCount`/`TopOversizedTypes` and `TopPaddingWasteTypes` are each a
  separate, differently-filtered aggregation over the same underlying per-type stats scan —
  neither shares `type.boxing`'s population. Two new observation types close the gap:
  `type.oversized-value` (one per type over `OversizedThresholdBytes`, already sorted descending
  by instance count) and `type.struct-padding-waste` (one per type with nonzero padding waste,
  already sorted descending by wasted bytes) — both bounded by distinct-type count, the same
  population-size guarantee `type.boxing` itself relies on.
- Old test suite's fixtures set `TotalBoxedObjects`/`BoxedEnumCount`/`NullableBoxedCount`/etc. as
  independent scalars with `TopBoxedTypes` usually left empty — the same test-only decoupling
  `AllocationPatternSynthesisRuleParityTests` fixed for its own analyzer. Rebuilt every fixture
  from a single per-type entry list (`BuildFromEntries`), deriving every aggregate scalar from
  that list for both the old and new sides, plus one new fixture
  (`AllZero_NoPerTypeObservationsAtAll_StillEmitsSummaryFinding`) exercising the zero-observations
  edge case `type.boxing-pressure` exists to cover.

### `CollectionFindingGenerator`

- `collection.wasteful` is capped by `CollectionAnalyzerOptions.TopWastefulCollectionsToShow`,
  but `WasteCountsByKind`/`WasteBytesByKind`/`WasteCountsByElementType`/`WasteBytesByElementType`
  are exact scan-time accumulators over the *entire* wasteful-collection population — the domain
  model's own doc comments say deriving them from a capped top-N list "would undercount every
  element type," so none of the four can be reconstructed from `collection.wasteful`. Closed with
  a new whole-run `collection.waste-summary` (the plain scalars: `TotalCollections`/
  `TotalWastedMemory`/`WastefulCollectionCount`) plus two new per-entry observation types,
  `collection.kind-waste` (one per `CollectionKind` present, at most 11 values, no entity
  subject) and `collection.element-type-waste` (one per distinct element type, bounded by
  distinct-type count like `type.boxing`).
- `CollectionKind` has no natural entity subject, so its name goes into `Evidence[0].Description`
  on both the new `collection.kind-waste` observation and the existing `collection.wasteful`
  (which needed it too, as the old generator's fallback identity when `WasteCountsByKind` is
  empty but `TopWastefulCollections` isn't) — first migration to reuse the same categorical-text
  Evidence idiom across two different observation types for the same underlying fact.
- Old test suite's fixtures already passed `WasteCountsByKind`/`WasteBytesByKind`/etc. directly,
  with no `TopWastefulCollections`/other-field coupling to break — unlike Boxing, no fixture
  rebuild was needed; `ToObservations` just mirrors the dictionaries directly into observations.

### `ObjectShapeFindingGenerator`

- The most novel gap of any migration so far: `type.shape-composition`'s own Phase 5 step 1 doc
  comment *already* deliberately excludes every per-type `Top*Types` list (misleadingly named —
  each is actually the entire whole-heap type population partitioned by a hand-picked-threshold
  `Category`, unlike every other analyzer's genuinely-capped "Top" lists), citing the same
  unbounded-volume risk `GCGenerationAnalyzer`'s remarks flag. But the old finding generator reads
  exactly three small, already-selected slices of those lists — `TopReferenceHeavyTypes.Take(3)`,
  `TopValueHeavyTypes[0]`, and a filtered finalizable-and-heavy subset — never the full
  populations themselves. Three new per-type observation types carry only that selection,
  reusing the analyzer's own pre-existing sort order (never re-derived): `type.reference-heavy`
  (top 3 by GC-scan-cost score), `type.value-heavy` (the single top type by heap size), and
  `type.finalizable-reference-heavy` (every `IsFinalizable` + ≥10K-instance entry — filtered, not
  capped, since the old generator needs an *exact* count of this one, narrow, distinct-type-scale
  population).
- Real parity bug caught mid-migration, not just a gap: the old generator's evidence text uses
  `TopReferenceHeavyTypes.Count`/`TopValueHeavyTypes.Count` — the full classification population
  size — while the new per-type observations are capped to a display-only top 3/top 1. Counting
  matched observations (the `LockGraphSynthesisRule` move) would have silently shown "3" instead
  of the true count whenever more than 3 reference-heavy types exist. Fixed by adding two exact
  whole-run counts, `reference-heavy-type.count`/`value-heavy-type.count`, to
  `type.shape-composition` instead — caught by a new fixture,
  `MoreThanThreeReferenceHeavyTypes_EvidenceCountUsesFullTotalNotJustTop3`, before it could reach
  the old test suite's blind spot (its own fixtures never exercised more than one reference-heavy
  type at a time).
- Old test suite covered only the reference-field-density finding; added six new fixtures for the
  value-heavy and finalizable-reference-heavy findings (above/below their respective 10K-instance
  thresholds), plus a "no reference-heavy types at all" case and one all-signals-firing-together
  case, none of which the old suite exercised.

### `StringFindingGenerator`

- Same capped-vs-exact trap `ObjectShapeSynthesisRule`'s migration caught, found again here:
  `heap.long-string` is emitted uncapped, inline, during the heap scan, but the domain result's
  own `VeryLongStrings` list gets truncated to `maxVeryLongStringsToKeep` *after* that scan
  completes — so counting/summing matched `heap.long-string` observations would silently
  overcount whenever more than the cap exists. Closed by putting `very-long-string.count`/`.bytes`
  on a new whole-run `type.string-composition` observation instead, computed from the
  already-truncated list at the exact point the domain result itself is built.
- `DuplicationRatio`/`DuplicatePatternCount`/`DuplicateWastedBytes` have no per-entry stand-in at
  all — they come from `stringStats`, a sampled hash structure with no "duplicate string" entity
  kind in the SDK yet (the pre-existing `heap.long-string` doc comment already noted this same gap
  for `TopDuplicates`). All three, plus `LohStringBytes`/`PctOfManagedHeap`/
  `TotalStringMemoryBytes`/`SamplingCoverage`/`StringsSampled`, went onto the same
  `type.string-composition` observation alongside the two very-long-string totals — one whole-run
  observation covering every scalar this generator's six findings need, none requiring per-entry
  identity data.

---

## Plugins.Memory

### `LohFragmentationFindingGenerator`

- Chosen deliberately as a contrast to Array: smallest generator in the set (one finding, two
  fields), to check the playbook isn't only exercised by gap-heavy cases.
- `heap.loh-fragmentation` is emitted per segment-kind row (Large/Pinned, ≤2), each carrying
  that kind's own `fragmentation.percent` — reconstructing the old generator's combined-total
  percent means summing `total.bytes`/`free.bytes` across matched rows and recomputing, never
  averaging the per-kind percentages (two kinds at 50%/10% with unequal segment counts do not
  average to 30%). A fixture exercises exactly that combination.
- Confirmed the enricher wiring generalizes for free: adding the rule to
  `DefaultSynthesisRuleCatalog` was the only change needed to reach the real report — no
  `RunAnalyzersNode`/`ServiceRegistration.cs` edits this time.

### `AllocationPatternFindingGenerator`

- First entry where the finding generator's severity/title/evidence text is driven entirely by
  **re-derived judgment**, not raw facts read directly: `AllocationPatternPressureClassifier`
  (`AllocationPatternAnalyzer`'s own sole-owner classifier, per the Smell A audit) computes
  `GCPressureLevel` and two pressure scores from `Gen0CountPct`/`Gen2CountPct`/`LohSizePct`, and
  the domain result's own `AllocationProfile` field is Stage-1-baked from the same two count
  percentages (`ClassifyProfile`) — both already excluded from `gc.allocation-composition`
  (Phase 5 step 1) as the audit's catalogued Smell B/A, so nothing needed un-baking, but the rule
  still needs the *raw inputs* those formulas run on.
- Step c's audit found `gc.allocation-composition` carried the count-fraction inputs
  (`gen0.count-fraction`/`gen2.count-fraction`) but none of the size-fraction ones
  (`Gen0SizePct`/`Gen2SizePct`/`LohSizePct` — evidence text and `ComputePressureScore` both need
  `LohSizePct`), and no whole-run scalar for `TransientClassificationThreshold` (the
  `AllocationProfile` cutoff, runtime-configurable via `AnalysisContext.AnalyzerOptions`) or the
  single long-lived-type name the evidence text embeds.
- Step d's fix: three new raw measures (`gen0.size-fraction`/`gen2.size-fraction`/
  `loh.size-fraction` — `gen1.size-fraction` deliberately omitted, no finding reads it);
  `transient-classification.threshold` as a measure rather than a hard-coded 70.0 default, same
  wrinkle `GCHandleSynthesisRule` hit; and a single `EvidenceRef` carrying the one
  top-by-composite-score long-lived type's name — the unbounded, composite-ranked
  `TopLongLivedTypes` list itself is *not* observed per-entry (Smell B, plus the same real
  observation-volume risk this observation's own note already flags), only the one identity the
  old generator actually reads.
- **`AllocationPatternPressureClassifier` and `AllocationProfile` are duplicated, not called or
  imported**: both live in `DumpDetective.Plugins.Memory`, `internal` to that assembly, invisible
  to `DumpDetective.Reporting`. The rule re-implements `ComputePressureScore`/`ClassifyPressure`/
  `ComputePromotionPressureScore`/`ClassifyProfile` verbatim as private methods (with a private
  enum mirroring each domain enum) — the same "re-derive rather than reach into another
  assembly's internals" choice `TimerLeakSynthesisRule` made for its type-category
  classification, just applied to arithmetic instead of a name match.
- **The old generator's own unit tests hard-code `Profile: AllocationProfile.Mixed` for every
  pressure fixture**, including High/Critical ones whose real `Gen0CountPct`/`Gen2CountPct` would
  classify as `Retained` in production (`ClassifyProfile` and `ClassifyPressure` read the same
  two inputs but are otherwise independent formulas, so the old fixtures decouple them in a way
  production never can) — the same "achievable only in test fixtures" situation
  `AsyncStateMachineSynthesisRuleParityTests` hit. The parity fixtures compute `Profile` from the
  same `ClassifyProfile` logic the rule uses, so both sides render the same (real) profile text
  instead of reproducing the impossible decoupling.
- Unlike every prior playbook entry, Low pressure emits **no finding at all**, not even an
  info-level one — ported as a bare early return, not a suppressed/gated finding.

### `GCGenerationFindingGenerator`

- Step c's audit found `gc.generation-composition` (Phase 5 step 1) already carried the
  byte-based totals/fractions, but none of the count-based scalars
  (`TotalObjects`/`PohObjects`/`Gen0Objects`/`Gen2Objects`) the POH/Gen0/Gen2 findings' own
  percentage math needs, none of the three runtime-configurable thresholds
  (`LohThresholdPercent`/`Gen0PressureThresholdPercent`/`PohThresholdPercent`), and no
  finalizable-Gen2 cross-reference totals. `FallbackMode`/`GenBytesAreApproximate` needed nothing
  new — they're already `Provenance.Fidelity`/`Observation.Confidence` on the existing
  observation. Two genuinely different-sounding-but-distinct pairs surfaced here for the first
  time: `loh.percent`/`gen2.percent` (the domain result's own percentages the finding generator
  reads directly) are **not** the same values as the existing `loh.fraction`/`gen2.fraction`
  (independently-computed byte-fractions-of-total-managed-bytes) despite the naming similarity —
  `gen2.percent` in particular is count-based where `gen2.fraction` is byte-based.
- The real gap: the Gen0/Gen2 findings' "top types" evidence reads `PerTypeGenerationProfiles` —
  the exact same up-to-131,568-entry whole-heap population this observation's own Phase 5 step 1
  remarks already named as the volume risk it deliberately deferred. Emitting it per-entry to
  back two findings that only ever show 3 types each would reintroduce exactly that risk.
- Step d's fix: rather than accept that risk or duplicate the old generator's selection logic
  (`BuildTopTypeEvidence`/`IsFrameworkType`, which needs the *full* population to run its
  framework-namespace-deprioritization search) into the rule — which would have needed the rule
  to see that full population anyway — **the selection itself was ported into the analyzer**
  (`GCGenerationAnalyzer.SelectTopTypes`/`IsFrameworkType`), and only its ≤3-per-list output is
  observed, as two new bounded per-type types: `gc.generation-top-allocator` (ranked by
  `Gen0Count`) and `gc.generation-top-accumulator` (ranked by exact Gen2 bytes when the whole-run
  `has-gen2-bytes` flag is set, otherwise Gen2 instance count — both raw values carried on every
  entry so the rule doesn't need a second observation type to pick the right text format). This
  keeps the analyzer's own per-run observation count at 6, not 131,568 — the first entry in this
  playbook where selection logic was moved *into* the analyzer specifically to keep an unbounded
  population from ever becoming observations, rather than trusting emission order or re-deriving
  a ranking in the rule from data it already has.
- The rule accordingly does no ranking or framework-namespace filtering of its own: it only
  formats and joins the already-selected, already-ordered observations — trusting emission order
  the same way `ArraySynthesisRule` trusts its own pre-sorted lists, just one level further
  removed (sorted *and* filtered upstream, not just sorted).
- `FormatBytes` (GB/MB at 2 decimal places, KB at 1) is a different formatter from
  `FormatHelper.FormatBytes` used elsewhere in this playbook, and is duplicated verbatim from the
  old generator rather than swapped — a real, if minor, text-precision difference between the two
  that this migration must not silently introduce.

### `MemoryFindingGenerator`

- Smallest gap of any whole-run-scalar-driven entry so far: the only existing observation this
  analyzer produced (`heap.segment-composition`, per-generation-label committed/reserved/used
  bytes) backs a completely different consumer, so effectively every field this finding generator
  reads was unobserved. All of it — `TotalBytes`/`LohBytes`/`LohPercent`/`TotalObjects`/
  `LohObjects`/`UniqueTypes`/`Top1BytesPercent`/`Top5BytesPercent`/`SmallObjectCountPercent`/
  `SmallObjectBytesPercent`/`ObjectsPerMb` — is a whole-run scalar with no per-type/per-object
  population risk, so this needed exactly one new observation, `heap.memory-composition`,
  carrying all of them directly.
- The one exception: the dominant-type finding's `TopTypes[0].TypeName`/`TotalBytes`. `TopTypes`
  is the full, unbounded per-type population (`projection.AllTypesBySize`) — the same volume risk
  `GCGenerationAnalyzer`'s `PerTypeGenerationProfiles` note flags — but only the single top-1
  identity is ever read, so (like `AllocationPatternSynthesisRule`'s top-long-lived-type) it's
  carried as a single `top1-type.bytes` measure plus an `EvidenceRef` for the name, not a
  per-entry observation.
- `MemoryPressureClassifier.ComputeMemoryPressureScore` (`MemoryAnalyzer`'s own sole-owner
  classifier, per the Smell A audit) is `internal` to a different assembly — its formula is
  duplicated in `MemorySynthesisRule`, same choice `AllocationPatternSynthesisRule` made for its
  own classifier; only the composite formula is ported, since the finding generator never calls
  the four individual component scores or `ClassifyPressureBand`.

### `SegmentReservationFindingGenerator`

- Step c's audit found `heap.segment-reservation` (Phase 5 step 1) already deliberately excludes
  `AddressSpacePressureRisk`/`PressureRiskReason` as Smell A judgment — but the finding generator
  reads both directly, and unlike `AllocationProfile` (a plain enum re-derivable from two
  percentages already on the observation), `PressureRiskReason` is a pre-formatted natural-
  language string embedding a threshold value (`ThirtyTwoBitPressureThresholdBytes`) that wasn't
  on the domain result *or* the observation at all — it lives only in
  `SegmentReservationAnalysisOptions`, read directly from `options` inside `Analyze`, never
  persisted anywhere the emission code could reach.
- Step d's fix, two parts: `EmitObservationsAsync` now takes `options` as a parameter (the same
  fix `AllocationPatternAnalyzer`'s own emission needed for its threshold) so
  `thirty-two-bit-pressure.threshold-bytes` can be a measure; and `dump-pointer-size`/
  `logical-heap.count`/the two ratio thresholds were added alongside it — all five are raw
  facts/config, not judgment, so carrying them directly (rather than a stored classification) is
  what let the rule re-derive `AddressSpacePressureRisk`/`PressureRiskReason` itself via a
  verbatim-ported `EvaluatePressureRisk`, the same choice `AllocationPatternSynthesisRule`/
  `MemorySynthesisRule` made for their own classifiers.
- `NearEmptyRegionCount`/`NearEmptyRegionCommittedBytes` needed no new measures at all: when the
  dump is regions-based, they're exact sums over every matched `heap.region-composition`
  observation's own `near-empty.count`/`near-empty.committed-bytes` (already emitted for every
  region-generation bucket, never a capped sample) — the same "don't duplicate what summing
  already gives you" choice `DbConnectionSynthesisRule` made for `OpenConnections`. `IsRegionsBased`
  itself needed no measure either: it's reconstructed as "did any `heap.region-composition`
  arrive," since that type is only ever emitted when the dump is regions-based.
- **The old generator's own unit tests set `AddressSpacePressureRisk`/`PressureRiskReason` as
  independent fixture fields**, including reason text that doesn't match the real formula's
  message shape at all (e.g. "Reserved address space is within 10% of the 2 GB 32-bit limit."
  versus production's "has X MB reserved (>Y MB threshold)") — achievable only because the old
  generator reads the stored fields directly rather than deriving them, the same "test-only
  decoupling" situation `AllocationPatternSynthesisRuleParityTests` hit. The parity fixtures
  compute both fields from the same `EvaluatePressureRisk` logic the rule uses instead.

---

## Plugins.Async

### `AsyncStateMachineFindingGenerator`

- Step c's audit found `type.async-state-machine`'s only `Subject` was the state-machine's own
  `TypeRef` — `DeclaringType`/`OriginatingMethod` existed only inside a private regex parser,
  never surfaced. That knocked out 3 of 4 branches (high-count's "top method", the entire
  fire-and-forget branch, async-void's "top method"), since all three need method identity.
- Step d's fix: a second `Subject` on the observation — a `Low`-fidelity `MethodRef` (no real
  parameter signature is derivable from a type name alone, so the signature is left empty
  rather than guessed) — instead of duplicating the private parser into the rule.
- Unlike `ArraySynthesisRule`, this rule **must** re-rank rather than trust emission order:
  checked (not assumed) that `AsyncStateMachineAnalyzer` emits inline per type, before its own
  post-loop sorts run — the opposite of `ArrayAnalyzer`'s structure, where emission happens in a
  separate pass over already-sorted lists. Different analyzers, different order guarantees;
  each has to be checked on its own.
- The parity test surfaced that several of the old generator's own unit-test fixtures set
  `TotalStateMachines` and `SuspendedMethodMap` to independent, mutually inconsistent values
  (e.g. 12,000 suspended instances of one method with the total left at 0) — achievable only
  because the old generator reads them as separate fields. Real production can't produce that
  combination (both are exhaustive sums over the same type population), so the parity fixtures
  were rebuilt consistent with that coupling rather than reproducing the impossible combination.

### `AsyncTaskFindingGenerator`

- Step c's audit found the analyzer emitted only `type.orphaned-task` (one per orphaned task,
  Phase 5 step 1) — the largest gap found so far in this playbook. Seven of the old generator's
  eight signals (cycle deadlock, Gen2/LOH pending tasks, unresolved TCS in Gen2/LOH, pending VTS
  in Gen2/LOH, faulted tasks, continuation-chain depth, high pending count) read
  `AsyncTaskDomainResult` fields with no observation backing at all.
- Step d's fix: one new whole-run `async.task-population` observation (no entity subject — same
  "whole-process scalar" pattern as `gc.generation-composition`), carrying all thirteen measures
  those seven signals need. `type.orphaned-task` stays as-is; the rule reconstructs `OrphanedTasks`
  by counting matched instances rather than summing a measure.
- Unlike `AsyncStateMachineSynthesisRule`, no re-ranking question arises: every signal but
  `orphaned` reads a single observation instance's scalar measures directly, and `orphaned` is a
  plain count, not a ranked list.
- The domain result's own remarks note TCS/VTS analysis runs independent of the task scan (a heap
  can have TCS/VTS instances with zero `Task` objects), so `async.task-population` is emitted on
  both the normal path and the `total == 0` early-return (`EmptyWith`) path — omitting it there
  would have silently dropped the `tcs-unresolved-oldgen`/`vts-pending-oldgen` signals for that
  case.

---

## Plugins.Gc

### `FinalizableObjectFindingGenerator`

- Step c's audit found the analyzer emitted only `finalizer.queue-entry` (one per valid queue
  entry, Phase 5 step 1). That backs 6 of the 9 signals (backlog count via a new whole-run
  observation below, retained bytes, undisposed-in-queue, and the four known-queue-pattern
  findings via per-entry Subject grouping) but not the Gen2 accumulation signal, which reads
  the whole finalizable-*type* population (`TotalFinalizableObjects`/`TotalFinalizableBytes`/
  `Gen2Count`/top type by Gen2 count) — a different population than the queue.
- Step d's fix, two parts:
  - New per-type `type.finalizable-population` observation (one per finalizable type from
    `heap.types`, not capped) — backs the Gen2 branch the same way `type.array-composition`
    backs Array's LOH-pressure branch.
  - `finalizer.queue-entry` gained `shallow.bytes` and `is-critical-finalizer` measures — the
    old generator's CriticalFinalizerObject branch read `entry.ShallowSize`/
    `entry.IsCriticalFinalizerType`, neither of which existed as a measure yet.
  - New whole-run `finalizer.queue-population` observation (`queue.count`) — the queue backlog
    signal's `FinalizerQueueCount` counts every queue entry including invalid
    (`"<unknown>"`-typed) ones that `finalizer.queue-entry` structurally cannot observe
    individually (no resolvable `Type` for a `Subject`).
- Unlike `AsyncTaskSynthesisRule`, no aggregate/top-signal cap: the old generator emits up to
  nine independent findings unconditionally, so the rule does the same — no re-ranking or
  severity-comparison logic needed at all, only per-observation-type grouping and summation.
- The old generator's own unit tests (`FinalizableObjectFindingGeneratorTests`) set
  `CriticalFinalizerQueueCount`/`TopQueueTypesByCount`/etc. as independent fields with no
  backing `TopQueueEntriesByRetainedSize` population — same "possible only because the old
  generator reads fields independently" situation the `AsyncStateMachineSynthesisRule` parity
  tests hit. The parity tests here rebuild every aggregate field from a single list of queue
  entries instead of reusing that fixture shape.

### `GCHandleFindingGenerator`

- Step c's audit found `handle.pinned` (Phase 5 step 1) only backs the debugger-follow-up
  address list — capped to `GCHandleAnalysisOptions.TopPinnedHandleAddressesToShow`, not the
  whole handle population. 5 of 6 signals read whole-population aggregates (`TotalHandles`,
  `PinnedRetainedBytes`, SOH/non-SOH pinned counts, `RefCountedHandleCount`,
  WeakShort/WeakLong generation breakdowns, dependent-handle counts) with no observation
  backing at all.
- Step d's fix, two parts: a new whole-run `handle.population` observation (no entity subject,
  same pattern as `gc.generation-composition`) carrying every aggregate the six signals need;
  and a new per-type `handle.refcounted-target` observation (one per distinct RefCounted target
  type, canonicalized from its display name the same way `type.finalizable-population` and
  `type.boxing` are, since no per-handle address survives into `TopRefCountedTargetTypes` to
  re-resolve a real `TypeRef` from) — backs the COM-interop signal's "dominant type" text.
- **Genuinely new wrinkle in this playbook entry**: `GCHandleFindingGenerator`'s thresholds
  aren't private consts baked into the generator the way every prior analyzer's are — they're
  read from `GCHandleDomainResult`'s own fields, sourced from `GCHandleAnalysisOptions`
  (runtime-configurable via `AnalysisContext.AnalyzerOptions`). Hard-coding the option defaults
  into `GCHandleSynthesisRule` as private consts, matching every other rule so far, would have
  silently dropped that configurability — a real behavior regression, not just a mechanical
  migration. `handle.population` carries all eight thresholds as measures instead, and the rule
  reads them from the observation rather than hard-coding defaults.
- Emission itself needed no changes to `Analyze()` — unlike `AsyncTaskAnalyzer`/
  `FinalizableObjectAnalyzer`, `GCHandleDomainResult` already carries every aggregate the new
  observations need, so both were emitted from the existing `EmitObservationsAsync` step using
  `result.*` directly, no weaving into the population loop required.

### `GCRootFindingGenerator`

- Step c's audit found `heap.root` (Phase 5 step 1) deliberately left out `RootFinding.RootKind`
  as a measure — its own remarks flagged this as a gap to revisit "if a synthesis rule actually
  needs to group by it," and this is exactly that: 3 of 4 signals (static-root retention,
  finalizer-queue pressure, pinned-handle accumulation) group `GCRootDomainResult.ByKind` by
  `Kind` (`StrongHandle`/`FinalizerQueue`/`PinnedHandle`/`AsyncPinnedHandle`), which `heap.root`
  alone cannot reconstruct.
- Step d's fix, two parts: a new `root.kind-summary` observation (one per `ByKind` row, bounded
  to `HeapRootKind`'s fixed small taxonomy) carrying a `kind.ordinal` measure — the same
  pattern `heap.loh-fragmentation`/`heap.segment-kind-composition` already established, and the
  same "no live enum instance, parse the string back" situation `heap.root`'s own remarks
  anticipated; and a new whole-run `root.population` observation for `TotalRoots` (includes
  roots dropped before `ByKind`/`heap.root` ever see them — null target, unresolvable metadata,
  zero-size object — so it can't be reconstructed by summing `root.kind-summary`) and the
  subgraph-walk-cap signal.
- `root.population` is guarded on `TotalRoots > 0`, skipped entirely on the no-heap-index
  `EmptyResult()` path `Analyze` already returns early for — an unconditional emission would
  have fabricated an all-zero population observation misrepresenting "no data available" as
  "zero roots measured," and broke `GCRootAnalyzerObservationTests`'
  `AnalyzeAsync_WithoutHeapIndex_EmitsNoObservations` (updated alongside the `WithHeapIndex` case
  to scope its assertions per observation type instead of assuming `heap.root` was the only one).
- Trusts `heap.root` emission order for the static-root finding's "top suspect" text
  (`TopRootsBySeverity[0]`) — same already-sorted-before-emission structure `ArraySynthesisRule`
  relies on, not `AsyncStateMachineSynthesisRule`'s inline-per-type case.

### `WeakReferenceFindingGenerator`

- Step c's audit found `weakhandle.target` (alive targets) and `weakreference.stale-wrapper`
  (stale wrappers) already back `AliveWeakTargets`/`StaleWrapperCount` by simple count — no gap
  there. But `TotalWeakHandles`/`DeadWeakTargets` (dead handles never get an observation at all:
  nothing to point a `Subject` at once the target is collected), `WeakReferenceObjectCount`/
  `WeakReferenceObjectBytes` (every `WeakReference<T>` instance, not just the stale subset
  `weakreference.stale-wrapper` observes), `DependentHandleDeadKeyCount`, and the "held only via
  weak reference" signal (count + top type) had no observation backing.
- Step d's fix, two parts: a new whole-run `weakreference.population` observation for the
  whole-process scalars (`total-handles.count`, `dead-targets.count`, `object.count`,
  `object.bytes`, `dependent-dead-key.count`) plus `held-only-detection-available` (distinguishes
  "computed, zero found" from "reachability unavailable, not computed" — same purpose as
  `HeldOnlyViaWeakReferenceDetectionAvailable`); and a new `held-only-via-weak-reference` boolean
  measure added directly to the existing `weakhandle.target` observation (not a separate
  observation type) — since every held-only target is already a `weakhandle.target` with a real
  `Subject`, grouping matched observations by type reconstructs the "most common type" narrative
  without a second per-type observation stream.
- `StaleWrapperCountIsExact` is not observed at all: the retyped analyzer's own remarks say the
  one degraded (extrapolated) case that could make it `false` "can no longer occur by
  construction," and it unconditionally constructs `StaleWrapperCountIsExact: true` — so the
  rule hard-codes the empty qualifier text as a structural guarantee, not a measured fact,
  rather than inventing an always-1.0 measure with no real second state.
- Unlike every prior playbook entry, the summary/overview finding is not gated on anything — it
  always fires, aggregating up to four independently-gated signals that are then re-sorted by
  severity-then-fixed-priority (200/150/120/100) after the fact, mirroring
  `AsyncTaskSynthesisRule`'s aggregate pattern but without a 2-finding cap.

---

## Plugins.Infra

### `SqlConnectionPoolFindingGenerator`

First of the Infra plugin's analyzers, picked up one at a time.

- Step c's audit found `infra.sql-connection-pool` already backs every numeric field the single
  finding reads — `current.size`/`max.size` reconstruct `UtilizationPercent` exactly, and pool
  count / near-capacity count are both reconstructed by counting/filtering the matched
  observations. No new observation type or measure was needed for any of that.
- The one real gap: the "worst pool" narrative's anonymised connection-string label has no home
  among `Subjects`' typed `EntityRef` kinds (`TypeRef`/`MethodRef`/`ObjectRef`/... all identify a
  CLR type, method, or heap address — none carry an arbitrary display string). Fixed by carrying
  it as `EvidenceRef.Description` on the observation itself — an existing free-text evidence slot,
  not a new field or observation type.
- Smallest playbook entry so far: no whole-run population observation, no per-type breakdown, no
  re-ranking question — the old generator's own `worst = argmax(UtilizationPercent)` scan over
  `r.Pools` translates directly into the same scan over matched observations.

### `SqlCommandFindingGenerator`

2nd of the Infra plugin's analyzers.

- Step c's audit found `infra.sql-command` emits only for active commands (one per instance) —
  `ActiveCount` is exactly reconstructable by counting those. `TotalCommands`/`DisposedCount`
  cover every scanned candidate, including disposed and unreadable-state ones that never get an
  observation at all — no way to reconstruct those two without a new observation.
- Step d's fix: one new whole-run `infra.sql-command-population` observation carrying
  `total.count`/`disposed.count` only — `ActiveCount` deliberately not duplicated as a measure,
  same "count what you can count" choice as `SqlConnectionPoolSynthesisRule`.
- Single-finding generator, same shape as `SqlConnectionPoolFindingGenerator`: no aggregation,
  no re-ranking, no per-type breakdown needed.

### `SqlTransactionFindingGenerator`

3rd of the Infra plugin's analyzers.

- Same shape and same fix as `SqlCommandFindingGenerator` one row up, with one extra whole-run
  count (`OtherCount`, transactions in some non-Active/non-Disposed state) alongside
  `TotalTransactions`/`DisposedCount` — all three added to a new `infra.sql-transaction-population`
  observation; `ActiveCount` again reconstructed by counting `infra.sql-transaction` observations.

### `DbConnectionFindingGenerator`

4th of the Infra plugin's analyzers.

- Step c's audit found `infra.db-connection` covers the full Open-state population (not
  capped — only its root-path enrichment is bounded), so `OpenConnections`/`Gen2OpenConnections`
  are exactly reconstructable by counting/filtering. But `TotalConnections` and the
  Closed/Broken/Other/Unknown-state counts never get an observation at all (only Open
  connections do), and the type-breakdown narrative needs per-type totals across *every* state,
  which the Open-only stream can't provide.
- Step d's fix, three parts: a new whole-run `infra.db-connection-population` observation for
  the four non-Open state counts and the grand total; a new per-type
  `infra.db-connection-type-population` observation (bounded to distinct connection types) for
  the type breakdown; and a root-path `EvidenceRef.Description` on `infra.db-connection` itself
  (same free-text-evidence choice as `SqlConnectionPoolSynthesisRule`'s connection-string label)
  for the Gen2 finding's "retention path for the largest one" narrative.
- `Gen0OpenConnections` deliberately not reconstructed at all: the old generator never reads it,
  so inventing a way to derive it would be scope creep beyond the playbook's own "back exactly
  what the generator reads" mandate.
- The type-breakdown loop trusts matched-observation emission order rather than re-ranking —
  `DbConnectionAnalyzer.Analyze` already sorts `ByType` descending by `TotalCount` before
  `EmitObservationsAsync`'s loop runs over it, the same structure `ArraySynthesisRule` relies on.

### `HttpObjectFindingGenerator`

5th of the Infra plugin's analyzers, largest Infra entry so far.

- Step c's audit found `infra.http-object` covers the full (uncapped) population for 5 of 7
  categories — `HttpClientCount`/`HttpWebRequestCount`/`ServicePointCount`/
  `ActiveHandlerTrackingEntryCount`/`ExpiredHandlerTrackingEntryCount` are all exactly
  reconstructable by counting matched observations grouped by `Subjects[0]`'s canonical type
  name, since `HttpObjectAnalyzer.ClassifyType` requires an exact type-name match for every one
  of them — no separate category measure needed. But `HttpWebResponseCount`/
  `HttpMessageHandlerCount` are never observed at all (no per-instance field extraction exists
  for either category); the HttpClient Gen0/Gen2 reuse-vs-churn narrative needs a generation
  split `infra.http-object` never carried; the ServicePoint low-`ConnectionLimit` narrative
  needs that field; and the HttpMessageHandler "largest contributor" narrative needs a
  per-module breakdown no per-instance stream can provide (since handler instances themselves
  aren't observed).
- Step d's fix, four parts: `generation.ordinal` and `connection-limit` measures added directly
  to `infra.http-object` (present only for HttpClient/ServicePoint respectively — same
  "presence means resolved" convention as `retained.bytes` elsewhere); a new whole-run
  `infra.http-object-population` observation for the two never-observed category counts; and a
  new per-module `infra.http-handler-module` observation (first use of `ModuleRef` as a
  Subject outside `ModuleAnalyzer`) for the "largest contributor" narrative, emitted in already-
  sorted-descending order like `ArraySynthesisRule`'s per-type lists.
- Six independent findings (`AllSignalsFireTogether` exercises all six at once), no aggregation
  or re-ranking — same "up to N independent findings unconditionally" shape as
  `FinalizableObjectSynthesisRule`/`GCRootSynthesisRule`.

### `TimerLeakFindingGenerator`

6th of the Infra plugin's analyzers, first zero-gap entry in the whole playbook.

- Step c's audit found `infra.timer` (one per distinct timer-shaped type name) already backs
  every count both findings read — no analyzer change, no new observation type, no new measure.
  Each of the six timer categories is an exact-type-name classification
  (`TimerLeakAnalyzer.ClassifyType`), and every `infra.timer` observation's `Subjects[0]`
  already carries that same type name, so re-deriving the category from the canonical name in
  the rule reconstructs every count exactly — including the oddly-named `LogicalTimerCount`,
  which the real analyzer sets equal to `TimerQueueTimerCount` (not a sum of the "real"
  disposable timer categories, despite the name) — preserved as-is, not "fixed", per the
  playbook's mandate to reproduce behavior exactly.
- The old generator's first finding also computes an evidence-based `Finding.Confidence`
  override via `EvidenceConfidence.Compute` (root-path presence/truncation, signal count) — not
  read by any Title/Narrative/Recommendation/Tags/MetricValue/MetricUnit text, and, like every
  prior rule in this playbook, left as `ConfidenceBreakdown.MeasurementCertain` rather than
  reconstructed. No observation needed to exist solely to feed a value this pass doesn't
  reproduce for any other rule either.
- Confirms the playbook's step-c audit is genuinely finding real gaps rather than always
  finding *a* gap to fix: sometimes the answer is "already fully backed."

### `WcfChannelFindingGenerator`

7th and last of the Infra plugin's analyzers.

- Step c's audit found `infra.wcf-channel` (faulted-only stream) backs `FaultedChannels` by
  simple count, but the faulted-type breakdown iterates types in **TotalCount**-descending
  order (not FaultedCount order) filtered to non-zero-faulted entries — an order the faulted-
  only stream can't reproduce without knowing each type's *total* count, not just its faulted
  count. The binding-specific fault guidance and remote-endpoint summary also had no
  observation backing at all, and the channel-count finding's five non-Faulted state counts
  plus Duplex/Session/ChannelFactory counts are never observed (only Faulted channels are).
- Step d's fix, four parts: a `binding-hint.ordinal` measure added to `infra.wcf-channel`
  (always present — `ClassifyBindingHint` never returns null); a remote-endpoint
  `EvidenceRef.Description` on the same observation; a new per-type
  `infra.wcf-channel-type-population` observation (`total.count` only, emitted in the real
  analyzer's own already-sorted-descending order) so the rule can trust iteration order for the
  breakdown the same way `ArraySynthesisRule` trusts pre-sorted lists; and a new whole-run
  `infra.wcf-channel-population` observation for every count the faulted-only stream can't
  provide.
- **Second analyzer (after `GCRootAnalyzer`) where an existing early-return path needed a
  matching fix**: `ChannelFactory<T>` count comes from whole-heap type statistics, independent
  of the channel-instance scan, so `WcfPresent` can be true with zero channels — the
  `typeStats.Count == 0` branch now still emits a factory-only `infra.wcf-channel-population`
  when `factoryCount > 0`, otherwise the `ChannelFactory<T>` finding could never fire under the
  new observation-based path in that real scenario.
- Closes out the Infra plugin: 7/7 analyzers (SqlConnectionPool, SqlCommand, SqlTransaction,
  DbConnection, HttpObject, TimerLeak, WcfChannel) now have synthesis rules.

---

## Plugins.Leaks

### `DominatorFindingGenerator`

First analyzer picked up after the Infra plugin's own 7/7.

- Step c's audit found `type.retention` (Phase 5 step 1) backs the top-dominator-type finding's
  `TypeName`/`Count` exactly, but not its retained-bytes value: the old finding always reads
  `TypeSnapshot.EstimatedRetainedBytes` (the heuristic BFS estimate), while `type.retention`'s
  `retained.bytes` measure uses the exact Lengauer-Tarjan value whenever the §D9 overlay
  succeeded for that type — a real divergence, not a naming quirk, since the finding's own text
  and severity thresholds would silently change once the overlay engages. The highly-referenced-
  objects and reference-tracking-capped findings, and the type-hotspot finding (built from
  `TopRetentionTypes`, an aggregation over `TopHighlyReferencedObjects` by type — a different
  population than `TopDominatorTypes`), had no observation backing at all.
- Step d's fix, three parts: a new `estimated-retained.bytes` measure added directly to
  `type.retention` (always the heuristic estimate, alongside the existing possibly-exact
  `retained.bytes`) so both consumers can be backed from the same observation without either
  shadowing the other; a new whole-run `type.retention-population` observation
  (`highly-referenced.count`, `approximated-reference-addresses.count`); and a new per-type
  `type.retention-hotspot` observation (one per `TopRetentionTypes` entry, canonicalized the same
  way `DbConnectionAnalyzer`'s type-population observation is).
- **Cannot trust `type.retention-hotspot`'s emission order** for the type-hotspot finding, unlike
  `type.retention`'s: the real analyzer's `TopRetentionTypes` is sorted by estimated-retained
  bytes, not by incoming-reference count, so the rule scans every matched instance for the
  highest `total-incoming-references.count` — the old generator's own linear scan translated
  directly, the same "old generator's own scan, not the emission order" situation
  `AsyncStateMachineSynthesisRule` hit for its per-type case. A parity test
  (`TypeHotspot_PicksMaxByTotalIncomingReferences_NotFirstEntry`) exercises a fixture where the
  winning entry is emitted second, not first.
- The old generator's highly-referenced-objects finding also computes an evidence-based
  `Finding.ConfidenceScore` override via `EvidenceConfidence.Compute` — left unreproduced, same
  choice `TimerLeakSynthesisRule`'s own entry made, since no rendered text reads it.

### `EventLeakFindingGenerator`

- Step c's audit found the existing `event.publisher-leak` observation (Phase 5 step 1) reads
  from `TopPublisherEvents`, a top-50-by-subscriber-count sample used by a *different* consumer —
  not the `TopLeakGroups` list this finding generator's instance/static aggregate findings
  actually read, and not capped the same way. Every per-group field the aggregate findings need
  (`IsStatic`, `InstanceCount`, `TotalSubscribers`, `MaxSubscribers`, `MaxSeverityScore`,
  `IsTimerEvent`, `IsPropertyChangedEvent`, `EventFieldName`) had no observation backing, and
  neither did the whole-run `TotalEventLeakInstances`/`InstanceEventLeakCount`/
  `StaticEventLeakCount` scalars the zero-leak finding and the groupless-fallback branch both
  need.
- Step d's fix, two parts: a new per-group `event.publisher-leak-group` observation, one per
  `TopLeakGroups` entry (exhaustive, unlike `event.publisher-leak`'s top-50 sample from a
  different list) — `EventFieldName` has no home among `Subjects`' typed `EntityRef` kinds (the
  `TypeRef` subject alone identifies only the publisher type), so it's carried as
  `EvidenceRef.Description`, the same free-text-evidence choice every prior group/aggregate-level
  analyzer made; and a new whole-run `event.publisher-leak-population` observation
  (`total.count`, `instance-leak.count`, `static-leak.count`), emitted unconditionally including
  the zero-leak case, since `Analyze` always computes these exhaustively.
- The rule ports the old generator's `AddAggregateFinding` reduction (max severity, max
  subscribers, top group by subscriber count, first-two-distinct examples) directly onto matched
  `event.publisher-leak-group` observations filtered by `is-static` — no re-ranking question
  arises, since the old generator never sorts `TopLeakGroups` either, it's a single linear-scan
  reduction. A parity test (`GroupCountFallback_UsedWhenNoGroupsOfKindSampled`) exercises the
  branch where `TopLeakGroups` samples zero groups of one `IsStatic` value even though the
  population's own count for that kind is nonzero, falling back to
  `InstanceEventLeakCount`/`StaticEventLeakCount` exactly as the old generator does.
- The old generator's aggregate findings also compute an evidence-based
  `Finding.ConfidenceScore` override via `EvidenceConfidence.Compute(topEvidence)` — left
  unreproduced, same choice `TimerLeakSynthesisRule`/`DominatorSynthesisRule` made, since no
  rendered text reads it; `topEvidence` (the per-instance root-path evidence used only for that
  override) needed no observation backing as a result.

### `ReferenceChainFindingGenerator`

- Step c's audit found the existing `type.root-reachability` observation (Phase 5 step 1) is
  emitted inline in the first per-type pass, before the multi-sample-enrichment pass that
  computes `SampleCount`/`RetainedSampleCount`/`DominantSampleRootKind` — and only for types with
  a resolvable sample address, skipping the ones `NoSampleAddressCount` counts. None of the
  enrichment fields the finalizer-only-retention finding needs existed as observations, and
  neither did the whole-run `AnalyzedSamples`/`RetainedSamples`/`RetainedPercent`/
  `TraversalLimitedSamples` scalars three other findings read, nor `SharedRootGroups` (a
  cross-type join over `TopTypeSampleTraces`, computed after both per-type passes).
- Step d's fix, three parts: a new per-type `reference-chain.type-sample` observation, emitted
  from the second (enrichment) pass over the same `TopTypeSampleTraces` population — a
  genuinely different, richer set than `type.root-reachability`'s (covers every pending type,
  not just ones with a sample address) — carrying the enrichment fields, with
  `DominantSampleRootKind` (a free-form string, not a fixed enum like `HeapRootKind`) as
  `EvidenceRef.Description`; a new whole-run `reference-chain.population` observation, emitted
  unconditionally including the no-samples case since `Analyze` always computes these
  exhaustively; and a new `reference-chain.shared-root-group` observation, one per
  `SharedRootGroups` entry, the **first per-analyzer observation to use `ObjectRef` (a heap
  address) as its sole `Subject`** rather than a `TypeRef` — the shared root is an object, not a
  type — with `RootKind` and the comma-joined type-name list as two separate `EvidenceRef`
  entries (neither has a home among `Subjects`' typed `EntityRef` kinds or a numeric `Measure`).
- Both list-shaped findings trust emission order rather than re-ranking: `TopTypeSampleTraces` is
  already sorted descending by type size before either pass runs (the old generator's own filter
  loop doesn't re-sort it either), and `BuildSharedRootGroups` already sorts its output
  (type-count desc, then address asc) before this analyzer's emission loop runs over it — same
  already-sorted-before-emission structure `ArraySynthesisRule` relies on.
- Two boundary fixtures had no prior test coverage and were added to the parity test suite (not
  the old generator's own unit tests, per the playbook's "only add a fixture if a threshold
  constant genuinely has none yet" rule): `TraversalLimitedSamplesBelowWarningThreshold` (10%,
  Info) and `TraversalLimitedSamplesAtWarningThreshold` (exactly 20%, Warning) — the 0-traversal-
  limited case was covered, but not the >0-and-around-the-threshold cases.

### `StaticRootFindingGenerator`

- **Deliberately does not fix `StaticRootAnalyzer`'s own known, documented bug**
  (`HeapRootKind.StaticVar` never fires on ClrMD 4, so this analyzer produces zero findings on
  every real dump today) — same "port the consumption logic, not the fix" choice the analyzer's
  own retyping made, and the same choice `StaticRootLeakDetector`'s retyping made per user call.
  Nearly a zero-gap entry like `TimerLeakFindingGenerator`: the existing `heap.static-root`
  (Phase 5 step 1) already backs everything except the whole-run scalars.
- Step c's audit found `heap.static-root` covers *every* analyzed root, while
  `RootCount`/`TotalRetainedBytes` are scoped to the *significant* subset (`IsSignificant`, a
  config-threshold filter no observation carries) — not reconstructable by counting/summing that
  stream. The severity check's `topRoots[0].TotalMemoryImpact` needed no new field, though: a
  root large enough to be the overall max is always >= `SignificantMemoryThresholdBytes`, hence
  always significant, so it's exactly the max `heap.static-root` `retained.bytes` across all
  analyzed roots.
- Step d's fix: one new whole-run `static-root.population` observation (`root.count`,
  `retained.bytes`), emitted unconditionally including the zero-significant-roots case (also
  currently unreachable, same bug), since both scalars are exhaustively computed either way.
- **Cannot trust observation order for the "largest root" reconstruction**, unlike several prior
  rules' per-type lists: `TopRootsByRetainedBytes` is explicitly sorted descending before the old
  generator ever reads it, but `heap.static-root` is emitted inline during root enumeration — an
  arbitrary, not size-sorted, order. The rule scans every matched `heap.static-root` observation
  for the max `retained.bytes` rather than trusting first-emitted, and a parity test
  (`LargestRootObservedOutOfOrder_StillEmitsCriticalFinding`) exercises observations emitted in
  the reverse of the sorted-list order to prove it.
- The old generator's single retention finding also computes an evidence-based
  `Finding.ConfidenceScore` override via `EvidenceConfidence.Compute(topEvidence)` — left
  unreproduced, same choice every prior Leaks-plugin rule in this playbook made.

---

## Plugins.Runtime

### `CrashFindingGenerator`

- Step c's audit found `runtime.active-exception` (Phase 5 step 1, one per active exception
  instance) backs the fallback path's `ActiveExceptionTypeCounts` reconstruction (count matched
  Subject types), but nothing else: `TotalExceptions`/`ActiveExceptions`/unique-type count were
  whole-run scalars with no observation at all, and — the real gap — `TopCrashThreadCandidates`
  (used for the primary finding's preferred "top thread's exception type" text) groups active
  exceptions **by thread**, an axis `runtime.active-exception`'s Subject (the exception type
  alone) structurally cannot recover. Counting matched Subject types gives type-level totals,
  never which thread a given active exception belongs to.
- Step d's fix, two parts: a new whole-run `runtime.crash-population` observation
  (`total.count`/`active.count`/`unique-types.count`), emitted unconditionally including the
  zero-exception case; and a new `runtime.crash-thread-candidate` observation, one per
  `CrashThreadCandidate` — bounded by distinct thread-with-active-exception count (process-scale,
  never heap-scale, so no volume risk despite being a new per-entity observation), already sorted
  descending by `ActiveExceptionCount` before the emission loop runs (the sort was hoisted out of
  the final `return` expression into its own variable, reused by both the observation loop and
  the domain result, rather than duplicated).
- The rule trusts `runtime.crash-thread-candidate` emission order for the top candidate — same
  already-sorted-before-emission structure `ArraySynthesisRule` relies on — and only falls back
  to the type-counting reconstruction over `runtime.active-exception` when no candidate
  observations exist at all, mirroring the old generator's own two-tier fallback
  (`TopCrashThreadCandidates[0]` → `ActiveExceptionTypeCounts.OrderByDescending(...).First()` →
  `"Unknown"`).
- The old generator's active-exceptions finding also computes a weighted-average
  `Finding.ConfidenceScore` (candidate `OriginalStackTraceConfidence` tiers, weighted by
  `ActiveExceptionCount`) plus a `Caveats` tier-distribution summary — left unreproduced, same
  choice every prior rule in this playbook made for evidence-based confidence overrides, since
  neither is part of the parity surface these rules verify
  (Title/Narrative/Recommendation/Severity/Category/Tags/MetricValue/MetricUnit).

### `HeapTopologyFindingGenerator`

- Step c's audit found `heap.segment-kind-composition` (Phase 5 step 1, one per SOH/LOH/POH/
  Frozen/Unknown row) already backs `TotalCommittedBytes`/`TotalSegments`/every per-kind
  bytes-and-segment-count/`LohPercent`/`PohPercent` by summing (or sum-then-divide) across its 5
  rows — no gap there at all, the largest zero-new-whole-run-measure entry in this playbook so
  far. The real gaps: per-kind fragmentation (`SohFragmentedBytes`/`LohFragmentedBytes`/
  `PohFragmentedBytes`/`FrozenFragmentedBytes` — a genuinely different metric from
  `committed.bytes`/`reserved.bytes`, computed from a per-segment `committed − used` sum this
  observation never carried), and two whole-run facts with no per-kind home at all:
  `IsServerGc` and the logical-heap-skew finding's max/min extremes over
  `PerLogicalHeapSummaries`.
- Step d's fix, two parts: `fragmented.bytes` added to the SOH/LOH/POH/Frozen rows only — no
  `UnknownFragmentedBytes` field exists on the domain result to read for the fifth (the local
  accumulator is computed in `Analyze` but never returned), so that row's measure is omitted
  rather than fabricated as zero; and a new whole-run `heap.topology-population` observation for
  `is-server-gc`/`logical-heap.count` plus `logical-heap.max-bytes`/`min-bytes` — the two extremes
  computed by the analyzer itself from the full `PerLogicalHeapSummaries` list, not observed per
  logical heap, since the finding only ever reads the two extremes and never any individual
  heap's identity (no natural `EntityRef` fits a logical-heap-index anyway).
- **The old generator's own unit tests set `TotalCommittedBytes`/`LohPercent`/`PohPercent` as
  independent fixture fields, decoupled from `KindSummaries`** (most fixtures leave
  `KindSummaries` empty entirely while asserting on percentages/severities) — achievable only
  because the old generator reads the given whole-run fields directly rather than deriving them
  from the per-kind rows, the same "test-only decoupling" situation
  `AllocationPatternSynthesisRuleParityTests`/`SegmentReservationSynthesisRuleParityTests` hit.
  The parity fixtures derive every whole-run scalar from the same per-kind bytes the rule
  reconstructs its own totals from, and always populate all 5 `KindSummaries` rows (matching
  `HeapTopologyAnalyzer.Analyze`'s own always-5-rows shape) rather than the old fixtures'
  sometimes-single-row shortcut.

### `JitFindingGenerator`

- Step c's audit found `runtime.jit-activity` (Phase 5 step 1) already backed 6 of the finding
  generator's signals in full (JIT heap bytes, JIT manager count already missing though, tiered/
  managed/unmanaged/dynamic-method frame counts). Missing: `JitManagerCount`,
  `LargeMethodThresholdBytes` (already a domain-result field, just not yet a measure),
  `MaxThreadFrameDepth`/`MaxThreadFrameDepthOSThreadId`, and the large-methods finding's
  `TopLargestMethods[0]` (`Signature`/`HotSize`/`ColdSize`) plus `TopLargestMethods.Count`.
- Step d's fix: `jit-manager.count`/`large-method.count`/`large-method.threshold-bytes`/
  `max-thread-frame-depth.count`/`max-thread-frame-depth.os-thread-id` added as plain whole-run
  measures (no gap-closing subtlety — straightforward raw facts); and, for the single top
  method, `top-method.hot-size-bytes`/`cold-size-bytes` as measures plus an `EvidenceRef`
  carrying its pre-formatted `Signature` string. **Still no per-method observation and no
  `MethodRef`** — `JitAnalyzer`'s own Phase 5 step 1 remarks already ruled that out (no SDK
  helper splits a `ThreadStackFrameRef`'s display string into name/parameter-types the way
  `EntityCanonicalizer` does for bare type names, and hand-rolling one risks the exact
  "regex-drift" bug class the `AsyncStateMachineAnalyzer` re-audit already hit) — evidence text
  doesn't need identity resolution the way a `Subject` would, so the free-text description was
  sufficient without revisiting that decision.
- **The old generator's own code comment claims signals are "sorted by severity descending, then
  priority descending" before being appended — the actual code never sorts them.** Each signal
  carries an unused `Priority` field, but the final loop just iterates `signals` in the order the
  six if-checks ran (JIT heap bloat → unmanaged ratio → tiered → large methods → dynamic codegen
  → deep stack), which is *not* priority-descending (400, 120, 80, 140, 100, 160). The rule
  preserves the real (stale-comment-contradicting) behavior — insertion order — not the
  documented-but-never-implemented sorted order.

### `ModuleFindingGenerator`

- Largest observation-count entry so far by type count, not by volume: only
  `runtime.module-heap-footprint` (Phase 5 step 1, one per `TopModulesByHeapMemory` entry)
  existed, backing exactly one of the finding generator's eight signals (heavy modules) in full.
  The other seven — version conflicts (+ per-conflict-group version details), type-density
  anomalies, dynamic/anonymous module accumulation, cross-domain loads, AssemblyRef mismatches,
  and unknown-identity duplicates — were all unobserved.
- Step c/d's fix: one new whole-run `runtime.module-population` observation (module/conflict/
  dynamic/domain/anonymous/unknown-identity counts plus the heavy-module-warning byte threshold),
  plus **five new per-module observation types** — `runtime.module-conflict-group`,
  `-type-density`, `-cross-domain-load`, `-assemblyref-mismatch`, and `-unknown-identity`.
  **None of these needed a bounded-selection-in-the-analyzer trick, a single-top-identity
  shortcut, or emission-order caution** the way several prior playbook entries did: every list
  `ModuleFindingGenerator` reads is module-scale (loaded-assembly count, typically tens to low
  hundreds in a real process), never heap-object-scale, so each was safe to observe fully,
  per-entry — the same reasoning that already justified the pre-existing, uncapped
  `runtime.module-heap-footprint`.
- `DynamicModules` (from a single global `EnumerateModules()` pass) and `TotalDynamicModules`
  (summed across a separate per-AppDomain enumeration — the same physical dynamic module counted
  once per domain it's loaded into) are genuinely different fields despite the similar names,
  both carried as distinct measures on `runtime.module-population` — the same "similar name,
  different formula" trap `GCGenerationSynthesisRule` hit for `loh.fraction` vs. `loh.percent`.
- Free-text `EvidenceRef` carries string data with no home among `Subjects`/`Measures`:
  `runtime.module-conflict-group`'s comma-joined `Versions` list, and
  `runtime.module-assemblyref-mismatch`'s combined `"{RequiredAssemblyName} {RequiredVersion}
  (loaded: {LoadedVersions})"` text (three string fields the finding always renders together, so
  one evidence slot suffices) — same pattern used throughout this playbook for string data a
  typed `EntityRef` can't carry.
- Added five new boundary fixtures the old generator's own tests never exercised (all using
  empty lists/zero counts by default): heavy modules above/below threshold, type-density
  anomaly, anonymous-module accumulation, unknown-identity duplicates, and — most notably —
  version conflicts *with* `ConflictDetails` populated, since every existing
  `versionConflictGroups`-only fixture left `ConflictDetails: []`, meaning the finding's own
  "Conflicts: ..." detail-listing branch had never actually been exercised by any test before
  this migration.

---

## Plugins.Threads

### `HangFindingGenerator`

- `thread.wait-state` (Phase 5 step 1, one per `TopWaitingThreads` entry) only backed the
  per-thread `locks-held.count` measure; every scalar the generator's one finding actually reads
  — `WaitingThreadCount`, `TotalAliveThreads`, `WaitingPercent`, `QueuedWorkItems`, and every
  input `HangHealthClassifier.ComputeHealthScore` needs (`PendingTasks`, `FaultedTasks`,
  `TotalTaskContinuations`, the runtime thread-pool max/active/CPU counters, and
  `HighThreadPoolThreshold`) — was unobserved.
- Step c/d's fix: one new whole-run `runtime.hang-pressure` observation carrying all of the
  above (same whole-run shape as `gc.generation-composition`), plus one new per-thread measure,
  `circular-wait-candidate.count`, added to the existing `thread.wait-state` observation.
- `HangHealthClassifier.ComputeHealthScore` is a genuine Stage-1 "sole owner" classifier
  (`internal` in `DumpDetective.Plugins.Threads`, inaccessible from Reporting) — its formula is
  duplicated verbatim in `HangSynthesisRule`, the same precedent as
  `AllocationPatternPressureClassifier`/`MemoryPressureClassifier`. The one input the classifier
  reads that has no natural whole-run measure — how many of `TopWaitingThreads` are "MonitorWait
  while holding a lock" (its deadlock-candidate signal) — is *not* re-derived from a leaked
  `WaitType` string at synthesis time; `circular-wait-candidate.count` is a raw per-thread fact
  the analyzer itself computes at emission time (it still has the real `WaitType` enum in scope),
  and the rule just sums the flag across matched observations. This keeps the categorical
  `WaitType` string itself out of `Measures`, consistent with the pre-existing
  `thread.wait-state` registry note, while still letting the classifier's own arithmetic run
  unmodified.
- Old test suite's four fixtures (waiting-percent Critical/Warning/Info, queued-work-items
  Warning) all carried zeroed-out health-score inputs — no fixture ever exercised the circular-
  wait, thread-pool-saturation, or task-backpressure penalty branches. Added five new fixtures
  covering each: circular-wait candidates (and a non-circular-wait control proving the flag,
  not just wait-count, gates the penalty), saturation with/without CPU starvation, task
  backpressure above `HighThreadPoolThreshold`, faulted tasks, continuation backlog, and one
  all-signals-firing-together case.

### `LockGraphFindingGenerator`

- Both `lock.contested` and `thread.blocked` (Phase 5 step 1) already emit one observation per
  entry with no cap, covering every contested lock and every deadlock candidate — so
  `ContestedLockCount`/`DeadlockCandidateCount` don't need dedicated measures at all;
  `LockGraphSynthesisRule` reconstructs both by counting matched observations of each type. This
  is the same "count instead of duplicate" move `ArraySynthesisRule`'s pre-sorted-list trust
  established, one step further: no data at all needs re-emitting when the observation is
  already unbounded and per-entry.
- `TotalHeldLocks` is the one scalar with no observation to stand in for it — uncontested locks
  (the majority of `TotalHeldLocks` in a healthy dump) get no per-lock observation, only
  contested ones do. Step c/d's fix: one new whole-run `runtime.lock-pressure` observation
  carrying just that one measure, `held-lock.count`.
- `DeadlockCandidateDetails`' `ManagedThreadId`/`OsThreadId` pair (used to build the "Candidate
  thread(s): thread N (OS: M)" evidence suffix) already has a home: `thread.blocked`'s own
  `Subjects[0]` is the full `ThreadRef`, so no new measure was needed for it either — this
  migration's only real gap was the whole-run scalar above.
- Old test suite's five fixtures never combined "1 deadlock candidate" with a distinguishing
  assertion from "2 deadlock candidates" beyond the `>= 2` recommendation threshold itself; added
  one new fixture (`SingleDeadlockCandidate_StillWarningNotCritical`) confirming a single
  candidate still gets the "reduce lock scope" recommendation, not the cycle-detection one.

### `ThreadFindingGenerator`

- Unlike `LockGraphFindingGenerator`'s counting move, `BlockedThreadCount`/
  `LockHoldingThreadCount`/`ActiveProcessingThreadCount` were *not* reconstructed by counting
  matched `thread.state` observations, even though `thread.state` is also unbounded/per-entry.
  Reason: `ThreadsWithActiveExceptionsCount` is incremented for every thread — alive or not —
  while `thread.state` is only ever emitted for alive threads (`ProcessThread`'s exception
  counter sits above its `IsAlive` check). Since that one field can't be derived by counting
  `thread.state` matches, all the summary-finding scalars were kept together in a single new
  whole-run `runtime.thread-triage` observation instead of splitting derivation strategy
  per-field — one dead thread with a lingering exception record would otherwise silently
  undercount a real production run without a test ever catching it.
- `ActiveProcessingThreadCount` (needed for the "zero active threads" hang finding) has no
  existing field at all pre-migration — the old generator computes it by summing
  `TopActiveThreadHotspots`' per-frame counts. Ported the running total into
  `ThreadCategorization` itself, incremented in lockstep with `TrackTopFrameHotspot`'s own
  early-return (a thread with zero captured frames is tracked in neither), so it always equals
  that sum exactly, including the old code's own "no frames → not counted" quirk — preserved,
  not fixed, consistent with the `StaticRootLeakDetector` migration's precedent for
  pre-existing analyzer quirks.
- The finalizer thread's detail (`FinalizerThreadBlocked`/`FinalizerManagedThreadId`/
  `FinalizerLockCount`) got its own single-subject `runtime.finalizer-thread` observation
  (emitted at most once, only when a finalizer thread was found) rather than new whole-run
  measures — `FinalizerManagedThreadId` needs a real `ThreadRef` identity for the "managed
  thread N" evidence text, not a bare nullable int a whole-run measure can't carry.
- Old test suite's eleven fixtures set domain-result scalars directly, with no matching
  per-thread snapshot data — same "no underlying detail list" gap `LockGraphFindingGenerator`'s
  fixtures had for `ContestedLockCount`. Added one new fixture
  (`FinalizerPresentButNotBlocked_NoFinalizerFinding`) proving a present-but-unblocked finalizer
  thread (an observation exists, but `is-blocked` is 0) correctly suppresses the finalizer
  finding — the old fixtures only ever tested "no finalizer at all" vs. "finalizer blocked."

### `ThreadStackClusterFindingGenerator`

- `thread.stack-cluster` already existed but is filtered by `MinClusterSize`, so
  `AliveThreadCount`/`UniqueClusters`/`DiversityPercent` — all computed from the *unfiltered*
  cluster population — have no per-cluster stand-in. Closed with one new whole-run
  `runtime.thread-cluster-diversity` observation.
- The dominant-cluster finding needed the raw `Signature` string and, when present, the
  `FrameworkPattern` classification name — both free-text with no home among numeric `Measures`.
  Rather than add measures, both went into `thread.stack-cluster`'s own `Evidence` list
  (`Evidence[0]` = signature, always present; `Evidence[1]` = framework-pattern name, only when
  `ClassifyFrameworkPattern` matched) — first observation type in this project to carry evidence
  text for something other than a mismatch/conflict message.
- `ClassifyFrameworkPattern` is `internal` to `ThreadStackClusterAnalyzer` itself (not even a
  separate classifier class) and backed by a private lookup table (`FrameworkPatterns`) —
  inaccessible from Reporting either way. Rather than duplicate the lookup table (the
  `AllocationPatternPressureClassifier` precedent), `ThreadStackClusterSynthesisRule`
  reconstructs only the boolean fact it actually needs (`FrameworkPattern != null`) from
  `Evidence.Count > 1` — the classifier's actual pattern-matching logic never crosses into
  Reporting at all, a narrower move than every prior "duplicate the classifier" migration.
- `thread.stack-cluster` observations are emitted in `TopClusters`' own already-sorted-descending
  order, so the rule trusts the first matched observation as the dominant cluster — same
  already-sorted-before-emission structure `ArraySynthesisRule` relies on.
- Old test suite's two fixtures never covered the "no clusters at all" or "dominant cluster below
  50%" cases; added `NoClusters_OnlyEmitsSummaryFinding`,
  `DominantClusterBelowFiftyPercent_NoWarningEvenWithoutFrameworkPattern`, and
  `HighDiversity_SummaryIsInfo` to round those boundary cases out.
