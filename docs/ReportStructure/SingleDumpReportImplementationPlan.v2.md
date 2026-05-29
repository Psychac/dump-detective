# Single-Dump Report Implementation Plan v2

## Overview

This plan implements [SingleDumpReportFormat.v2.md](SingleDumpReportFormat.v2.md) end-to-end.

Current state:

1. Visual and UX baseline is implemented.
2. Functional decision-support layer is not fully implemented.

Plan structure:

1. Track A: Completed visual foundation (compressed summary).
2. Track B: Detailed functional execution roadmap.

---

## Scope

In scope:

1. Action prioritization and ranking.
2. Confidence and trust model.
3. Cross-analyzer correlation and dedupe.
4. Investigation workflow and handoff output.
5. Determinism, explainability, and auditability.
6. HTML/JSON/Markdown parity for functional semantics.

Out of scope:

1. Analyzer internals unrelated to report semantics.
2. Non-report product concerns.

---

## Status Snapshot

### Track A (Visual Foundation)

Status: Completed

Compressed summary of completed steps:

1. A1 Style version gating and config plumbing.
2. A2 Tokenized design layer and severity semantics.
3. A3 Responsive three-zone shell.
4. A4 Above-the-fold triage composition.
5. A5 Unified section card anatomy.
6. A6 Motion and interaction baseline.
7. A7 Accessibility baseline and screen-reader summary.
8. A8 Print/export mode with metadata footer.
9. A9 Renderer smoke/parity/component tests.

Notes:

1. Print footer now correctly screen-hidden and print-only.
2. Required spec token names are now exposed and test-guarded.

### Track B (Functional Upgrade)

Status: Not Started

Execution phases:

1. B1 Action Prioritization Engine.
2. B2 Confidence and Trust Model.
3. B3 Cross-Analyzer Correlation.
4. B4 Workflow and Handoff UX.
5. B5 Determinism and Explainability.
6. B6 Validation Matrix and Rollout.

---

## Prerequisites

1. v1 report generation pipeline operational.
2. v2 visual baseline merged and green.
3. Stable section and finding anchors available.
4. Existing canonical serializer/formatters available.

---

## Track B Detailed Plan

## Step B1: Action Prioritization Engine

Status: Not Started

Goal:

Generate deterministic top actions from findings using explicit, explainable factors.

Implementation tasks:

1. Add action-ranking model in reporting domain:
   1. `PriorityFactors` (severity, blast radius, impact likelihood, time-to-mitigate, confidence, dependency risk).
   1. `RankedActionRecord` (action text, why-now, owner role, validation step, supporting finding refs).
2. Add ranking service:
   1. `ActionPriorityService` with deterministic tie-break logic.
   1. Stable sort fallback: fingerprint, analyzer, title.
3. Wire into report composition:
   1. Produce ranked actions before HTML panel rendering.
   1. Persist ranking factors in JSON payload.
4. Keep markdown narrative aligned with ranked actions.

Candidate files:

1. `src/DumpDetective.Reporting/Models/*` (new ranking records).
2. `src/DumpDetective.Reporting/Services/*` (ranking service).
3. `src/DumpDetective.Reporting/Services/ReportSerializer.cs`.
4. `src/DumpDetective.Reporting/Templates/report.renderers.panels.js`.
5. `src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs`.

Acceptance:

1. Same input produces same action ordering.
2. Every action has rationale and evidence pointers.
3. JSON includes factor breakdown per action.

Tests:

1. Deterministic ordering test with shuffled input.
2. Tie-break test.
3. Serialization test for factor fields.

---

## Step B2: Confidence and Trust Model

Status: Not Started

Goal:

Make confidence explicit, auditable, and decision-linked.

Implementation tasks:

1. Introduce confidence composition model:
   1. Evidence completeness score.
   1. Cross-analyzer consistency score.
   1. Heuristic penalty.
   1. Coverage/freshness modifier.
2. Emit confidence caveats and verification guidance:
   1. Mandatory verification message for low-confidence criticals.
3. Propagate confidence into action ranking (input to B1).
4. Ensure confidence appears consistently in HTML/JSON/markdown.

Candidate files:

1. `src/DumpDetective.Reporting/Models/*` (confidence records).
2. `src/DumpDetective.Reporting/Services/*` (confidence scoring utility).
3. `src/DumpDetective.Reporting/Templates/report.renderers.sections.js`.
4. `src/DumpDetective.Reporting/Templates/report.renderers.header.js`.
5. `src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs`.

Acceptance:

1. Confidence never represented by color only.
2. Low-confidence critical findings always include verification step.
3. Confidence caveats visible before deep drill-down.

Tests:

1. Confidence band mapping tests.
2. Caveat propagation tests.
3. HTML smoke assertion for verification guidance on low-confidence critical.

---

## Step B3: Cross-Analyzer Correlation and Dedupe

Status: Not Started

Goal:

Provide correlated incident hypotheses and suppress duplicate noise.

Implementation tasks:

1. Build correlation service:
   1. Fingerprint clustering.
   1. Domain bridge heuristic linking.
   1. Conflict detector when findings disagree.
2. Emit `CorrelationRecord` with:
   1. source IDs, link rationale, net confidence, affected subsystems.
3. Drive executive summary and cross-domain panels from correlation output.
4. Preserve raw findings in JSON while presenting deduped summaries in HTML/markdown.

Candidate files:

1. `src/DumpDetective.Reporting/Models/*` (correlation records).
2. `src/DumpDetective.Reporting/Services/*` (correlation engine).
3. `src/DumpDetective.Reporting/Templates/report.renderers.header.js`.
4. `src/DumpDetective.Reporting/Templates/report.renderers.panels.js`.

Acceptance:

1. Duplicate findings collapse into one narrative entry with provenance links.
2. Correlation entries show explicit rationale.
3. Disagreement flags shown when confidence conflicts are material.

Tests:

1. Dedupe cluster test.
2. Correlation rationale presence test.
3. Conflict-flag emission test.

---

## Step B4: Investigation Workflow and Handoff Output

Status: Not Started

Goal:

Support triage -> validate -> decide -> handoff flow directly in report.

Implementation tasks:

1. Add investigation jump paths:
   1. top action -> finding -> section evidence -> provenance.
2. Add handoff block content contract:
   1. incident summary, top actions, risk-if-ignored, evidence refs, known limitations.
3. Add concise copy/export helpers for handoff payload.
4. Ensure markdown preserves handoff narrative ordering.

Candidate files:

1. `src/DumpDetective.Reporting/Templates/report.ui.js`.
2. `src/DumpDetective.Reporting/Templates/report.renderers.panels.js`.
3. `src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs`.
4. `src/DumpDetective.Reporting/Models/*` (handoff block model).

Acceptance:

1. Analyst can traverse action to provenance in <= 2 interactions.
2. Handoff block present and complete.
3. Markdown includes same handoff narrative in deterministic order.

Tests:

1. Interaction hook tests for jump links.
2. Handoff block snapshot tests (HTML + markdown).

---

## Step B5: Determinism, Explainability, and Versioned Scoring

Status: Not Started

Goal:

Make scoring and ordering reproducible and auditable.

Implementation tasks:

1. Introduce `ScoringModelVersion` in report metadata.
2. Emit explainability payload for ranking decisions.
3. Normalize sort keys and null handling globally.
4. Add deterministic serialization checks for repeated runs.

Candidate files:

1. `src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs`.
2. `src/DumpDetective.Reporting/Services/ReportSerializer.cs`.
3. `src/DumpDetective.Reporting/Serialization/*`.

Acceptance:

1. Repeated runs on identical input yield byte-stable ordering for ranked blocks.
2. Scoring model version appears in JSON and HTML metadata.

Tests:

1. Repeatability regression test.
2. Metadata presence test for scoring version.

---

## Step B6: Validation Matrix and Rollout

Status: Not Started

Goal:

Expand test coverage from visual parity to functional reliability, then roll out safely.

Implementation tasks:

1. Add functional integration suite:
   1. prioritization determinism.
   1. confidence propagation.
   1. correlation dedupe/conflict.
   1. handoff block completeness.
2. Add golden JSON fixtures for priority/correlation scenarios.
3. Add rollout flags for staged enablement:
   1. v2 visual-only mode.
   1. v2 full-functional mode.
4. Update docs and sample config for new fields.

Candidate files:

1. `tests/DumpDetective.Tests/**`.
2. `config.sample.json`.
3. `src/DumpDetective.Cli/config.json` (sample/default behavior if needed).
4. `docs/ReportStructure/*`.

Acceptance:

1. All new functional tests green.
2. Existing schema and parity tests remain green.
3. Feature flags allow rollback to visual-only path.

---

## Milestones

1. Milestone M1: B1 + B2 merged, deterministic top actions with confidence-aware ranking.
2. Milestone M2: B3 merged, cross-domain correlation and dedupe live.
3. Milestone M3: B4 + B5 merged, workflow/handoff + explainability complete.
4. Milestone M4: B6 complete, validation matrix green and rollout ready.

---

## Risks and Mitigations

1. Risk: ranking logic instability between runs.
   1. Mitigation: strict stable sort policy + deterministic tests.
2. Risk: confidence scoring perceived as opaque.
   1. Mitigation: expose factor breakdown in JSON and UI rationale text.
3. Risk: correlation over-merges unrelated findings.
   1. Mitigation: conservative threshold + visible provenance links + conflict flags.
4. Risk: added functionality increases first-screen clutter.
   1. Mitigation: preserve summary-first layout, keep detail collapsible.

---

## Rollout Strategy

1. Keep default as v1.
2. Enable v2 visual baseline in preview (already available).
3. Enable v2 functional mode behind flag for internal users.
4. Collect analyst feedback on action ranking and confidence trust.
5. Promote v2 functional mode after parity, determinism, and usability signoff.

---

## Definition of Done (v2 Full)

1. Visual baseline remains compliant and stable.
2. Top actions are deterministic, explainable, and confidence-aware.
3. Cross-analyzer correlations reduce noise without hiding evidence.
4. Investigation workflow supports action-to-provenance navigation quickly.
5. Handoff output is concise, complete, and channel-ready.
6. JSON/markdown remain semantically aligned with HTML.
7. Full validation matrix is green.
