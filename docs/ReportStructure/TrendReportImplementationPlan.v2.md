# Trend Report Implementation Plan v2

Last updated: 2026-06-01 — regression classification and interactive T3 filters implemented; tests pending.

## Overview

This plan implements the comparative UX and visual upgrades defined in [TrendReportFormat.v2.md](TrendReportFormat.v2.md), while preserving the structural/data contract in [TrendReportFormat.md](TrendReportFormat.md).

Scope: trend HTML composition and trend-specific metadata rendering; JSON/markdown maintain semantic parity.

---

## Prerequisites

1. Trend v1 implementation completed (`T0`-`T7`).
2. Single-dump v2 token/layout primitives available.
3. Stable trend anchors (`T0`-`T7`, `detail-{index}`) available.

---

## Step TV2-1: Style Version and Mode Wiring

Goal: Ensure trend v2 is explicit and isolated.

Changes:

- Respect `ReportStyleVersion = v2` in trend renderer path.
- Keep `IsTrendReport` as the mode switch; style switch is independent.

Acceptance:

- Trend v1 remains unchanged under style v1.
- Trend v2 can be enabled without changing trend data assembly.

- Status: Completed — trend HTML rendering now emits `ReportStyleVersion = "v2"` and `IsTrendReport` remains the mode switch.

---

## Step TV2-2: Add T0b Change Story

Goal: Add a narrative summary directly after trend header.

Changes:

- Build new trend summary block from existing trend aggregates:
  1. first major regression point
  2. largest inflection snapshot
  3. top worsening domains
  4. likely coupling hints
- Render as plain-language card with metric references.

Files to touch:

- trend composer (new builder or helper)
- trend HTML renderer section order

Acceptance:

- Reader can identify top change narrative in first screen.

Status: Completed — T0b Change Story implemented and rendered in header.

---

## Step TV2-3: Extend T1 with Risk Velocity

Goal: Move from static severity to severity + momentum.

Changes:

- Add derived fields for each domain row:
  1. `VelocityScore`
  2. `VolatilityScore`
  3. `ConfidenceTrend`
- Render directional chips (`accelerating`, `stable`, `recovering`).

Files to touch:

- trend health scorecard builder
- trend scorecard renderer

Acceptance:

- Domain rows display both current severity and movement direction.

 - Status: Completed — `VelocityScore`, `VolatilityScore`, and `ConfidenceTrend` implemented; movement chips rendered in T1.

---

## Step TV2-4: Regression Classification

Goal: Make regression triage explicit.

Changes:

- Classify each regression finding as:
  1. `NewRisk`
  2. `AmplifiedRisk`
  3. `VolatileRisk`
- Add dashboard filtering controls by class.

Files to touch:

- regression finding assembly logic
- regression dashboard UI

Acceptance:
- Status: Completed — classification persisted to report JSON (`regressionClass`), finding cards render a regression chip, and the T3 dashboard now exposes interactive single-select filters with counts. Unit tests for classification and JSON parity remain to be added.

- Dashboard can isolate each class and counts match source findings.

---

## Step TV2-5: Significance Thresholding

Goal: Reduce trend noise.

Changes:

- Apply default significance rules before rendering trend tables:
  1. relative delta threshold
  2. severity-threshold crossings
  3. consecutive worsening requirement
- Keep hidden-row count and show disclosure text.

Files to touch:

- timeline/regression builders
- trend renderer summaries

Acceptance:

- Low-signal fluctuations are suppressed by default.
- Raw values remain available in JSON.

---

## Step TV2-6: Add T3b Correlation Timeline

Goal: Surface multi-domain coupling events early.

Changes:

- Add correlation event extraction from existing trend points/findings.
- Render compact timeline lane with event cards.
- Include links to relevant sections/snapshots.

Files to touch:

- trend composer (correlation builder)
- trend renderer (new section)

Acceptance:

- Cross-domain events visible without opening snapshot detail.

---

## Step TV2-7: T4 Timeline Readability Upgrade

Goal: Keep dump-wise detail but improve scan speed.

Changes:

- Preserve `Dump 1..N` columns.
- Add directional markers and compact delta chips per step.
- Highlight severe direction changes.

Files to touch:

- timeline table renderer
- trend CSS tokens/classes

Acceptance:

- Intermediate snapshots remain visible.
- Direction and magnitude are both readable in one pass.

---

## Step TV2-8: T6 Snapshot Detail Framing

Goal: Keep compare story primary, deep detail secondary.

Changes:

- Ensure snapshot detail block stays collapsed by default.
- Add explicit divider heading: `Current state deep dive`.
- Keep jump links from snapshot strip stable.

Acceptance:

- Compare layer is readable as standalone view.
- Snapshot deep dive reachable in <= 2 interactions.

---

## Step TV2-9: Trend Accessibility and Motion

Goal: Improve temporal comprehension without harming accessibility.

Changes:

- Direction conveyed by text/icon + color.
- Keyboard focus for timeline points and snapshot links.
- Reduced-motion support disables staged reveals.

Acceptance:

- Trend direction remains understandable in non-color mode.
- Timeline navigation is keyboard accessible.

---

## Step TV2-10: Trend-Specific Test Pack

Goal: Prevent regression in compare story quality.

Tests:

1. First-screen composition test (T0, T0b, T1, T2 present in order).
2. Regression-classification unit tests.
3. Significance-threshold behavior tests (suppressed counts).
4. Correlation timeline rendering tests.
5. Visual regression snapshots for desktop/tablet/mobile.
6. JSON parity tests: no semantic value loss when style v2 is enabled.

Acceptance:

- Trend story remains consistent across HTML, markdown, and JSON.

---

## Rollout Strategy

1. Ship trend v2 behind style flag.
2. Validate against multi-snapshot real incidents.
3. Tune thresholds from field feedback.
4. Promote to default after parity, performance, and usability signoff.

---

## Done Criteria

1. Reader identifies biggest regression and inflection point in first screen.
2. Noise suppression reduces low-signal rows while preserving material shifts.
3. Correlation timeline surfaces multi-domain events clearly.
4. Deep snapshot detail remains available without dominating the report.

---

## Functional Upgrade Track (Additive)

This track adds decision-support behavior on top of the existing visual/comparative plan.
Existing sections above remain valid and unchanged.

### Track Status

1. FTV2-1 Prioritized Trend Actions: Not Started
2. FTV2-2 Trend Confidence Model: Not Started
3. FTV2-3 Correlation Event Payload: Not Started
4. FTV2-4 Suppression Explainability: Not Started
5. FTV2-5 Determinism and Scoring Versioning: Not Started
6. FTV2-6 Workflow and Handoff: Not Started
7. FTV2-7 Functional Validation Matrix: Not Started

---

## Step FTV2-1: Prioritized Trend Actions

Goal: rank trend actions by urgency and impact, not just severity snapshots.

Implementation tasks:

1. Add trend action ranking factors model:
  1. severity
  2. velocity
  3. persistence
  4. blast radius
  5. change confidence
  6. mitigation urgency
2. Add deterministic ranking service for trend actions.
3. Emit rationale and evidence pointers per action.
4. Ensure markdown mirrors ranked action order.

Candidate files:

1. `src/DumpDetective.Reporting/Models/*` (trend action records).
2. `src/DumpDetective.Reporting/Services/*` (ranking service).
3. `src/DumpDetective.Reporting/Templates/report.renderers.panels.js`.
4. `src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs`.

Acceptance:

1. Same input yields same action ordering.
2. Every action includes why-now and evidence references.

---

## Step FTV2-2: Trend Confidence Model

Goal: expose confidence in change independently from single-snapshot evidence confidence.

Implementation tasks:

1. Introduce trend confidence fields:
  1. evidence confidence
  2. change confidence
2. Compute change confidence from sample depth, consistency, oscillation, and data gaps.
3. Add low-confidence verification guidance for high-severity regressions.
4. Feed confidence into action ranking factors.

Candidate files:

1. `src/DumpDetective.Reporting/Models/*`.
2. `src/DumpDetective.Reporting/Services/*`.
3. `src/DumpDetective.Reporting/Templates/report.renderers.header.js`.
4. `src/DumpDetective.Reporting/Templates/report.renderers.sections.js`.

Acceptance:

1. High-severity low-confidence entries visibly request verification.
2. Confidence caveats are visible before deep snapshot detail.

---

## Step FTV2-3: Correlation Event Payload and Causality Hints

Goal: make T3b events machine-readable and auditable.

Implementation tasks:

1. Add correlation event schema with event type, domains, signals, confidence.
2. Add optional causal-hypothesis text.
3. Wire payload into HTML timeline and JSON output.
4. Label correlations as suggestive, not definitive.

Candidate files:

1. `src/DumpDetective.Reporting/Models/*` (correlation event records).
2. `src/DumpDetective.Reporting/Services/*` (correlation builder).
3. `src/DumpDetective.Reporting/Templates/report.renderers.sections.js`.

Acceptance:

1. Every correlation event contains rationale and confidence.
2. HTML and JSON present consistent event semantics.

---

## Step FTV2-4: Suppression Explainability

Goal: make noise filtering transparent.

Implementation tasks:

1. Track suppression reason per hidden row.
2. Expose threshold values used for suppression.
3. Surface hidden-row explainability in summary and JSON.
4. Prevent suppression of critical transitions.

Candidate files:

1. `src/DumpDetective.Reporting/Models/*`.
2. `src/DumpDetective.Reporting/Services/*` (thresholding logic).
3. `src/DumpDetective.Reporting/Templates/report.renderers.sections.js`.

Acceptance:

1. Hidden rows are counted and explainable.
2. Critical transitions are always retained.

---

## Step FTV2-5: Determinism and Scoring Versioning

Goal: guarantee reproducibility and auditability.

Implementation tasks:

1. Add metadata fields:
  1. `TrendScoringModelVersion`
  2. `TrendThresholdProfile`
2. Standardize tie-break rules across trend sections.
3. Add repeatability checks for ordering and scores.

Candidate files:

1. `src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs`.
2. `src/DumpDetective.Reporting/Services/ReportSerializer.cs`.
3. `src/DumpDetective.Reporting/Serialization/*`.

Acceptance:

1. Repeated identical runs produce stable ranking/order.
2. Scoring and threshold profile metadata are emitted.

---

## Step FTV2-6: Workflow and Handoff Block

Goal: support operational incident handoff from trend report.

Implementation tasks:

1. Add trend handoff block with:
  1. baseline-to-current summary
  2. top regressions
  3. urgent actions
  4. caveats and limitations
  5. evidence anchors
2. Ensure quick navigation paths:
  1. regression -> timeline row -> snapshot detail
  2. correlation event -> involved domains
3. Preserve handoff narrative in markdown.

Candidate files:

1. `src/DumpDetective.Reporting/Templates/report.renderers.panels.js`.
2. `src/DumpDetective.Reporting/Templates/report.ui.js`.
3. `src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs`.

Acceptance:

1. Handoff narrative available without opening deep sections.
2. Key navigation paths require <= 2 interactions.

---

## Step FTV2-7: Functional Validation Matrix

Goal: lock in functional behavior with tests.

Test additions:

1. Deterministic ranking tests for trend actions.
2. Trend confidence propagation tests.
3. Correlation payload and rationale tests.
4. Suppression explainability and critical-row retention tests.
5. Handoff block completeness tests (HTML + markdown + JSON).
6. Repeatability tests for scoring/order with identical input.

Acceptance:

1. New functional tests pass.
2. Existing visual/parity tests remain green.

---

## Functional Milestones

1. TM-F1: FTV2-1 + FTV2-2 complete (action ranking + confidence).
2. TM-F2: FTV2-3 + FTV2-4 complete (correlation payload + suppression explainability).
3. TM-F3: FTV2-5 + FTV2-6 complete (determinism + handoff workflow).
4. TM-F4: FTV2-7 complete and rollout-ready.

---

## Functional Rollout Notes

1. Keep trend v2 visual path available as baseline.
2. Gate functional behavior behind explicit feature toggles.
3. Tune thresholds and ranking weights from incident feedback.
4. Promote after determinism, parity, and usability signoff.
