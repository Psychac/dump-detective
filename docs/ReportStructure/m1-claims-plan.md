# M1 — Claims: Implementation Plan

Implements [ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path) step **M1**,
with [phase-8-sinks-and-ui.md](../refactor/modularity/phase-8-sinks-and-ui.md) migration steps 1–2
folded in as its first step. The claim model itself is specified in
[ReportSystemVision.md § 6](ReportSystemVision.md#6-the-claim-graph); this doc covers only sequencing,
what exists today, and the decisions to make before starting.

**Status:** planned 2026-10-01. Nothing started.

## Why M1 is next

Picked over Phase 7 (cross-source correlation) and Phase 9 (isolation) on 2026-10-01:

- Phase 7's required report surface (source attribution, rendered `ConfidenceBreakdown`, navigable
  lineage, conflict findings) *is* M1 + M3. Building correlation first would mean squeezing its
  output into today's `InsightFinding` shape and redoing it.
- M1 pays off on every dump run; Phase 7 only on dump+trace sessions.
- Phase 5's observation lineage is computed but dropped before the report — see *Starting point*.
- Phase 9 has no driver (all analyzers first-party).

The § 19 rule that an M1 adapter "may not grow new judgment" was written for an adapter over
pre-Phase-5 domain results. Phase 5 shipped, so claims are authored **by the synthesis rules
themselves** — no adapter layer, and the rule doesn't need restating here.

## Starting point (checked against code 2026-10-01)

| Fact | Where |
|---|---|
| 76 `ISynthesisRule`s: ~39 per-analyzer, ~37 cross-analyzer | `Reporting/SynthesisRules/`, `…/Insight/` |
| `Finding` already carries `DerivedFrom` and a full `ConfidenceBreakdown` | `Sdk/Synthesis/Finding.cs` |
| Lineage and the breakdown are **flattened away** into `InsightFinding` (one score + limiting factors) | `Reporting/Services/FindingSynthesisAdapter.cs` |
| `InsightFinding` has 123 references across 36 files, including trend (`FindingLifecycleComparer`) and actions (`ActionPriorityService`) | `Core/Models/InsightFinding.cs` |
| Observations live on `SingleDumpPipelineState` and never reach `AnalysisReportDocument` | `Cli/Pipeline/SingleDumpPipelineState.cs` |
| Formatters are `Render(doc) → string`; no `IReportSink` | `Reporting/Formatters/IReportFormatter.cs` |
| Dump-side `report.json` is written only when `--output` is passed | `Cli/Output/ReportOutputWriter.cs` |
| No `dd-claim` widget; findings render through `report.renderers.findings.js` | `Reporting/Templates/` |
| Phase 5 deliberately did **not** pre-add `Claim` fields to `Finding`, deferring "until the report phase starts" | [phase-5 § step 6 judgment call](../refactor/modularity/phase-5-observations-synthesis.md) |

The confidence split ([vision § 6.4](ReportSystemVision.md#64-confidence-is-two-numbers-not-one))
needs no new judgment. `ConfidenceCalculator` already keeps the two apart:

- **Measurement** — corroborated measurement × weakest degraded measurement, capped by identity and
  temporal fidelity. All lineage-derived.
- **Inference** — the rule's own `Composite` × `ConflictPenalty`. All rule-stated.

## Decisions before step 1 — all decided 2026-10-05

| # | Decision | Recommendation |
|---|---|---|
| D1 | Evolve `Finding` into `Claim` in place, or add `Claim` beside it | **In place** *(decided 2026-10-05)*. Two judgment records is the duplication Phase 5 just removed. Sub-decisions: [D1 detail](#d1-detail) |
| D2 | Observations in `report.json` inline or as a side-car ([vision § 21 Q5](ReportSystemVision.md#21-risks-assumptions-and-open-questions)) | **Side-car** *(decided 2026-10-05)*. Measured on the 3.35 GB reference dump: ~97k observations = 104.8 MB compact JSON (1,128 B each), 3.7 MB gzipped. Gzipped by default, with an option to write uncompressed. `report.json` references it by path and hash |
| D3 | Markdown/text share one presentation layer over claims ([§ 21 Q6](ReportSystemVision.md#21-risks-assumptions-and-open-questions)) | **Shared** *(decided 2026-10-05)*. Prose generated from a structured assertion is shared by construction: the layer yields structured parts (headline, support, falsification lines) and each formatter adds only its own markup. Scope is Markdown and text only; HTML renders through `dd-claim` (step 5) |
| D4 | Rules stay C# with versioned ids ([§ 21 Q3](ReportSystemVision.md#21-risks-assumptions-and-open-questions)) | **Yes** *(decided 2026-10-05)*. Rules stay C#. Correction: `ISynthesisRule` has `RuleId` but no version today, so rule versioning is new work, folded into step 3 (`derivedFrom: RuleRef[]` needs it) |
| D5 | Which golden reports may change | **Accepted** *(decided 2026-10-05)*. Steps 1–2 golden-equal; steps 3+ change output by design, re-bless per batch |

### D1 detail

- **Name** — keep `Finding` while fields are added; rename `Finding` → `Claim` in one mechanical
  commit at the end of step 3, once the shape is stable.
- **`Narrative` / `Recommendation`** — stay author-written through step 3. `Assertion` is added
  beside them and rules populate both, with a test that they agree. Step 4 makes them derived
  (shared presentation layer, D3).
- **`Support` / `Counter` / `FalsifiedBy`** — optional with empty defaults at first, so existing
  rules compile unchanged. Become `required` once the last rule has migrated, so the compiler
  enforces "no rule without a falsifier".
- **Phase 5 fields** (`Analyzer`, `Category`, `Tags`, `MetricValue`, `MetricUnit`) — kept; the
  pipeline reads them. Reconcile the vision § 6.2 sketch to include them.
- **Out of M1** — `coverage`, `nextCapture`, verdict (M3); `relations` / `actions` only re-homed.

Not needed for M1: the JS bundler question (§ 21 Q4) — `dd-claim` fits the current template
system. It gates M4.

## Steps

1. **Sinks + unconditional `report.json`** *(done 2026-10-05)* — Phase 8 steps 1–2.
   - `IReportSink` in `Cli/Output`; `FileReportSink` renders through the existing formatters unchanged.
   - `ReportJsonSink` writes `<output>.report.json` beside the primary output file (which defaults to
     beside the dump) on every run, single-dump and trend alike, regardless of `--format`.
   - `--separate-json` retired (it extracted JSON from the rendered HTML by regex; `report.json` now
     comes from the document). The `_external` loader in `report.main.js` is now unreachable.
   - Gate met: full suite green (1452 passed), per-format output asserted equal to the formatter's
     own render, real run on the 3.35 GB reference dump wrote `out.html` 2.6 MB + `report.json` 66.3 MB.
   - Open: `report.json` is 66.3 MB uncompressed; the HTML embeds the same JSON gzip-base64.
2. **Lineage into the report** *(done 2026-10-05)* — closes the Phase 5 open item.
   - `FindingRecord` gains `derivedFrom` (observation ids) and `confidenceBreakdown`; `confidence` stays
     the single headline number. Carried by a fingerprint-keyed side map (`FindingLineage`) from
     `SynthesisFindingEnricher`, so `InsightFinding` is untouched. Findings sharing a fingerprint merge:
     union of observations, lower-confidence breakdown.
   - Observations go to a side-car per D2: `<output>.observations.json.gz` (`--observations-uncompressed`
     for plain `.observations.json`). `report.json` gains `observations: { file, sha256, count, compressed }`.
   - Companion files are named after the primary output (`Crash.html` → `Crash.report.json`,
     `Crash.observations.json.gz`) so reports sharing a folder don't collide. Step 1's fixed `report.json` is gone.
   - The HTML's embedded JSON omits lineage and the side-car reference (`HtmlEmbeddedReportJson`): left in,
     they grew the real report 2.6 → 6.3 MB for fields the HTML never reads.
   - Verified on the 3.35 GB reference dump: HTML 2.7 MB (unchanged), `report.json` 76.5 MB, side-car 6.0 MB
     gzipped for 97,408 observations; hash and count match, 82 findings carry lineage, 0 unresolved ids.
   - Open: the D2 estimate was 3.7 MB gzipped; the real side-car is 6.0 MB. Lineage is large for population
     findings (one finding cites 68,576 observations; 145,781 ids total), which is why `report.json` grew ~10 MB.
   - Open: trend runs write the side-car (all dumps' observations) but trend findings come from deltas, so
     they carry no lineage yet.
3. **`Claim` record** *(in progress — types, engine stamping and rename done 2026-10-05; golden test landed in step 4's pilot)* — per D1, in `Sdk/Synthesis`.
   - Done: `Assertion`, `AssertionPredicate` (15), `Reference`, `EvidenceStatement`, `Counter`, `FalsificationTest`,
     `RuleRef`, added to `Finding` as optional; `ISynthesisRule.Version` on the interface (the 76 rules already
     declared `Version => 1`); `SynthesisEngine` stamps `Finding.Rule` and `report.json` carries it per finding.
   - Done: `Sdk.Synthesis.Finding` renamed `Claim` (identifiers only; user-visible strings like "Critical Finding
     Narratives" and `InsightFinding` untouched, and other docs still say `Finding` for the Sdk type).
   - The per-rule golden test landed with step 4's pilot (see above).
   - Fields from [vision § 6.2](ReportSystemVision.md#62-the-claim-record) that M1 owns:
     `assertion` (closed predicate vocabulary, § 6.8), `subjects`, `support`, `counter`,
     `falsifiedBy`, split `confidence`, `derivedFrom` with rule id + version.
   - Deferred, with the milestone that owns each: `coverage` (M3), `nextCapture` (§ 6.5, after M3),
     `relations` and `actions` re-homing (after M1), verdict (§ 6.6, after M3).
   - **Predicate vocabulary** *(decided 2026-10-05)* — 15 values: the vision's 12 (`Retains`, `Grows`,
     `Blocks`, `Leaks`, `Exceeds`, `Fragments`, `Faults`, `Stalls`, `Duplicates`, `Conflicts`, `Missing`,
     `Normal`) plus `Composes` (descriptive snapshots, Info), `Dominates` (concentration; share is the
     measure) and `Exhibits` (a pattern is present, no threshold). Chosen by reading the existing rule
     titles; not a rule-by-rule audit.
     - `Grows` needs a reference snapshot, so it is trend-only in M1.
     - Cross-analyzer correlation rules keep their own predicate and cite related claims in `support`
       until `relations` lands; there is no `Correlates` predicate.
     - Tool-limitation findings ("reference tracking was capped", "traversal limit reached", "low sampling
       coverage") stay claims in M1 with `Missing` as a stand-in, then move to coverage in M3.
   - **`reference`** *(decided 2026-10-05)* — nullable; M1 implements only the budget case (a rule's own
     threshold becomes the reference, so `Exceeds` states what it exceeded). Prior snapshot, rate and
     distribution arrive with § 8.
   - **`support` / `counter` / `falsifiedBy` shapes** *(decided 2026-10-05)*
     - `support` and `counter`: lists of `{ observation ids, why }`, `why` a short rule-authored phrase.
     - `falsifiedBy`: list of `{ kind, test, command? }`, `kind` one of `capture`, `windbg`, `code-inspection`.
     - Guard 1: a test flags near-identical `falsifiedBy` text across claims.
     - Guard 2: an empty `counter` must be stated explicitly (`Counter.None("reason")`), so "none exists"
       is distinguishable from "never looked". `falsifiedBy` may not be empty.
     - Revisit if rules start padding; the plan's execution risk is boilerplate, not missing fields.
   - **Rule versioning** *(decided 2026-10-05; closes the D4 open item)* — hand-bumped `int Version` on
     `ISynthesisRule`, recorded in `derivedFrom` as rule id + version. A per-rule golden-output test fails
     when a rule's emitted findings change shape without a version bump. Derived (hash) versions rejected:
     they change on any rebuild, so identical rules from different builds would look different.
4. **Rule migration** *(done 2026-10-06 — all 74 rules; pilot 2026-10-05, batches 1-15 2026-10-06)* — all 76 rules author `assertion`, `counter`, `falsifiedBy`.
   - Done: `LohFragmentationSynthesisRule` (pilot), with the per-rule golden mechanism
     (`Golden/RuleShapeGolden.cs`, goldens in `Golden/RuleShapes/`; `DD_BLESS_RULE_SHAPES=1` to bless). Fails when
     a rule's claim shape changes without a `Version` bump, or the version changes without a re-bless.
   - Batch 1 (2026-10-06): `SqlCommand`, `SqlTransaction`, `SqlConnectionPool`, `LockGraph`, `StaticRoot`. Tests in
     `InfrastructureAndRuntimeRuleClaimTests`. Verified on the reference dump: 5 of 82 findings carry an assertion
     (`SqlConnectionPool` did not fire on this dump; covered by unit tests only).
   - Batch 2 (2026-10-06): `ThreadStackCluster` (2 claims), `Hang`, `Collection`, `TimerLeak` (2 claims). Tests in
     `ThreadingAndCollectionRuleClaimTests`. Reviewing the goldens caught a rule giving a queue-depth claim a counter
     and falsification test about idle waiting threads; fixed before commit. Verified on the reference dump: 11 of 82
     findings carry an assertion, and `timer-leak` (confidence 0.82) is the first real claim to show the
     confidence reason line. `AllocationPattern` was held back to a later batch (largest of the small rules).
   - Batch 3 (2026-10-06): `AllocationPattern`, `Thread` (5 claims), `String` (6 claims). Tests in
     `AllocationThreadAndStringRuleClaimTests`. Golden review again caught a wrong-direction counter and
     falsification test (an active-exceptions claim argued about stalled threads); fixed before commit. Verified
     on the reference dump: 16 of 82 findings carry an assertion. `ObjectShape` held back (three claims, more
     branching).
   - Batch 4 (2026-10-06): `ObjectShape` (3 claims), `Memory` (6), `EventLeak` (2 shapes), `GCRoot` (4),
     `ReferenceChain` (5). Tests in `MemoryRetentionAndRootRuleClaimTests`. Verified on the reference dump: 23 of 82
     findings carry an assertion; the two event-leak claims (confidence 0.4) are the first real claims where the
     confidence reason line should read as limited by data. Tool-limitation claims (`GCRoot` walk cap,
     `ReferenceChain` traversal limit, no samples) use `Missing`, per the step 3 decision.
   - Batch 5 (2026-10-06): `Crash` (3 claims), `SegmentReservation` (5), `DbConnection` (4), `Array` (4 kinds, sparse
     emitted per array), `GCGeneration` (4). Tests in `StabilityAndHeapLayoutRuleClaimTests`. Where a rule fires on
     either of two thresholds (multi-dimensional and pinned arrays, address-space pressure), the claim's measure and
     reference follow whichever threshold actually fired, so they always compare like with like. Verified on the
     reference dump: 31 of 82 findings carry an assertion. Also re-indented one unindented block from batch 4.
   - Batch 6 (2026-10-06): `GCHandle` (6 claims), `WcfChannel` (3), `AsyncStateMachine` (4 kinds), `WeakReference`
     (5), `Dominator` (4). Tests in `HandlesAsyncAndRetentionRuleClaimTests`. The per-claim gate caught a real
     mistake of mine: the Dominator hotspot claim's counter cited the population observation, which was not in
     the claim's lineage. The claim's confidence does read the population, so it was added to `DerivedFrom`
     (a lineage change for that one claim, in `report.json` only). Verified on the reference dump: 40 of 82
     findings carry an assertion. Known oddity kept as the rule defines it: the `WeakReference` overview claim
     takes the highest severity of its signals but asserts `Composes`.
   - Batch 7 (2026-10-06): `Jit` (7 claims), `LeakCandidate` (critical + aggregate), `Boxing` (7), `AsyncTask` (one
     claim per signal plus the cluster; structure chosen by signal key), `HttpObject` (6). Tests in
     `RuntimeBoxingAsyncAndHttpRuleClaimTests`. Review caught unresolved-promise claims asserting `Leaks` at Info
     severity; they now say `Exhibits` until they reach Warning. Verified on the reference dump: 50 of 82
     findings carry an assertion. Remaining per-analyzer rules: `HeapTopology`, `Module`, `FinalizableObject`;
     the ~37 cross-analyzer rules in `Insight/` follow.
   - Batch 8 (2026-10-06): `HeapTopology` (9 claims), `Module` (8), `FinalizableObject` (9) — the three largest
     per-analyzer rules — plus the first two cross-analyzer rules, `PohGrowthInsightRule` and
     `WastefulCollectionsInsightRule`. Tests in `TopologyModuleFinalizableAndInsightRuleClaimTests`. All 36
     per-analyzer rules are now migrated. Verified on the reference dump: 64 of 82 findings carry an assertion.
     The 76 count is confirmed: 76 `ISynthesisRule` implementers, 38 of them in `Insight/`; 36 remain after this
     batch, all in `Insight/`.
   - Batch 9 (2026-10-06): cross-analyzer rules `HttpClientAccumulation`, `DynamicAssemblyAccumulation`,
     `LohFragmentation`, `StringDuplicationRatio`, `JitHeapBloat` (the first claim citing two analyzers'
     observations, in two separate support statements). Tests in `InsightRulesBatchOneClaimTests`. Only one rule of
     the five fires on the reference dump (`wasteful-collections` from batch 8), so the real-dump check is thin
     here; the goldens carry the coverage.
   - Batch 10 (2026-10-06): `AnalyzerFailures`, `LohPressure`, `PinnedHandlePressure`,
     `StaleWeakReferenceAccumulation`, `WcfChannelFault`. Tests in `InsightRulesBatchTwoClaimTests`. Gate change:
     a claim with an empty `DerivedFrom` (`AnalyzerFailures`, about the tool's own run) may have empty support; every
     other gate still applies. `RuleShapeGolden` scenarios may now carry a `SynthesisContext`.
   - Batch 11 (2026-10-06): `FinalizerQueueBacklog`, `LohArrayPressure`, `PinnedStringLeak`, `ActiveCrash`,
     `DbConnectionLeak`. Tests in `InsightRulesBatchThreeClaimTests`. Verified on the reference dump (94 s): 68 of 82
     findings carry an assertion.
   - Batch 12 (2026-10-06): `RecurringTimeoutPattern`, `WcfOpeningTimeoutCorrelation`, `EventLeakPattern`,
     `FatalExceptionOnHeap`, `LeakSuspicion` (2 claims). Tests in `InsightRulesBatchFourClaimTests`. Verified on the
     reference dump (111 s): 73 of 82 findings carry an assertion.
   - Batch 13 (2026-10-06): `OrphanedTaskAccumulation` (2 claims), `BoxingGCCorrelation`, `JitModuleHotspot`,
     `LongHeldTransactionOnOpenConnection`, `MemoryTypeGeneration`. Tests in `InsightRulesBatchFiveClaimTests`.
     Verified on the reference dump (79 s): 76 of 82 findings carry an assertion.
   - Batch 14 (2026-10-06): `StaticRootFinalizableCorrelation`, `AsyncStateMachineFireAndForget`,
     `DataTableLifecycle`, `GCRootLargeRetention`, `ReferenceChainDominatorCorrelation`. Tests in
     `InsightRulesBatchSixClaimTests`. Verified on the reference dump (78 s): 78 of 82 findings carry an assertion.
   - Batch 15 (2026-10-06): the last six cross-analyzer rules — `AddressSpacePressure` (2 claims),
     `AllocationPressureCrossCorrelation` (2), `ClusterHangCorrelation`, `CrashModuleHotspot`,
     `KnownLeakPatterns` (2), `ThreadContention` (3). Tests in `InsightRulesBatchSevenClaimTests`.
   - **Rule count corrected: 74, not 76.** The plan's "76" came from an approximate split (~39 + ~37). The catalog
     (`DefaultSynthesisRuleCatalog`) lists 74 rule classes: 36 per-analyzer, 38 in `Insight/`. Earlier entries in
     this section that say "of 76" use the plan's number.
   - **Verified on the reference dump (78 s): 82 of 82 findings carry an assertion.**
   - **Completeness guard:** `EveryRuleAuthorsAClaimTests` fails if a catalog rule has no golden or its golden is at a
     stale version, if rule ids collide, or if a rule class exists that the catalog doesn't list.
   - **Still not built (step 4 follow-ups):** the cross-claim check for near-identical `falsifiedBy` text; and the
     counter quality problem — most counters are `Counter.None` with a reason, so "none exists" is the common case
     and the plan's padding risk applies. Neither blocks step 5.
   - **Open decision: inference confidence is unpopulated.** On the reference dump 72 of 82 claims sit at 1.0;
     10 are lower, all from measurement limits (missing root path, estimates, heuristics, degraded analyzers).
     73 rules still use the default `MeasurementCertain`. Counters that argue a claim may be transient or
     heuristic do not lower confidence, so a claim can concede its own counter-evidence and still read as certain.
     Vision § 6.4 wants measurement and inference confidence separate; only measurement is populated today.
   - **Open: intermittent test-host early stop.** One full-suite run reported 362 tests instead of ~1,700 and the
     next completed normally; the flaky `ArrayAnalyzerRetypingCharacterizationTests` failure appears most runs.
   - **Open: intermittent hang, not caused by this migration.** One real-dump run sat 42 minutes in Stage 3 inside
     `EventLeakAnalyzer`'s "building publisher registry" setup and was killed; the same dump finished in ~74 s on
     the next run with my changes, and on the committed state before them. Needs its own investigation.
   - `RuleShapeGolden` now enforces the plan's per-claim gates on every migrated rule: an assertion, at least one
     falsification test, an explicit counter (evidence or `None` with a reason), non-empty support, and every cited
     observation inside the claim's own `DerivedFrom`. Long evidence lists collapse to ranges in goldens.
   - Still to build: the cross-claim guard that flags near-identical `falsifiedBy` text; it needs more rules
     migrated before it can say anything useful.
   - Plumbing: the structured fields now reach `report.json` per finding (`assertion`, `support`, `counter`,
     `falsifiedBy`) and are omitted from the HTML's embedded JSON. Step 2 had only carried lineage, so the first
     real-dump check of the pilot found they were not being written; fixed.
   - Lesson from the pilot: a `counter` points away from what the claim says. The first draft gave a `Normal`
     claim a counter that argued against "fragmented"; caught on the real dump, fixed. Check direction per predicate.
   - Verified on the reference dump: 82 findings, 1 with `assertion`; `report.json` 76.5 MB, HTML 2.7 MB (unchanged).
   - Batched by domain package, per-analyzer rule and its cross-analyzer rules together.
   - The single biggest execution risk ([§ 21 assumption 3](ReportSystemVision.md#21-risks-assumptions-and-open-questions)):
     a rule that can't state what would falsify it is reported back, not padded.
   - Gates: [§ 17.1](ReportSystemVision.md#171-editorial-gates) rows for assertion, `falsifiedBy`,
     `counter`, registry; plus a test flagging near-verbatim `falsifiedBy` text across claims.
   - Each batch verified on the reference dump, one run at a time.
5. **Generated prose + `dd-claim`** *(card built 2026-10-06 for the one migrated claim; Markdown/text claim listing and remaining rules pending)* — per D3.
   - One prose generator over `assertion`; HTML, Markdown, text all call it.
   - `dd-claim` widget design *(decided 2026-10-05; not yet built)*:
     - **Assertion line:** a full generated sentence on its own line under the title, wrapping allowed, not
       a chip. The authored title, narrative and recommendation stay; replacing titles waits until generated
       wording proves as good on migrated rules.
     - **Confidence:** one headline chip (band + number, as today). Only when below the top band, one plain
       reason line naming the binding dimension and the remedy: data-limited ("get better data") or
       inference-limited ("use judgment"). Dimension is picked from the breakdown numbers (measurement:
       identity, temporal, capability fidelity; inference: evidence strength × conflict penalty), so the
       flat `limitingFactors` list needn't be split. Exact field mapping to be confirmed when building.
       The full breakdown sits in the expanded "Why".
     - **"Why" block:** collapsed by default, showing counts (support / counter / tests); expanded it lists
       supports, "weakened by", and "wrong if" tests with copyable commands.
     - **HTML carries a slim projection with no observation ids.** Each support/counter entry links to the
       analyzer section that produced its observations (analyzer name per entry, not ids). Per-observation
       drill stays in `<output>.report.json` and the side-car; true per-observation navigation needs
       observations in the page and belongs to M4.
     - **Unmigrated claims** (no assertion) render exactly as today, so steps 4 and 5 can interleave.
     - **Markdown/text:** the same parts as plain lines from the shared generator (D3), no collapse.
     - **Deferred:** conflict state (§ 6.3; needs `relations`, after M1), next-capture row (M3), actions row.
     - **Built and reviewed on the real dump (2026-10-06):** assertion line with a muted predicate label;
       "Confidence" label on the existing four-bar chip, bars and number taken from the breakdown (the old chip
       showed a severity default that contradicted the breakdown); breakdown as a hover/focus tooltip, not a
       table; source chips only for analyzers other than the card's own. Number stays hidden, as on every card.
     - **Known redundancy, accepted for now:** on a `Normal` claim the title, assertion line and recommendation
       all say "within acceptable range". It goes away when title and narrative become generated from the
       assertion (not yet scheduled; replacing them waits until generated wording proves as good).
     - **Known gap, accepted for now:** Why-block support lines carry no link on this card (their source is the
       card's own analyzer, and "Investigate" already goes to that section). Per-observation navigation needs
       observations in the page, which belongs to M4.
6. **Retire `InsightFinding` and `FindingSynthesisAdapter`** *(pending)*.
   - Trend and action consumers read `Claim`; fingerprints unchanged so trend identity holds.
   - Done when `InsightFinding` has zero references.

## Adjacent milestones — state today

Recorded so the M2/M4 plans start from facts, not re-survey:

- **M2 (entities)** — observations already carry `EntityRef` subjects (`TypeRef`, `ThreadRef`,
  `ModuleRef`, `MethodRef`); 43 emission sites pass `Subjects = []` (process-wide scalars). The
  HTML string pool and `CompactRowJsonConverter`
  ([ReportFormatCleanSlate P0a](ReportFormatCleanSlate.md#10-phased-migration)) are shipped; M2
  replaces the pool with an entity table.
- **M4 (payload)** — still one gzip+base64 payload; no segmentation or column store.

## Open questions

- Reference-dump path and run command for verification — confirm before step 1.
