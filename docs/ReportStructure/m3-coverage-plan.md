# M3 — Coverage: Implementation Plan

Follows [m2b-relations-plan.md](m2b-relations-plan.md). Tracked as milestone **M3** in
[ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path); the model is
[§ 7](ReportSystemVision.md#7-coverage-and-negative-space). Depends only on M1 (claims), so it does not wait on M4 or M5.

**Status:** planned 2026-10-12, nothing built. Steps 1-2 done, step 4 done; D1-D6 decided 2026-10-12, with D6 points 3-4 open until step 8's reading of the trend comparers.

## Goal

A reader can tell "we looked and found nothing" from "we did not look". Three visible changes:

1. Each claim states what was and was not measured for it, specifically ("depth limit 20 reached on 312 of 14,003 paths"), not a blanket caveat.
2. A cell with no value says why (not measured, or ambiguous), and is distinct from zero.
3. A healthy result is published as a claim ("no deadlock cycles among 214 threads; the lock graph was fully traversed").

## Starting point (read from code 2026-10-12; counts not yet measured on a dump)

| Piece | Where it is today | Gap against § 7 |
|---|---|---|
| Run status | `AnalyzerRunStatusRecord` (name, status, duration, finding/warning counts, scan count, cache hits, error, skip reason), in `ReportAppendix.AnalyzerRunSummary` | Per analyzer, not per capability; no scope, no boundary, no consequence; not linked to any claim |
| Known limitations | `ReportAppendix.KnownLimitations`: hard-coded strings in `ReportSectionAssembler` | Static text, identical for every dump |
| Tool-limitation claims | 8 rules emit `AssertionPredicate.Missing` as the M1 stand-in: Dominator, GCRoot, HeapTopology, Module, ReferenceChain (2), String, AnalyzerFailuresInsight | These are coverage statements filed as claims ([M1 plan](m1-claims-plan.md): "then move to coverage in M3") |
| `Claim.Coverage` | not present; M1 deferred it to M3 | The link from a claim to what it rests on |
| Unknown cells | placeholders as presentation strings ("N/A", em dash) inside table data | Indistinguishable from a value; reason lost. *Measure first:* count per table, and which are "not measured" vs "not applicable" |
| Negative claims | `Normal` predicate exists; *measure first:* how many rules emit it, and for which "looked, found none" cases | |

## Decisions

| # | Decision | Status |
|---|---|---|
| D1 | Granularity of a coverage record | **Decided 2026-10-12: keyed by capability × artifact (§ 7.1), hybrid fill.** Status and cause come from the existing `[RequiresCapability]` / `[OptionalCapability]` resolution in `CapabilityResolver` (`Unsatisfied`, `Degraded` + `FidelityBoost`), which costs no analyzer change. `scope` and `boundary` are optional and filled only by analyzers that already know them, first the 8 that emit `Missing` today; never invented for the rest. Chosen over per-analyzer because it is forward-facing: a trace artifact and capability-level claims need the key, and migrating later would rewrite the records. |
| D2 | What happens to the 8 `Missing` stand-in claims | **Decided 2026-10-12: classify each, move only the ones that describe how far the analysis got.** Read so far (3 of 8): ReferenceChain traversal-limit and no-samples, GCRoot subgraph-walk-capped are coverage (`boundary`); AnalyzerFailures is run metadata, not a claim about the dump. A claim that describes the dump stays a claim. For moved ones: no percentage threshold (a hand-picked constant, § 5.2); status follows the facts (any limit hit is `Partial`) and `boundary` carries the fraction. A `Partial` / `Degraded` record feeds the `limitingFactors` of every claim citing it. No threshold-driven claim is raised in its place. Nothing is deleted until its consumers are checked (step 1). |
| D3 | Unknown cell representation on the wire | **Decided 2026-10-12: the builder states the reason; the string is never parsed.** A cell is a value, `null`, or `null` plus a reason (`not-measured` or `not-applicable`), added without changing the table shape (the payload shape is also M4's concern). Start with the worst columns measured in step 1 (finalizer-queue `Disposed` / `Disposed Field Found`, types `Est. Retained` / `LOH Bytes`, leak-candidate `Root Chain` / `Root Kind` / `Class`), about 217,000 placeholder cells in all. |
| D4 | Which negative claims to publish | **Decided 2026-10-12: only absences the rule actually computed** (it scanned the population and found none), never a generic "nothing wrong" per analyzer. **Every negative claim is gated on coverage:** a `Normal` claim is emitted only when the capability it cites is `Complete`; if `Partial` or `Unavailable` it is suppressed and the coverage record is what the reader sees. Negative claims are ordinary claims with a falsifier, rendered collapsed under severity OK with the true count, no top-N. The list of rules that publish one waits for step 1's count of `Normal` claims. |
| D5 | Scope of the verdict and `nextCapture` | **Decided 2026-10-12: out of M3.** Both consume coverage records, so they follow once those exist. M3 step 5's coverage headline is a display of coverage, not the § 6.6 verdict. Until `nextCapture` exists a `Partial` / `Unavailable` record states the gap and its `consequence`, not the remedy. |
| D6 | How trend treats coverage | **Decided 2026-10-12 for points 1-2** (see [Trend and coverage](#trend-and-coverage)): a claim or measure is compared only when its capability is `Complete` in both snapshots, otherwise "not comparable: coverage differs" naming the capability, never resolved / new / unchanged; a capability changing status between dumps is its own trend item and replaces the lifecycle the moved claims had. Points 3-4 (negative claims in trend, null reasons in the scoped timeline) stay open until the comparers and scoped timeline are read (step 8). |

## Steps (proposed order)

1. **Measure** (no code change). On the 3.35 GB reference dump, foreground, one run: count placeholder cells by table and kind; list every `Missing` and `Normal` claim; list which analyzers can name a scope and boundary today; read the 5 `Missing` rules not yet read (Dominator, HeapTopology, Module, String, ReferenceChain's second) and classify all 8 (D2); find every consumer of their fingerprints (trend, lead findings, actions, executive summary). Output feeds D1-D4.
2. **`CoverageRecord` in the model** *(done 2026-10-12, verified on reference dump, committed 869268d4)*, keyed by capability × artifact (D1).
   - **Decisions taken in step 2:** the type lives in the SDK (`SdkVersion.Minor` 17 → 18); a missing capability gets no artifact (`Artifact` null) because none provides it; the builder is driven by the session's artifacts, never by artifact kind, so a dump, a trace or both are treated alike (no `trace.` prefix rule).
   - **Built:** `CoverageRecord(Capability, Artifact?, Status, Impacts)`, `CoverageStatus`, `CoverageImpact` (analyzer, `NotRun` / `ReducedFidelity`, `FidelityBoost`) in `Sdk/Artifacts`; `CoverageRecordBuilder` in Orchestration from `AnalysisSession` + `CapabilityResolution`; carried through `DumpOrchestrationService` → `SingleDumpPipelineState.Coverage` → `AnalysisReportDocument.Coverage` (single-dump and trend).
   - **Only `Complete` and `Unavailable` are produced.** `Complete` means available, not that every analyzer finished its scan; `Degraded`, `Partial`, `Skipped`, `Failed` come with steps 3-4. Impacts are structured; the wording is step 5's.
   - **Not covered:** the trace-only path (`TraceOrchestrationService`) does not resolve a session, so a trace-only report carries no coverage yet.
   - **Tests:** 5 new in `CoverageRecordBuilderTests` (dump only, missing required, missing optional with fidelity, dump + trace, aggregation); suite 1786 passed, 66 skipped.
   - **Real dump (156 s, foreground):** 32 records, 23 `Complete` (every capability the dump provides) and 9 `Unavailable` (`trace.*`: alloc-samples, contention-events, cpu-samples, custom-events, exception-events, gc-events, http-events, jit-events, thread-timeline), naming 3 analyzers as not run (`ContentionAnalyzer`, `CpuHotspotAnalyzer`, `GcPauseAnalyzer`) and 33 analyzer entries at reduced fidelity. Claims (82), entity table, summary, relations, executive summary and analyzer run statuses identical to the previous run; `report.json` +11 KB (85.22 → 85.23 MB). Results: `m2-step1/after-m3s2/`.
   - **Consequence for step 5:** on a dump-only run 9 of 32 records are "no trace supplied", not a gap in the dump. The report surface must group them apart from real gaps; the data already distinguishes them (`Artifact` null, and no trace artifact in the session).
3. **Claim ↔ coverage link** *(merged into step 4, decided 2026-10-12)*. Building it alone would link nothing on any run: every gap today is a missing `trace.*` capability that no artifact was meant to supply, which must not attach to claims (it would put "reduced fidelity: no trace" on most of the 82 claims and, if it moved confidence, shift nearly all of them with no change in evidence). It gets built with step 4, where the first `Partial` records exist and the link can be checked against real data.
4. **Move the limitation claims, and link claims to coverage** (D2, and former step 3).
   - **Move:** the claims classified as coverage in the step 1 results become `Partial` / `Failed` records with a concrete `boundary`.
   - **Link:** a claim carries references (indices into the document's `coverage` array) to the records that bear on it; the first check is the ReferenceChain traversal-limit case on the reference dump, one claim linking to one `Partial` record.
   - **Rule (decided 2026-10-12, option B):** a record attaches to a claim only when a capability was unavailable and the claim's analyzer used it, never for an optional capability that no artifact in the session was meant to supply. Today nothing in the data tells "no trace supplied" from "trace supplied but lacking this capability" (a trace artifact claims every `trace.*` capability), so the rule needs revisiting when a source can provide only some capabilities.
   - **Confidence:** only `Degraded` / `Partial` records add a `limitingFactors` line; absent trace capabilities never touch confidence. The engine already folds observation fidelity (`Degraded`) into `CapabilityFidelity`, so a new cap must not double-count it.
   - **Done 2026-10-12: ReferenceChain, GCRoot, Dominator, String, AnalyzerFailures** *(D-B decided: a separate `ICoverageRule`, not a new return type on `ISynthesisRule`)*.
     - **Built:** `ICoverageRule` and `CoverageBoundary` in the SDK (`SdkVersion.Minor` 19); `CoverageRecord` gains `Boundary`, `Rule` and `Claims`; `CoverageImpactKind.Limited`; `SynthesisEngine.EvaluateCoverage`; `CoverageMerger` (a rule record replaces the session record for the same capability and artifact); factory and catalog gain `CoverageRuleTypes` (default empty, so existing rules and fakes are untouched); `ReferenceChainCoverageRule` replaces the two `Missing` claims (rule bumped to version 2, golden re-blessed).
     - **Link is record-side:** the record lists the fingerprints of the claims whose analyzer is limited (`Claims`), instead of the claim carrying references, so `Claim` and `FindingRecord` did not change; a claim's coverage is the inverse lookup. The linked claim's `LimitingFactors` gains one line naming the boundary; its scores do not move.
     - **Tests:** 4 on `ReferenceChainCoverageRule`, 1 enricher link test, 3 `CoverageMergerTests`; the traversal-limit claim test now asserts the claim is gone. Suite 1793 passed, 66 skipped.
     - **Real dump (82 s, foreground):** `heap.root-paths` is now `Partial`, 2 of 10 sampled types before the path or depth budget, linked to `reference chain analysis::retention-coverage`, whose limiting factors gained that line. Claims 82 → 81 (only `reference chain analysis::traversal-limit` gone); no other claim changed severity, title or confidence; entities, summary, relations and `topActions` identical; 32 records as before (the `Partial` replaced the `Complete`). Results: `m2-step1/after-m3s4/`.
     - **GCRoot, Dominator, String done (2026-10-12).** Three more `ICoverageRule`s, each replacing its `Missing` claim; their synthesis rules went to version 2 and the goldens were re-blessed.
       - **Boundary reshaped:** `CoverageBoundary(Affected, Total?, Unit, Limit)` replaces `Reached` / `Total`. `Limit` is a verb phrase ("hit the path or depth budget"), `Total` is null when the analyzer does not know the population, and zero means nothing was available to scan. ReferenceChain now reads "8 of 10 sampled types hit the path or depth budget" (the earlier "2 of 10 ... before" was the same fact, stated as what was reached).
       - **Capability per rule:** GCRoot `heap.references` (subgraph walk, total unknown); Dominator `heap.reverse-references` (approximated incoming-reference counts, total unknown); String `heap.strings` (strings longer than the maximum duplicate-string length; total from `total-string.count`); ReferenceChain `heap.root-paths`.
       - **Real dump (110 s, foreground):** GCRoot fires, 823 root-owned subgraphs truncated, `heap.references` is `Partial`; Dominator and String do not fire. `GC Root Analysis` findings 1 → 0, no other analyzer's count moved; claims in `domainInsights` unchanged (81 → 81); entities, summary, relations and `topActions` identical. Results: `m2-step1/after-m3s4b/`.
       - **Step 1 correction:** the table above said the GCRoot `Missing` claim does not fire here. It does (finding count 1); it never appeared in `domainInsights`, which is where step 1 counted, so the count of claims per analyzer must come from the run summary's finding counts, not from `domainInsights`.
       - **No claim links on this dump for GCRoot:** the `heap.references` record lists no claims because the analyzer has no remaining claim to limit (its one claim was the cap itself).
       - **Duplicate for step 5:** `ConfidenceSectionBuilder` and `GCRootSectionBuilder` still state the walk cap from the domain result, so the report says it twice until step 5 points them at the coverage record.
       - **Tests:** 9 new on the three rules; suite 1799 passed, 66 skipped. `GCHandleAnalyzerRetypingCharacterizationTests` failed once in the full run and passed alone twice (the known self-snapshot flakiness, unrelated).
     - **AnalyzerFailures done (2026-10-12), decision A: a `Failed` record keyed by analyzer, no capability.** A failure says nothing about the capabilities the analyzer declared, which other analyzers may still cover, so nothing is marked `Degraded` on its behalf.
       - **Built:** `AnalyzerFailureCoverageRule` (one record per failed analyzer, any count); `CoverageRecord.Capability` is now nullable and `CoverageImpactKind.Failed` was added (`SdkVersion.Minor` 20); `SynthesisContext.AnalyzerRunSummary` (two counts) was replaced by `FailedAnalyzers` (names), since the one rule that read the counts is gone; `CoverageMerger` adds a record with no capability rather than matching it. `AnalyzerFailuresInsightRule` and its golden are deleted: its claim fired only from three failures on a hand-picked threshold.
       - **Tests:** 3 new on the rule and merger, the old rule's 5 tests removed; suite 1796 passed, 66 skipped, plus one failure (`GCGenerationAnalyzerRetypingCharacterizationTests`, a regex timeout under a slower full run) that passed alone: the known flakiness.
       - **Real dump (91 s, foreground):** no analyzer failed, so no `Failed` record appears and the rule is verified by unit tests only. Claims, limiting factors, entities, summary, relations and `topActions` identical to the previous run. Results: `m2-step1/after-m3s4c/`.
       - **Gap:** the enricher returns early when a run has no observations, so a run where every analyzer failed would report no failures. Real dumps always have observations; a synthetic empty run is the only case.
       - **Not done:** `SkippedByFilter` / `SkippedByCancellation` runs produce no `Skipped` record; D2 named only failures.
     - **Step 4 status: done**, apart from the claim-side items listed as gaps above.
     - **Known gap:** in a trend run, rule-produced records of the earlier dumps are not kept (only the session records and the last state reach the document); step 8 owns that.
5. **Report surface.** A coverage headline on the summary and a per-claim coverage line; the appendix status table links to claims it affects. HTML-only first, like the dossier.
6. **Unknown as a value** (D3), starting with the tables measured in step 1 as worst.
7. **Negative claims** (D4), a few rules, each with its own falsifier like any claim.
8. **Trend** (D6): read the comparers and scoped timeline first, then make "not comparable" and coverage-change items real; verify on the two-dump and three-dump trend launch profiles (one at a time, foreground).

## Trend and coverage

Read from code 2026-10-12. The trend path assumes every dump was fully analyzed, so a coverage difference between dumps currently looks like a change in the process.

- **A failed or skipped analyzer vanishes from the trend.** `TrendBuildReportNode.BuildSnapshot` skips every run whose status is not `Success`, so its claims and domain result are absent from that snapshot.
- **Absent reads as resolved.** `FindingLifecycleComparer` compares claim fingerprints between the first and last snapshot (`ResolvedFindings` = in baseline, not in current). A claim missing because its analyzer failed or was degraded in the later dump is reported as resolved.
- **`ObservationTrendDiffer` skips silently.** A measure the baseline had and the current did not is dropped ("not diffable without fabricating a value"), with no record of why.
- **The 8 `Missing` claims have a lifecycle today** (new, persistent, resolved across dumps). Once moved to coverage (D2) that lifecycle is gone, so coverage needs its own comparison or the change is lost.
- **Negative claims (D4)** would appear in New / Resolved. A `Normal` claim disappearing usually means something else appeared, not that nothing happened.
- **Not read yet:** the ~30 `IAnalyzerTrendComparer` implementations (they read `AnalysisSnapshot.DomainResults`, which already excludes non-`Success` runs) and the scoped timeline's handling of null cells.

Proposed D6, matching [§ 3.4](ReportSystemVision.md#34-timeline-alignment-and-its-confidence): *suppressed rather than degraded*.

1. **Not comparable is a result.** A claim or measure is compared only when the capability it rests on is `Complete` in both snapshots. Otherwise it is reported as "not comparable: coverage differs" naming the capability, never as resolved, new or unchanged.
2. **Coverage changes are trend items.** A capability going `Complete` to `Partial` / `Unavailable` between dumps is shown, and replaces the lifecycle the 8 moved claims had.
3. **`Normal` claims** are shown only as a change in state: a negative claim that stopped holding is reported with the claim that replaced it; one that appears is not listed as a new finding. *Open:* exact rule.
4. **Null cells** carry their reason into the scoped timeline, so `not-measured` is not drawn as zero. *Open until the scoped timeline is read.*

## Step 1 results (2026-10-12, read from the existing `report.json` of the 3.35 GB reference dump, written 12:36 the same day at HEAD 3735b282; no new dump run)

- **Run command:** `CrashAnalysis` launch profile (`src/DumpDetective.Cli/Properties/launchSettings.json`); not re-run, because step 1 only reads output.
- **Capability resolution does not reach the report.** `DumpAnalysisService` computes `CapabilityResolution` and prints it only in diagnostic mode (`ConsoleUx.CapabilityReport`); only `Satisfied` is passed on. Step 2 must carry `Unsatisfied` and `Degraded` through to the document.
- **Claims:** 82 total. Predicates: Exceeds 33, Exhibits 16, Retains 9, Normal 9, Composes 5, Leaks 3, Duplicates 2, Missing 2, Faults 2, Stalls 1.
- **The 8 `Missing` rules classified (D2).** Only 2 fire on this dump; the other 6 are classified from code.

| Rule | Fires here | What it says | Class |
|---|---|---|---|
| ReferenceChain traversal-limit | yes (Warning) | N of M sampled types hit the path/depth budget | coverage (`boundary`) |
| ReferenceChain no-samples | no | no valid sample for the configured types | coverage |
| GCRoot subgraph-walk-capped | no | subgraph walks truncated at node/depth budget | coverage (`boundary`) |
| Dominator approximated reference counts | no | addresses past the tracking limit have bounded-error counts | coverage |
| String dedup sampling | no | dedup saw X% of all strings | coverage (`scope`) |
| AnalyzerFailures | no | N of M analyzers failed | coverage (`Failed`); run metadata, reads no observation |
| HeapTopology unknown segment kind | no | segments matched no known kind | **stays a claim (decided 2026-10-12):** the tool's own vocabulary gap, not a limit on traversal |
| Module unknown-identity duplicates | yes (Warning) | 1 module has no identifiable version | **stays a claim:** a fact about the dump, and it is in the entity dossier's `claimIds` |

- **Consumers of the 2 firing `Missing` claims.** Neither is in `topActions`. Only the module claim is in the entity summary's `claimIds`; the traversal-limit claim is not. No source code outside the rules, the analyzers' own measures, `MeasureDirectionRegistry` and tests refers to the fingerprints. Tests that pin them: golden files for gc-root, module, reference-chain and analyzer-failures, plus `MemoryRetentionAndRootRuleClaimTests`, `HandlesAsyncAndRetentionRuleClaimTests`, `TopologyModuleFinalizableAndInsightRuleClaimTests`, `SectionConfidenceFromFindingsTests`, `SdkRegistryConformanceTests`. Not checked: trend comparers (single-dump run).
- **`Normal` claims: 9**, all Info, from Reference Chain, Static Root, LOH fragmentation, Collection, Hang, Lock Graph, Thread Stack Clustering (2) and Module. They are not all computed absences: "Reference-chain retention coverage", "Collection capacity efficiency" and "Module dependency snapshot" are descriptive snapshots that stayed under a threshold. Candidates for D4 (a scan that found none): Static Root ("no candidates detected"), Lock Graph (deadlock cycles: 0), Hang. LOH fragmentation is "within acceptable range", a threshold result, not an absence.
- **Placeholder cells (D3):** 1,621,629 table cells in 138 compact tables; about 217,000 (13%) are placeholders: `N/A` 136,425, em dash 52,739, empty 13,766, `Unknown` 10,136, `-` 4,121, `null` 32. They are concentrated:
  - **Finalizer queue table:** `Disposed` and `Disposed Field Found`, 67,843 `N/A` each (135,686 of the 136,425 `N/A`).
  - **Types table:** `Est. Retained` 13,865 and `LOH Bytes` 13,855 em dashes. **Leak candidates table:** `Root Chain` 13,875, `Root Kind` 9,360 em dashes, `Class` 9,360 `Unknown`.
  - **Long-lived and transient types:** `Finalizable` empty, 9,273 and 4,336. **Leak instances / groups:** `Root Hint` 2,893 and `Category` 973 `-`.
  - Whether each is "not measured" or "not applicable" is not visible in the cell text. Several look like not applicable (a finalizer entry with no disposed field), some like not measured (retained bytes when dominators were unavailable). Telling them apart needs the builders to say, not a guess from the string.

## Verification (every step)

- One run per step on the 3.35 GB reference dump, foreground, never the 25 GB dump.
- Claims, entities and findings compared with the previous run; only the intended differences appear.
- Validator reports 0 warnings; harness and focused unit suites green.
- Results written beside the earlier ones in `m2-step1/`.

## Open questions

- Whether `boundary` ("312 of 14,003 paths") is available from `RootPathFinder` and the dominator walk without new counters. *Measure first* in step 1; if not, the boundary field ships empty for those analyzers rather than invented.
- Whether unknown-vs-not-applicable can be told apart from today's cell text, or needs the builders to say.

## Not in this plan

- Verdict, `nextCapture`, dual confidence beyond what M1 shipped (D5).
- Per-capability coverage for every analyzer (D1).
- Cross-artifact coverage beyond what trend needs (D6).
