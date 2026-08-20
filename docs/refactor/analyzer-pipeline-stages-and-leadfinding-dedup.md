# Analyzer pipeline: stage model and LeadFinding duplication

Status: proposal / discussion, not yet executed.

## Current pipeline (as-built, 4 stages)

1. **Analyzer** — `IAnalyzer.AnalyzeAsync` → `AnalyzerDomainResult`. Pure data, JSON-able. No formatting or judgment.
2. **Finding generation** — `FindingGenerationPipeline` (`DumpDetective.Analysis/Pipeline`) runs one `IFindingGenerator` per analyzer name (`Core.Abstractions.IFindingGenerator`, implementations in `DumpDetective.Reporting/FindingGenerators/*`) → attaches `InsightFinding[]` to `AnalyzerRunResult.Findings`. This is the only stage that should own severity/threshold judgment.
3. **Section building** — `IAnalyzerSectionBuilder.Build(AnalyzerDomainResult)` (`DumpDetective.Reporting/SectionBuilders/*`) reads the **same raw `AnalyzerDomainResult` independently of stage 2** and produces `AnalyzerDetailSection` (blocks, charts, compact tables, key metrics, and — problematically — its own `SectionLeadFinding`). Should be presentation-only: no new judgment, just shaping already-decided facts into narrative/table/chart form.
4. **Report assembly** — `ReportSectionAssembler` / `ReportDomainProjector` / `ReportSerializer` merge sections + findings into `AnalysisReportDocument`. In principle purely mechanical (ordering, grouping, serialization), but currently contains some judgment too (domain inference, info-suppression rules) that arguably belongs upstream.

## The core problem: stages 2 and 3 duplicate judgment

Stage 2 (`IFindingGenerator`) and stage 3 (`IAnalyzerSectionBuilder`) are two **independent** passes over the same `AnalyzerDomainResult`. They are only reconciled at assembly time by analyzer-name matching. In practice, many section builders re-derive their own severity/threshold logic to populate `SectionLeadFinding`, duplicating what the corresponding `IFindingGenerator` already computed. This is the same failure mode as the async-state-machine regex-drift regression (see `project_asyncstatemachine-reaudit-20260814` memory): two independently-maintained copies of the same rule silently drift apart when one is tuned and the other isn't.

### Full audit: every `SectionBuilder` that constructs `SectionLeadFinding` inline

`grep "new SectionLeadFinding(" src/DumpDetective.Reporting/SectionBuilders` finds exactly 8 files (of 39
section builders total). Every one was compared against its `IFindingGenerator` counterpart. Only 2 of 8
are "just" duplicated-but-currently-consistent; the other 6 are **actively divergent** — the builder and
generator disagree on what triggers a warning, or on which field drives severity, meaning the report's
`LeadFinding` header can already show a different (often weaker) signal than the actual computed findings.

| Analyzer | Builder logic (`LeadFinding`) | Generator logic (`Findings`) | Verdict |
|---|---|---|---|
| LOH Fragmentation | `LohFragmentationSectionBuilder.cs:127-144` — ≥30%/≥15% on `FragmentationPercent` | `LohFragmentationFindingGenerator.cs:16-18` — same bands | Duplicated, currently consistent |
| Finalizable Object | `FinalizableObjectSectionBuilder.cs:77-93` — `FinalizerQueueCount > 10_000` Critical / `> 1_000` Warning | `FinalizableObjectFindingGenerator.cs:10-11,27` — same numbers (`Gen2WarningThreshold`/`Gen2CriticalThreshold`) but applied to **`Gen2Count`**, a different field | **Divergent basis** — coincidentally identical magic numbers on the wrong metric |
| Segment Reservation | `SegmentReservationSectionBuilder.cs:27-46` — `ReservedToCommittedRatio` vs. domain-supplied `RatioHigh/MediumPressureThreshold` | `SegmentReservationFindingGenerator.cs:21-27` — driven by `AddressSpacePressureRisk` + 32-bit check; ratio thresholds never referenced | **Divergent** — two unrelated decision trees for the same analyzer |
| Crash / Exception | `ExceptionAnalysisSectionBuilder.cs:32-47` — fires only when `ActiveExceptions > 0` | `CrashFindingGenerator.cs:33-37` — also emits a Warning when `TotalExceptions > 0` with none active | **Gap** — that Warning case is silently absent from `LeadFinding` |
| Leak Candidate | `LeakAnalysisSectionBuilder.cs:40-44` — reads `LeakCandidateRecord.Severity`, a field already computed in the domain layer | `LeakCandidateFindingGenerator.cs` — reads the same `Severity`/`Classification` fields | Consistent — benign parallel selection, not re-derived logic |
| Async Task | `AsyncAnalysisSectionBuilder.cs:29-40` — only checks `MaxContinuationDepth >= 15` | `AsyncTaskFindingGenerator.cs` — 7 other signals (cycle detected, orphaned tasks, faulted tasks, pending tasks, Gen2/LOH TCS leaks), several of which reach Critical | **Gap** — a Critical async-deadlock cycle finding can exist while `LeadFinding` still shows (or omits) a lesser continuation-depth Warning |
| Lock Graph | `LockGraphSectionBuilder.cs:137-152` — fires on `DeadlockCandidateCount > 0` | `LockGraphFindingGenerator.cs:16-17` — severity driven by `ContestedLockCount > 0` | **Divergent trigger condition**, not just different thresholds |
| Hang | `HangSectionBuilder.cs:25-42` — fires on `IsStarved \|\| HealthScore < 50`, always "Warning" | `HangFindingGenerator.cs:18-20` — `WaitingPercent >= 80` → Critical, `>= 50` or `QueuedWorkItems > 500` → Warning, else Info | **Divergent** — generator can reach Critical; builder's `LeadFinding` never surfaces higher than Warning |

Additional lower-priority, non-`LeadFinding` judgment spotted during the audit (not full-blown duplication,
but still presentation code making its own classification call rather than reading one from the domain/finding
layer):
- `MemoryAnalysisSectionBuilder.cs:40-42` — inline `pressureBand` (High ≥75/Medium ≥50/Low) is a **narrative
  text label only** (no `LeadFinding`), and uses different cut points than `MemoryFindingGenerator`'s
  `MemoryPressureScore >= 70` Warning threshold. Lower risk since it's descriptive text, not a report contract
  slot, but still worth aligning.
- `SectionBuilderBase.cs:19-25` (`BuildConfidenceBand`) — a confidence-score → band/symbol ladder reused by
  many builders for narrative confidence blocks. Takes a raw `score` parameter rather than reading
  `InsightFinding.EffectiveConfidenceScore`; not incorrect today but a second place the same banding math
  could drift from `NormalizeSectionContractSlots`'s own confidence-symbol ladder (`ReportSectionAssembler.cs:216-221`,
  which is itself a **third**, near-identical copy of the same 4-tier band/symbol table).
- `LeakAnalysisSectionBuilder.cs:163-167` (`GetImpactBand`) — separate size-based Low/Medium/High/Critical
  band shown per leak-candidate card, orthogonal to `Severity`. Not a duplication bug, but a card can show
  "Low" impact next to "Critical" severity with no explanation of why they differ — a UX clarity issue more
  than an architecture one.

## The fix already has scaffolding: `NormalizeSectionContractSlots`

`ReportSectionAssembler.NormalizeSectionContractSlots` (`ReportSectionAssembler.cs:169-291`) already contains logic to **derive** `SectionLeadFinding` from the highest-severity `InsightFinding` on the matching `AnalyzerRunResult`:

```csharp
SectionLeadFinding? leadFinding = section.LeadFinding;
...
if (leadFinding is null && matchedRun is { } run && run.Findings.Count > 0)
{
    // picks top-severity InsightFinding, maps to SectionLeadFinding, computes confidence symbol
}
```

This only runs `if (leadFinding is null)` — i.e. it's a fallback for section builders that *don't* set `LeadFinding` themselves. Every builder listed above sets `LeadFinding` directly inside `Build()`, so this derivation path is dead code for them, and their independently-duplicated threshold logic wins every time.

**Consequence: the infrastructure to fix this already exists, and this is a correctness fix, not just cleanup.** 6 of the 8 audited builders can already show a `LeadFinding` that disagrees with — or is strictly weaker than — what the analyzer's own `IFindingGenerator` determined. The remaining work is subtractive, not additive:

1. Delete the inline `SectionLeadFinding` construction (and its duplicated/divergent threshold logic) from each of the 8 section builders in the table above.
2. Let `NormalizeSectionContractSlots`'s existing derivation-from-`Findings` path become the sole source of `LeadFinding`.
3. Suggested triage order — fix divergent-trigger cases first, since those are live correctness bugs (report can under-report severity), before the purely-duplicated (currently-consistent) cases which are lower urgency:
   - P0 (wrong signal today): Hang, Lock Graph, Finalizable Object, Segment Reservation
   - P1 (silently drops a real finding): Crash/Exception, Async Task
   - P2 (duplicated but consistent — cleanup, not a bug): LOH Fragmentation, Leak Candidate
4. While touching `SectionBuilderBase.BuildConfidenceBand`, note there are now **three** independent copies of essentially the same confidence-score → band/symbol ladder: `SectionBuilderBase.cs:19-25`, `ReportSectionAssembler.cs:216-221` (inside `NormalizeSectionContractSlots`), and `LeakAnalysisSectionBuilder.cs:27` inlines its own copy too. Worth consolidating into one shared helper as part of this pass, since it's the same drift risk at a smaller scale.
5. Re-run report golden/snapshot tests per analyzer to confirm the derived `SectionLeadFinding` text (`Title`/`Summary`/`Recommendation` sourced from `InsightFinding.Title`/`Evidence`/`Recommendation`) doesn't regress narrative quality — the wording between hand-written `SectionLeadFinding.Summary` and `InsightFinding.Evidence` is not always identical today, so this is a behavior-visible change, not a pure refactor. For the P0/P1 cases, the visible severity is *expected* to change (that's the bug fix) — flag those explicitly to whoever reviews the report diff so it isn't mistaken for a regression.

## P0 fix plan — Hang, Lock Graph, Finalizable Object, Segment Reservation

These four are live correctness bugs today (report's `LeadFinding` can show a wrong or weaker
severity than the analyzer actually determined) and are independent of the broader stage-boundary
discussion below — safe to fix now, without waiting on the rename/boundary decision.

**Change, per builder:** delete the local `SectionLeadFinding? leadFinding = ...` block (condition +
construction) and pass `LeadFinding: null` (or omit it) in the returned `AnalyzerDetailSection`, so
`NormalizeSectionContractSlots`'s existing fallback (`ReportSectionAssembler.cs:200-231`) becomes the
sole source, deriving it from the top-severity `InsightFinding` already produced by the matching
`IFindingGenerator`.

1. **`HangSectionBuilder.cs:25-42`** — remove the `IsStarved || HealthScore < 50` block. `HangFindingGenerator`
   already covers both signals (`WaitingPercent`, `QueuedWorkItems`) with a wider, correctly-ordered
   severity ladder (`HangFindingGenerator.cs:18-20`); nothing needs to move into the generator.
2. **`LockGraphSectionBuilder.cs:137-152`** — remove the `DeadlockCandidateCount > 0` block. `LockGraphFindingGenerator.cs:16-17`
   already keys severity off `ContestedLockCount`; the derived `LeadFinding` will use its
   `Recommendation`, which already special-cases `DeadlockCandidateCount >= 2` (`LockGraphFindingGenerator.cs:43-44`),
   so no information is lost.
3. **`FinalizableObjectSectionBuilder.cs:76-93`** — remove the `FinalizerQueueCount > 10_000 / > 1_000` block.
   Confirm before deleting: `FinalizableObjectFindingGenerator` currently only emits a Gen2-count-based
   finding and a queue-retained-*bytes*-based finding — neither is keyed on raw `FinalizerQueueCount`. If a
   large finalizer queue with low Gen2 residency and low retained bytes is a scenario worth surfacing (it
   may be, e.g. Gen0/Gen1 finalizer backlog), add a third `FinalizerQueueCount`-based signal to the generator
   *before* deleting the builder's version, so that case isn't silently dropped entirely rather than just
   being made consistent.
4. **`SegmentReservationSectionBuilder.cs:26-52`** — remove the ratio-threshold block. Confirm before deleting:
   `SegmentReservationFindingGenerator` doesn't currently use `RatioHighPressureThreshold` /
   `RatioMediumPressureThreshold` at all. If the ratio-crossing-a-configured-threshold case is meant to be a
   distinct signal from `AddressSpacePressureRisk`, port that check into the generator first (mirroring the
   builder's `critical`/`Warning` split at `SegmentReservationSectionBuilder.cs:29,42`) before removing it
   from the builder, otherwise that signal disappears from the report entirely, not just from `LeadFinding`.

**Order of operations:** for #1 and #2 the generator is already a strict superset, so the builder block
can simply be deleted. For #3 and #4, land the generator-side addition (if judged worth keeping) *before*
deleting the builder logic, in a separate commit, so `git bisect`/review can distinguish "moved logic" from
"deleted logic."

**Verification per analyzer:** run/inspect the report for a dump that currently triggers each builder's
`LeadFinding` path, confirm the post-change `LeadFinding` (now generator-derived) still fires and with the
severity you'd expect from `IFindingGenerator`'s own logic — this is the behavior-visible check called out
in item 5 above, scoped to just these four.

## Stage 1 purity audit — Analyzer domain results are not pure data either

The dedup problem isn't confined to stage 2 vs. stage 3. `AnalyzerDomainResult` records (stage 1
output) already bake in composite judgment scores and pre-curated, multi-criteria-ranked lists —
i.e. stage 1 is already doing some of stage 2's job, before `IFindingGenerator` or
`IAnalyzerSectionBuilder` ever run. Two distinct smells found, audited across all 36 files in
`DumpDetective.Analysis/Models/`.

### Smell A — composite/heuristic judgment fields baked into domain records

`grep` for `FindingSeverity`-typed fields and `*Score`/`*Classification`/`*Level` fields across every
domain model found 6 analyzers computing genuine judgment (not raw facts) inside the Analyzer itself:

| Analyzer | Field(s) | Computed in | Nature |
|---|---|---|---|
| Memory | `MemoryPressureScore` | `MemoryAnalysisProjection.cs:174-185` | Weighted composite: `lohPressure*0.35 + concentrationPressure*0.30 + smallObjectPressure*0.20 + densityPressure*0.15`, each sub-score normalized against a hand-picked constant (`/35.0`, `/70.0`, `/85.0`, `/45.0`, `/12_000.0`) |
| Allocation Pattern | `GCPressureLevel` (enum: Low/Moderate/High/Critical), `PromotionPressureScore` | `AllocationPatternDomainResult.cs:9,40-41`; classified via `AllocationPatternAnalyzer.ClassifyPressure(double)` | Same shape as Memory's pressure score — a classified band derived from a composite score |
| Event Leak | `SeverityScore` (on both `EventLeakGroupSnapshot` and `EventLeakInstanceSnapshot`) | `EventLeakDomainResult.cs:41,68` | Notably, the domain result already carries a `ScoringVersion` field (`EventLeakDomainResult.cs`) with the comment *"Bumped whenever the severity-scoring formula changes... trend comparisons across a version boundary are not meaningful"* — the team has already recognized this as a versioned scoring **algorithm**, which is exactly the kind of logic that belongs in a single, named, testable stage rather than embedded in the analyzer |
| GC Root | `SeverityScore` on `RootFinding` | `GCRootAnalysisProjection.cs:89` (`ComputeSeverity`) | Per-row severity baked before any finding generator runs |
| Hang | `HealthScore` | `HangAnalyzer.cs:370` (`ComputeHealthScore`) | Composite score; note `HangFindingGenerator`'s severity ladder is keyed on `WaitingPercent`/`QueuedWorkItems`, not `HealthScore` — while `HangSectionBuilder`'s (now-being-removed, see P0 above) `LeadFinding` was keyed on `HealthScore < 50`. So this one field alone was already implicated in the P0 Hang divergence bug — the composite score existing in stage 1 at all is *why* the builder had an independent, disagreeing signal to reach for. |
| Leak Candidate | `SuspicionScore` (int), `Severity` (`Core.Enums.FindingSeverity` — literally `InsightFinding`'s own output type), `Classification` (`LeakClass`) | Somewhere in the Leak Candidate analyzer/scoring path (not yet traced) | Most extreme case: the domain row already carries a `FindingSeverity` value. `LeakCandidateFindingGenerator` and `LeakAnalysisSectionBuilder` both just relay `candidate.Severity` — stage 2 is a pass-through for this analyzer today, not an interpreter |

**A likely-related fifth scoring location, not yet reconciled against the above:**
`ExplainableScoringEngine.ComputeScores` (`DumpDetective.Reporting/Services/ExplainableScoringEngine.cs:36`)
computes its own `ScoreBreakdown` for Leak / GcPressure / ThreadContention in the **Reporting** layer —
a different layer again from both the stage-1 baked scores above and the stage-2 `IFindingGenerator`s.
Whether it reuses `LeakCandidateRecord.SuspicionScore`/`HangAnalyzer.ComputeHealthScore` or recomputes
independently from raw findings is unconfirmed and should be checked before any consolidation — if
independent, it's a sixth drift point on top of the ones already catalogued.

### Smell B — Stage 1 pre-curates ranked/filtered views instead of exposing one raw table

Two variants of the same underlying complaint: the Analyzer decides *what's worth showing* (a
ranking/selection decision) rather than exposing complete per-entity raw data and letting a later
stage decide.

**Acute variant (Memory only, confirmed):** `MemoryDomainResult.TopTypes` is not "top-N by size" —
`MemoryAnalysisProjection.cs` builds three separately-sorted lists (`bySize`, `byCompositePressure`,
`bySize` again) and merges them via per-criterion quotas driven by four configurable weights
(`TopTypesBySizeWeight`/`ByCountWeight`/`ByLohWeight`/`ByAverageSizeWeight`), where `byCompositePressure`
itself reuses the same normalized/weighted scoring math as `MemoryPressureScore`. The **row selection
itself** — which types even appear in the report — is a multi-criteria judgment call baked into stage
1, not just the displayed score. This is already independently documented in
[analysis-profile-removal-plan.md §9.27](./analysis-profile-removal-plan.md) as *"the tier changes
which types are selected, not how many"* and categorized there as `5 — ranking function (keep)`.

**Milder, widespread variant (many analyzers):** several domain results carry multiple independently
capped `Top*` lists that all slice the *same* underlying per-entity population by a different single
raw dimension — e.g. `AsyncTaskDomainResult` has `TopPendingTaskTypes`, `TopFaultedTaskTypes`,
`TopContinuationTypes`, `TopOrphanedTasks`, `TopDeepestChains`, `TopContinuationFanoutTypes`,
`TopUnresolvedTaskCompletionSources`, `TopPendingValueTaskSources` — eight separately-built,
separately-capped lists, several of which are almost certainly views over the same task population
sliced by state. `AllocationPatternDomainResult` similarly has four `Top*TypeProfile` lists
(`TopTransientTypes`/`TopShortishTypes`/`TopLongLivedTypes`/`TopHighGen1SurvivorTypes`) over what is
structurally one per-type allocation-profile table. This variant isn't a weighted-merge judgment call
like Memory's (each list uses one clear raw criterion, which is fine on its own), but it is still N
independently-maintained, independently-capped derived views of one underlying entity, where a single
raw per-entity table plus downstream sorting would remove the duplication of both scan logic and cap
bookkeeping. `FinalizableObjectDomainResult` (checked earlier) is the good counter-example: its two
`Top*` lists are single-criterion, uncapped-judgment views, but even there, two lists over
`FinalizerQueueEntry`-shaped data is arguably one list with two sort orders applied downstream, not two
analyzer-side artifacts.

**Why this matters now specifically:** `analysis-profile-removal-plan.md` Category 1 already commits
to "analyzer emits complete ranked aggregate; renderer slices" for every `Top-N` cap — that plan is the
natural place this gets fixed, since removing the caps is already forcing a per-analyzer touch of every
`Top*` list. The refinement this doc adds: when doing that Category-1 migration, don't just relocate
each list's N-limit to the render layer independently — check whether the several lists on that
analyzer's domain result are views over the same entity, and if so, **collapse them into one complete
raw table** (all entities, all the raw columns needed to sort by any dimension) instead of relocating N
separately-capped lists. That removes N-1 redundant scan/dedup/cap code paths per analyzer, not just
the cap itself. This is an addendum to the profile-removal plan's execution, not a competing plan —
defer sequencing to that document's §11 pre-implementation checklist (B1-B4, D1-D9), since Category 1's
render-layer mechanism (D5, using Crash as the reference implementation per that doc) needs to exist
before "collapse to one table" can be verified end-to-end.

## Recommended stage boundary (going forward, supersedes the earlier 3-stage draft below)

Revised after the Stage 1 purity audit above — the boundary needs to move one stage earlier than the
original draft assumed, since the composite-score/pre-ranked-list smell shows stage 1 already leaking
into stage 2's job, not just stage 2 leaking into stage 3's.

- **Analyzer** — `IAnalyzer.AnalyzeAsync` → `AnalyzerDomainResult`. Pure raw facts only: counts, byte
  sums, per-entity raw stat tables (one table per entity type, not N pre-ranked/pre-filtered views of
  it). No composite scores, no severity/classification enums, no weighted quotas. A useful litmus
  test: if a field requires a hand-picked constant or weight to compute (`/35.0`, `*0.30`, a threshold
  cutoff), it doesn't belong here.
- **Insight** (merges the current `IFindingGenerator` + the judgment portion of `IAnalyzerSectionBuilder`
  into one stage per analyzer) — `AnalyzerDomainResult → (InsightFinding[], structured tables/rankings)`.
  Sole owner of all judgment: composite scores, severity, banding, which rows/entities are worth
  surfacing and in what order. Keep the internal implementation decomposed into small, independently
  unit-testable pure functions (e.g. a `ComputeSeverity(...)` function and a `RankTopTypes(...)`
  function as separate, directly-testable units within the same class/module) — the point of merging
  the *stage* is to guarantee one call site can't produce two disagreeing answers about the same fact,
  not to produce an untestable monolith.
- **Report assembly** — ordering/grouping across all analyzers (`ReportDomainProjector`,
  `ReportSectionAssembler`). Needs a cross-analyzer view (domain ranking, cross-domain insights) so it
  stays a separate step; its own judgment calls (`InferFindingDomain`, `ShouldSuppressInfoInsight`) are
  a secondary cleanup candidate, out of scope for this pass.
- **Render** — confirmed already close to pure today (`HtmlReportRenderer.Render`,
  `HtmlReportRenderer.cs:33-80`, just serializes `AnalysisReportDocument` to JSON for a client-side
  template). No stage-boundary change needed here.

This reverses the "rejected alternative" from the first draft of this doc (keeping finding-generation
and section-building as two separate, independently-testable stages). That draft under-weighted how
much judgment was already duplicated at the *content* level (not just the `SectionLeadFinding` slot) —
top-N selection and chart/table shape are themselves analytical decisions, not layout. Testability is
preserved by decomposing *within* the merged stage rather than *between* stages.

## Open questions

- Should `BuildConfidenceBand` narrative blocks (distinct from `SectionLeadFinding`) also be pulled from `InsightFinding.EffectiveConfidenceScore` where a finding exists, rather than an ad-hoc `score` parameter?
- Do we widen `NormalizeSectionContractSlots`'s single "top severity" pick to expose the full result of the pick (used to have visibility if two findings tie in severity), or is top-1 sufficient long-term?
- Full audit of `SectionLeadFinding`-constructing builders is complete (8/8, table above). Not yet audited: the ~31 remaining section builders for *other* forms of independent judgment beyond `LeadFinding` (e.g. inline severity coloring on table cells, per-row classification) — the two lower-priority items noted above (Memory `pressureBand`, Leak `GetImpactBand`) were found opportunistically while auditing the 8, not via an exhaustive pass over all 39.
- Where is `LeakCandidateRecord.SuspicionScore`/`Severity`/`Classification` actually computed? Not yet traced to a source file — needed before Smell A's Leak Candidate row can be acted on.
- Does `ExplainableScoringEngine.ComputeScores` (`DumpDetective.Reporting/Services/ExplainableScoringEngine.cs:36`) reuse the stage-1 baked scores (`LeakCandidateRecord.SuspicionScore`, `HangAnalyzer.ComputeHealthScore`) or recompute independently? If independent, it's a further drift point beyond the 6 already catalogued in Smell A.
- Smell A/B audit covered every file in `DumpDetective.Analysis/Models/` at the field-shape level (grep for `FindingSeverity`/`*Score`/`*Classification`/`*Level` fields, and Top* list counts) but only deep-read a sample (Memory, Allocation Pattern, Event Leak, GC Root, Hang, Leak Candidate, Async Task, Infrastructure group, Finalizable Object). The remaining ~20 files with 1-2 `Top*` lists each are lower risk (single list is much harder to have a multi-criteria-merge problem) but haven't been individually confirmed clean.
- How does "collapse N capped lists into one raw table" (Smell B, milder variant) interact with the project's bounded-memory rule (CLAUDE.md)? A single complete per-entity table with no cap could be large for high-cardinality entities (e.g. all task types, all thread waits) — needs the same exactness-vs-memory reasoning `analysis-profile-removal-plan.md` already applies to scan caps, not a free pass just because it's "one table instead of many."
