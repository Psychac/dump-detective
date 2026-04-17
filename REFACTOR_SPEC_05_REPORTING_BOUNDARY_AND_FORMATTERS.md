# DumpDetective — Spec 05: Reporting Boundary and Formatters

> **Phase:** Iteration 5
> **Prerequisite:** `REFACTOR_SPEC_01_SOLUTION_STRUCTURE.md`, `REFACTOR_SPEC_02_FULL_REWRITE_EXECUTION_PLAN.md`, `REFACTOR_SPEC_03_ANALYZER_CONTRACTS_AND_PIPELINE.md`, `REFACTOR_SPEC_04_CLI_HOSTING_AND_COMMAND_MODEL.md`
> **Target Runtime:** `.NET 10`

---

## 1. Goal

Establish a strict reporting architecture where:
- `ReportBuilder` composes report content from domain data only,
- formatter implementations (`Markdown`, `Text`, `Html`) perform rendering only,
- duplicate sections are removed at source,
- long values are wrapped (never truncated),
- analyzer detail is preserved end-to-end.

---

## 2. Scope

### In Scope
- Reporting domain model design for composed report sections.
- `ReportBuilder` composition responsibilities and boundaries.
- Canonical section identity + source-level deduplication policy.
- `IReportFormatter` contract and formatter-specific rendering rules.
- Shared table rendering helpers with wrapping behavior.
- `AnalyzerReportRenderer` and `TrendReportComposer` boundaries.

### Out of Scope
- Analyzer internals and pipeline execution (Spec 03).
- CLI command parsing/hosting details (Spec 04).
- Full test suite expansion strategy details (Spec 06), except reporting-focused tests needed for acceptance here.

---

## 3. Non-Negotiable Reporting Rules

1. **Composition/Rendering separation is mandatory**: business/report-content decisions belong to composition services, not formatters.
2. **No formatter-driven deduplication**: section dedup is done in composition layer before formatting.
3. **No data truncation**: long names/values must wrap in tables/lists across all output formats.
4. **Actionable detail preserved**: evidence, context, and remediation hints must not be dropped to “summarize.”
5. **Single canonical section for semantically duplicate analyzer outputs**.

---

## 4. Reporting Architecture Contract

## 4.1 Component Responsibilities

### `Services/ReportBuilder.cs`
- consumes analyzer run outputs and metadata,
- composes canonical sections,
- performs source-level deduplication,
- preserves full evidence payload,
- outputs a formatter-agnostic report model.

### `Services/AnalyzerReportRenderer.cs`
- maps analyzer-specific domain data into normalized section payloads,
- no output-format logic,
- no console/file writing.

### `Services/TrendReportComposer.cs`
- composes trend sections from trend model inputs,
- does not render markdown/html/text directly.

### `Formatters/IReportFormatter.cs`
- renders fully composed report model into target representation,
- no analyzer-specific branching beyond generic display behavior.

### `Output/OutputWriter.cs`
- writes rendered output to destination(s),
- no composition, dedup, or business logic.

---

## 5. Report Composition Model

## 5.1 Canonical Report Model

Define/align model(s) to include at minimum:
- report metadata (`GeneratedAt`, input source identity, runtime version),
- ordered collection of `ReportSection` entries,
- optional diagnostics summary,
- optional trend summary.

## 5.2 `ReportSection` Contract

Each section must include:
- `SectionKey` (stable canonical key),
- `Title`,
- `Category`,
- `Severity`/priority metadata,
- `NarrativeSummary`,
- structured payload blocks (tables, bullets, key-value evidence),
- remediation/suggested action content when available,
- fingerprints or identity metadata for dedup/auditing.

## 5.3 Section Key Strategy

`SectionKey` must be deterministic and stable.
Recommended composition inputs:
- analyzer canonical name,
- normalized finding category,
- stable fingerprint fields.

---

## 6. Source-Level Deduplication Specification

## 6.1 Dedup Layer

Deduplication happens inside `ReportBuilder` (or dedicated composition helper called by it), before any formatter execution.

## 6.2 Dedup Match Criteria

Candidate duplicate sections are matched on:
- same canonical `SectionKey`, or
- same normalized fingerprint tuple (`Category`, `Title`, `PrimaryEntity`, `FingerprintHash`) when key absent.

## 6.3 Merge Behavior

When duplicates are detected:
- keep one canonical section,
- merge evidence collections without loss,
- merge remediation hints (distinct union),
- preserve highest severity where severity differs,
- maintain deterministic ordering.

## 6.4 Auditing

Optionally capture dedup diagnostics:
- duplicate count,
- merged section keys,
- evidence item count before/after merge.

---

## 7. Formatter Contract and Rules

## 7.1 `IReportFormatter`

Expected members:
- stable formatter identity/key,
- render method that accepts canonical report model,
- pure rendering behavior (no mutation of source model).

## 7.2 Common Rendering Rules

Applies to `Text`, `Markdown`, and `Html`:
- render all composed sections (no implicit filtering unless explicitly configured),
- preserve all evidence rows/items,
- do not truncate long values,
- include remediation and diagnostics details when present,
- preserve section ordering from composition layer.

## 7.3 Long-Value Wrapping Policy (Critical)

- **Never trim with ellipsis** for content-bearing fields.
- For text output: soft-wrap by column width boundaries while preserving full content.
- For markdown output: wrap inside cell text using line breaks or compatible wrapping strategy while retaining full value.
- For html output: use CSS/markup strategy enabling wrap (`overflow-wrap:anywhere` equivalent behavior).

---

## 8. Table Rendering Helper Specification

Create/align shared helper(s) (in `Reporting` utilities/services) to avoid formatter divergence.

Helper capabilities:
- consistent column width planning,
- soft-wrap algorithm for long tokens,
- row height normalization for wrapped cells,
- deterministic alignment and separator generation,
- safe rendering of null/empty values.

Rule: formatters must use shared helper primitives for table layout rather than ad-hoc per-formatter algorithms.

---

## 9. Ordering and Determinism

- section order is deterministic and independent of hash/dictionary iteration.
- suggested ordering:
  1. critical/high severity findings,
  2. category grouping,
  3. analyzer stable order,
  4. title/fingerprint tie-breakers.
- formatter output must preserve this order exactly.

---

## 10. Implementation Plan

## Step 1 — Model alignment
- finalize canonical report/section model contracts in reporting layer.
- ensure sufficient fields for detail preservation + dedup fingerprints.

## Step 2 — Composition hardening
- refactor `ReportBuilder` to composition-only responsibilities.
- move any rendering logic out of builder into formatter/helper layer.

## Step 3 — Dedup engine
- implement source-level dedup with deterministic merge policy.
- add diagnostics counters for dedup actions.

## Step 4 — Formatter alignment
- implement/align `IReportFormatter` for `Markdown`, `Text`, `Html`.
- remove any formatter-level duplicate filtering.

## Step 5 — Wrapping helpers
- centralize table wrapping and long-value rendering rules.
- ensure no truncation behavior remains.

## Step 6 — Trend boundary
- ensure trend composition lives in `TrendReportComposer`.
- formatters render composed trend sections like any other section.

---

## 11. Acceptance Criteria

1. `ReportBuilder` performs composition only (no format-specific shaping).
2. Duplicate report sections are removed/merged at source before formatting.
3. All formatters render from the same canonical report model.
4. Long values are wrapped, not truncated, in `md`, `txt`, and `html` outputs.
5. Actionable analyzer evidence/remediation data is preserved fully.
6. Output section ordering is deterministic across runs.

---

## 12. Test Plan

## 12.1 Unit Tests
- section key generation determinism.
- dedup merge policy (severity selection, evidence union, remediation union).
- long-value wrapping helper behavior with very long tokens.

## 12.2 Integration Tests
- run report composition with duplicate-prone analyzer inputs and verify single canonical section.
- verify formatter parity: same section count and section keys across `md/txt/html`.
- verify long fields appear fully in outputs (no truncation markers).

## 12.3 Snapshot/Golden Tests (Reporting-focused)
- snapshot per formatter for representative dataset:
  - normal names,
  - long type names,
  - duplicate-finding inputs,
  - rich remediation/evidence payloads.

---

## 13. Risks and Mitigations

1. **Risk:** Hidden formatter logic still mutates semantics  
   **Mitigation:** add formatter parity tests comparing section keys/counts to composition model.

2. **Risk:** Wrapping decreases readability in text format  
   **Mitigation:** implement stable width heuristics and add dedicated readability snapshots.

3. **Risk:** Dedup merge accidentally drops details  
   **Mitigation:** test evidence cardinality before/after merge and assert non-loss.

---

## 14. Deliverables

- composition-only `ReportBuilder` implementation.
- canonical section model and key strategy.
- source-level dedup engine with deterministic merge behavior.
- aligned `IReportFormatter` implementations (`Markdown`, `Text`, `Html`).
- shared wrapping/table helper(s) with no-truncation guarantee.
- reporting-focused unit/integration/snapshot tests passing.

---

## 15. Exit Criteria

- Build is green.
- Reporting-focused tests pass.
- Source-level dedup validated.
- No truncation in formatter outputs.
- Ready to start `REFACTOR_SPEC_06_TEST_STRATEGY_AND_GOLDEN_FILES.md`.
