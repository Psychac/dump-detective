# Single-Dump Report Implementation Plan v2

## Overview

This plan implements the tightened v2 contract in SingleDumpReportFormat.v2.md.

Current status is split into:

1. Completed baseline delivery (already shipped).
2. Professional hardening and insight upgrade (this plan).

---

## Scope

In scope:

1. Navigation and rendering integrity hardening.
2. Information architecture and first-screen incident brief.
3. Root-cause clustering and action dedupe improvements.
4. Mobile and dense-table usability fixes.
5. Readability and visual hierarchy uplift.
6. Evidence clarity and confidence calibration polish.
7. Performance budgeting and lazy rendering.
8. HTML, JSON, markdown parity and deterministic behavior.
9. Reading-mode toggle and mode-specific presentation.
10. Top-action ticket template export payloads.

Out of scope:

1. Analyzer algorithm changes unrelated to report output semantics.
2. Non-report product features.

---

## Compressed Status Snapshot

### Track A. Completed Baseline (Already Done)

1. v2 style and token foundation.
2. Severity and confidence visual semantics.
3. First-screen triage skeleton and action queue.
4. Deterministic ranking and scoring metadata.
5. Correlation, dedupe, conflict, provenance breadcrumbs.
6. Incident handoff and markdown parity.
7. Determinism and schema/component coverage.

### Track B. Hardening and Professionalization (Planned Here)

Status: Complete

Goal:

Close quality gaps found during rendered-report deep review:

1. Broken anchors and duplicate IDs.
2. Horizontal overflow and dense mobile table failures.
3. Over-verbose action queue and duplicated sibling top actions.
4. Inconsistent summary counters and trust-friction details.
5. High DOM weight and heavy above-the-fold cognitive load.

---

## Execution Plan

## Phase P1. Integrity and Trust Hardening

Status: Complete

Goal:

Make report behavior reliable and internally consistent before visual polish.

Tasks:

1. Add anchor integrity validator in render pipeline and fail on unresolved links.
2. Add unique ID allocator guard and fail on collisions.
3. Add summary counter cross-checks against detailed blocks.
4. Normalize number formatting policy per report locale.
5. Add diagnostics section for integrity warnings in non-strict mode.

Candidate files:

1. src/DumpDetective.Reporting/Templates/report.ui.js
2. src/DumpDetective.Reporting/Templates/report.renderers.sections.js
3. src/DumpDetective.Reporting/Services/ReportSerializer.cs
4. src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs

Acceptance:

1. Broken anchor count equals zero.
2. Duplicate ID count equals zero.
3. Summary counters match detail counts.
4. Deterministic formatting for displayed numerics.

Tests:

1. Anchor target existence test.
2. Duplicate ID rejection test.
3. Summary consistency regression test.
4. Locale formatting snapshot test.

Progress notes:

1. Added runtime render-integrity audit that reports duplicate IDs and broken in-page anchors.
2. Added unique DOM ID allocator in renderer shared helpers and applied it to finding, section, and provenance anchors.
3. Added action-link anchor resolution against concrete rendered findings before fallback anchor generation.
4. Added runtime dead-anchor remediation for unresolved hash links so users do not hit broken navigation targets.
5. Verified generated CrashAnalysis v2 HTML now reports duplicate IDs = 0 and broken anchors = 0 in render-integrity-report.
6. Added automated renderer contract tests for reading-mode toggle, ticket menu presence, and integrity markers.

---

## Phase P2. Incident Brief and IA Refactor

Status: Complete

Goal:

Turn first screen from data inventory into decision-support narrative.

Tasks:

1. Introduce Incident Brief block as first major section.
2. Reorder above-fold blocks to Incident Brief, Health Summary, Top Actions, Key Metrics.
3. Move deep metadata and verbose context into collapsible secondary sections.
4. Replace audience framing with two reading modes:
   1. Incident mode default.
   2. Forensics mode.
5. Add visible mode toggle in the report shell with persisted selection.
6. Ensure deep links preserve or gracefully restore selected mode.

Candidate files:

1. src/DumpDetective.Reporting/Templates/report.renderers.header.js
2. src/DumpDetective.Reporting/Templates/report.renderers.panels.js
3. src/DumpDetective.Reporting/Templates/report.renderers.sections.js
4. src/DumpDetective.Reporting/Templates/report.css

Acceptance:

1. First-screen triage answers what, severity, next steps without deep scrolling.
2. No large table above the fold.
3. Reading mode switch preserves semantic parity.
4. Mode toggle is discoverable and keyboard accessible.
5. Mode persistence works across navigation and anchor jumps.

Tests:

1. First-screen ordering snapshot test.
2. No-large-table-above-fold UI assertion.
3. Incident vs forensics mode parity test.
4. Mode toggle accessibility test.
5. Mode persistence and deep-link restore test.

Progress notes:

1. Added visible reading-mode toggle in report header actions.
2. Added Incident and Forensics mode persistence via local storage.
3. Added mode-aware deep-link handling that auto-restores Forensics view for deep analyzer anchors.
4. Validated real generated HTML behavior: mode toggle switches Incident/Forensics and persists selection.

---

## Phase P2b. Ticket Template Export from Top Actions

Status: Complete

Goal:

Allow direct copy-as-ticket payload generation from each top action.

Tasks:

1. Add top-action ticket template menu with providers:
   1. Azure DevOps.
   2. Jira.
   3. GitHub.
2. Build deterministic template composer using report and action context.
3. Include incident summary, why-now, validation, evidence refs, and limitations in all templates.
4. Provide one-click copy interactions and fallback plain-text rendering.

Candidate files:

1. src/DumpDetective.Reporting/Templates/report.renderers.panels.js
2. src/DumpDetective.Reporting/Templates/report.ui.js
3. src/DumpDetective.Reporting/Services/ReportSerializer.cs
4. src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs

Acceptance:

1. Every top action exposes all three ticket template options.
2. Template content is deterministic for identical input.
3. Copied payload includes required fields and stable evidence links.

Tests:

1. Ticket template payload snapshot test per provider.
2. Copy interaction hook test.
3. Deterministic template ordering test.

Progress notes:

1. Added top-action ticket template generation for Azure DevOps, Jira, and GitHub formats.
2. Added copy-to-clipboard actions for ticket templates in Action Queue rows.
3. Added required ticket-template-menu anchor container with single-instance ID behavior.
4. Validated ticket template action buttons render for all providers in real generated CrashAnalysis report.

---

## Phase P3. Root-Cause Clustering and Action UX

Status: Complete

Goal:

Reduce noise and improve action clarity.

Tasks:

1. Add root-cause cluster layer above individual findings.
2. Rank action clusters, not duplicate sibling findings.
3. Replace dense action queue default with triage cards:
   1. Do now.
   2. Next sprint.
   3. Watchlist.
4. Keep full tabular export path for audit and CSV.

Candidate files:

1. src/DumpDetective.Reporting/Services/ActionPriorityService.cs
2. src/DumpDetective.Reporting/Services/CorrelationService.cs
3. src/DumpDetective.Reporting/Models/ExecutiveSummaryModels.cs
4. src/DumpDetective.Reporting/Templates/report.renderers.panels.js
5. src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs

Acceptance:

1. Top actions contain no near-duplicate siblings at parent rank level.
2. Every action card includes why now, owner, validation step, and evidence links.
3. JSON preserves raw child findings and cluster mapping.

Tests:

1. Cluster dedupe deterministic test.
2. Action diversity test on known duplicate-heavy fixture.
3. HTML and markdown action narrative parity test.

Progress notes:

1. Added action clustering in ActionPriorityService to consolidate near-duplicate sibling findings before ranking.
2. Updated ranked action rationale to include cluster breadth context and analyzer spread.
3. Added triage lane cards (Now, Next, Watch) above Action Queue while retaining full table/export path.
4. Added clustering regression coverage in ReportingCompositionTests.

---

## Phase P4. Responsive and Readability Uplift

Status: Complete

Goal:

Eliminate horizontal overflow and improve scanability.

Tasks:

1. Enforce no page-level horizontal overflow at mobile breakpoints.
2. Convert dense tables to stacked or card mode below 900 px.
3. Clamp long prose in table cells with expand affordance.
4. Raise readability baseline by reducing micro-label density and repeated chips.
5. Tighten section rhythm, spacing, and hierarchy for scan speed.

Candidate files:

1. src/DumpDetective.Reporting/Templates/report.css
2. src/DumpDetective.Reporting/Templates/report.renderers.sections.js
3. src/DumpDetective.Reporting/Templates/report.renderers.panels.js

Acceptance:

1. Mobile layout remains readable and fully navigable.
2. No forced horizontal panning for primary workflow.
3. Action and finding cards remain scannable under constrained widths.

Tests:

1. Viewport visual regression at 390 px, 768 px, 1280 px.
2. Overflow audit test for key containers and tables.
3. Typography density snapshot baseline test.

Progress notes:

1. Added responsive stacked table mode for data-responsive-stack tables below 900 px.
2. Added long-cell clamp and expand/collapse affordance for dense table prose.
3. Hardened mobile header action layout to wrap without page-level overflow.
4. Verified runtime viewport overflow audit at 390, 768, and 1280 px with zero page-level overflow.

---

## Phase P5. Evidence Clarity and Confidence Calibration

Status: Complete

Goal:

Increase trust by making recommendations contextual and verifiable.

Tasks:

1. Enrich lead findings with compact why triggered and threshold context.
2. Add expected post-fix metric direction for each top action.
3. Surface probable false-positive caveats where applicable.
4. Reduce generic recommendation wording in summary surfaces.

Candidate files:

1. src/DumpDetective.Reporting/Services/ConfidenceScoringService.cs
2. src/DumpDetective.Reporting/Templates/report.renderers.sections.js
3. src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs

Acceptance:

1. Critical and warning lead findings include concrete metric evidence.
2. Validation guidance is specific and measurable.
3. Confidence caveats visible without expanding deep details.

Tests:

1. Confidence caveat propagation test.
2. Threshold context presence test.
3. Post-fix validation phrasing snapshot test.

Progress notes:

1. Top-action validation guidance now includes expected post-fix metric direction by signal type.
2. Clustered why-now narratives now include explicit score and related-finding context.
3. Confidence caveat propagation remains surfaced in action queue summary notes.

---

## Phase P6. Performance Budget and Render Efficiency

Status: Complete

Goal:

Reduce render weight and improve interaction smoothness on large reports.

Tasks:

1. Add lazy rendering for collapsed deep sections and large tables.
2. Defer hydration for non-critical interactive components.
3. Reduce duplicated markup in repeated finding cards.
4. Introduce report performance budget checks in CI.

Candidate files:

1. src/DumpDetective.Reporting/Templates/report.ui.js
2. src/DumpDetective.Reporting/Templates/report.renderers.sections.js
3. src/DumpDetective.Reporting/Templates/report.renderers.panels.js
4. tests/DumpDetective.Tests/Reporting/Performance/*

Acceptance:

1. Initial render path remains responsive on large single-dump reports.
2. Heavy sections do not block first-screen interactivity.
3. Performance budget thresholds enforced in tests.

Tests:

1. Render budget regression test.
2. Expand latency test for dense sections.
3. Lazy render correctness test.

Progress notes:

1. Added lazy hydration for large detail tables (threshold-based) so heavy row DOM is deferred until expand.
2. Added visible lazy-hydration hint and managed-state refresh after hydration.
3. Added renderer contract tests for lazy-hydration markers and responsive table hooks.

---

## Phase P7. Validation Matrix and Rollout

Status: Complete

Goal:

Ship safely with measurable confidence.

Tasks:

1. Extend audit matrix to include:
   1. Anchor integrity.
   2. ID uniqueness.
   3. Mobile overflow.
   4. Clustered top-action diversity.
   5. Counter consistency.
2. Add staged flags:
   1. v2 baseline.
   2. v2 professional hardening.
3. Publish before and after sample reports for reviewer signoff.
4. Update docs and sample config with new toggles.

Candidate files:

1. tests/DumpDetective.Tests/**
2. docs/ReportStructure/SingleDumpReportV2.FinalAuditMatrix.md
3. config.sample.json
4. src/DumpDetective.Cli/config.json

Acceptance:

1. All gates in updated matrix are green.
2. Rollback to baseline mode remains available.
3. HTML, JSON, markdown incident story stays aligned.

Progress notes:

1. Updated audit evidence via targeted tests and runtime crash-report verification.
2. Confirmed anchor integrity, ID uniqueness, mode toggle persistence, ticket templates, clustering behavior, and mobile overflow gates.
3. Verified report generation and dynamic rendering against real CrashAnalysis dump with v2 style.

---

## Milestones

1. M1: P1 integrity hardening complete.
2. M2: P2 IA refactor, reading modes, and toggle complete.
3. M3: P2b ticket template export complete.
4. M4: P3 clustering and action UX complete.
5. M5: P4 responsive and readability complete.
6. M6: P5 confidence and evidence refinement complete.
7. M7: P6 performance budgets complete.
8. M8: P7 rollout gates green and promoted.

---

## Risks and Mitigations

1. Risk: cluster dedupe may hide meaningful outliers.
   1. Mitigation: always expose child findings and provenance links.
2. Risk: responsive table transformations may reduce detail discoverability.
   1. Mitigation: add explicit expand controls and preserved export path.
3. Risk: performance optimizations may break deterministic output.
   1. Mitigation: enforce deterministic snapshot and ordering tests post-optimization.
4. Risk: IA refactor may disrupt existing user muscle memory.
   1. Mitigation: staged rollout and mode toggle during transition.

---

## Definition of Done

v2 professionalization is done when:

1. Integrity checks pass with zero broken anchors and zero duplicate IDs.
2. First screen is action-oriented and table-free.
3. Top actions are clustered, diverse, deterministic, and evidence-linked.
4. Mobile is readable with no page-level horizontal overflow.
5. Confidence and caveats are explicit and actionable.
6. Render performance budgets pass in CI.
7. HTML, JSON, markdown remain semantically aligned.
8. Incident and Forensics reading modes are fully functional and toggleable.
9. Copy-as-ticket templates are available from every top action.
