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
3. **`Claim` record** *(pending)* — per D1, in `Sdk/Synthesis`.
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
4. **Rule migration** *(pending)* — all 76 rules author `assertion`, `counter`, `falsifiedBy`.
   - Batched by domain package, per-analyzer rule and its cross-analyzer rules together.
   - The single biggest execution risk ([§ 21 assumption 3](ReportSystemVision.md#21-risks-assumptions-and-open-questions)):
     a rule that can't state what would falsify it is reported back, not padded.
   - Gates: [§ 17.1](ReportSystemVision.md#171-editorial-gates) rows for assertion, `falsifiedBy`,
     `counter`, registry; plus a test flagging near-verbatim `falsifiedBy` text across claims.
   - Each batch verified on the reference dump, one run at a time.
5. **Generated prose + `dd-claim`** *(pending)* — per D3.
   - One prose generator over `assertion`; HTML, Markdown, text all call it.
   - `dd-claim` widget: support, counter, falsification tests, both confidence numbers with limiting
     factors; conflict state per § 6.3.
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
