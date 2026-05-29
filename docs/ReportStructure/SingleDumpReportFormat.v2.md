# Single-Dump Report Format v2

## Purpose

Define a compact, testable contract for a professional single-dump report that is:

1. Action-first in incident triage.
2. Evidence-grounded and auditable.
3. Readable on desktop and mobile.
4. Semantically consistent across HTML, JSON, and markdown.

This extends SingleDumpReportFormat.md. v1 field availability remains authoritative.

---

## What Is Already Implemented (Minified)

The current v2 baseline already delivers:

1. Version-gated style path and tokenized visual system.
2. Severity semantics, confidence display, and section cards.
3. First-screen triage composition and action queue panel.
4. Confidence-aware deterministic action ranking and model version metadata.
5. Correlation extraction, dedupe, conflict signaling, and provenance breadcrumbs.
6. Incident handoff block and markdown parity.
7. Deterministic ordering tests and schema/component contract tests.

This document defines the required hardening and polish layer on top of that baseline.

---

## Core Functional Contract

### F0. Reading Modes (Primary Framing)

Report framing is mode-based, not audience-labeled.

Required modes:

1. Incident mode (default): concise, action-driven, root-cause grouped.
2. Forensics mode: full analyzer depth, raw tables, and provenance-first detail.

Required behavior:

1. A visible mode toggle must be present in the report shell.
2. Switching modes must not change semantic data, only presentation density and default expansion.
3. Current mode must persist across in-page navigation and deep links.
4. JSON remains full-fidelity regardless of active mode.

### F1. Incident Brief First

Above the fold must start with an Incident Brief block before deep detail.

Required fields:

1. Primary incident hypothesis.
2. Severity and blast radius summary.
3. Top 3 immediate actions with owner role.
4. Evidence strength summary with confidence caveat signal.
5. Time-boxed response lane (Now, Next, Watch).

No large data tables may appear before the Incident Brief and Top Actions.

### F2. Action Prioritization and Root-Cause Clustering

Top actions must be clustered by root cause before ranking to avoid duplicate siblings.

Required behavior:

1. Collapse near-duplicate findings into one parent action cluster.
2. Keep child findings accessible as supporting evidence.
3. Rank parent actions with deterministic tie-breaks.
4. Preserve full raw findings in JSON.

Ranking dimensions:

1. Severity.
2. Blast radius.
3. Impact likelihood.
4. Time to mitigate.
5. Confidence composition.
6. Dependency and cross-domain risk.

### F3. Confidence and Verification

Confidence must be explicit, numeric, and textual.

Required behavior:

1. Confidence is never color-only.
2. Low-confidence critical actions must show mandatory verification guidance.
3. Confidence caveats must be visible in summary cards, not only deep detail.
4. Confidence must influence ranking and appear in JSON factors.

### F4. Correlation and Conflict Narrative

Correlation output must reduce noise and explain linkage.

Each correlation entry must include:

1. Source finding references.
2. Link rationale.
3. Net confidence.
4. Impacted domains or subsystems.
5. Conflict flag when severity or confidence disagreement is material.

### F5. Investigation Workflow and Handoff

HTML navigation must support fast analyst flow:

1. Top action to finding.
2. Finding to evidence.
3. Evidence to provenance.

Maximum interaction target: 2 interactions from top action to provenance.

Handoff block must include:

1. Incident summary.
2. Ranked top actions.
3. Risks if unaddressed.
4. Evidence references.
5. Known limitations.

Markdown must preserve this order deterministically.

### F6. Ticket Payload Templates

Each top action must provide copy-ready ticket payload templates.

Required templates:

1. Azure DevOps work item body.
2. Jira issue body.
3. GitHub issue body.

Template payload must include:

1. Incident summary.
2. Ranked action statement.
3. Why-now rationale.
4. Validation step.
5. Evidence references and known limitations.

---

## Reliability Contract (New Mandatory)

### R1. Anchor and ID Integrity

1. All in-page links must resolve to existing targets.
2. All IDs must be unique in the rendered document.
3. Legacy anchor compatibility remains supported.
4. Broken anchor count must be zero.

### R2. Data Consistency

1. Summary counters must match detailed sections.
2. Top action counts must be consistent across all surfaces.
3. Number formatting must be locale-consistent within one report.

---

## Layout and Visual Contract

### V1. Responsive Shell

Viewport behavior:

1. 1280 px and above: left rail, main column, right rail.
2. 900-1279 px: left rail plus main, right rail collapsible.
3. Below 900 px: single column with sticky compact incident bar.

Horizontal overflow must not occur at page level.

### V2. Table Behavior

1. No uncontrolled table overflow on mobile.
2. Dense tables must switch to card or stacked row mode below 900 px.
3. Long cells must clamp with expand affordance.
4. Large evidence tables should be lazy-rendered when collapsed.

### V3. Typographic Readability

1. Minimum body size target: readable mobile baseline.
2. Reduce micro-label density and repeated badges.
3. Keep prose line length approximately 70-95 characters on desktop.
4. Prioritize title, evidence, action hierarchy over decorative chips.

### V4. Severity Semantics

Severity encoding requires both color and shape or icon:

1. Critical: filled badge with strong border and icon.
2. Warning: outlined badge with icon.
3. Info: neutral badge.
4. Unknown: muted badge.

Confidence symbols remain inline near finding title.

### V5. Design Tokens (Required Names)

Renderer must expose stable token names for:

1. Canvas, surface, elevated backgrounds.
2. Primary, secondary, muted text.
3. Severity and border colors.
4. Domain accents.
5. UI and mono typography scales.
6. Spacing, radius, and shadow levels.

Token names remain stable even if values change.

---

## Single-Dump Mode Contract (New Mandatory)

When report contains one dump:

1. Hide trend-only scaffolding and timeline-only language.
2. Reallocate space to single-dump root evidence.
3. Prioritize retained roots, hotspot types, suspicious threads, and handle pressure.
4. Preserve trend-capable schema but suppress trend-first visuals.

---

## Performance Contract (New Mandatory)

Set report-render performance budgets and enforce them in tests.

Minimum requirements:

1. Controlled DOM growth by default-collapsing heavy sections.
2. Lazy hydration or deferred render for deep tables and heavy charts.
3. Avoid unnecessary duplicated markup for repeated finding cards.
4. Keep interaction responsiveness stable during expand and scroll.

---

## Accessibility Contract

1. WCAG AA contrast for text and badges.
2. Keyboard navigation for toggles, links, and tables.
3. ARIA labels for severity, confidence, and collapsibles.
4. Screen-reader summary at top with critical count and top actions.
5. No critical workflow behind pointer-only interactions.

---

## Required HTML IDs and Anchors

Required IDs:

1. report-header
2. health-scorecard
3. executive-summary
4. top-actions
5. domain-{letter}
6. section-{id}
7. finding-{id}
8. provenance-{id}
9. reading-mode-toggle
10. ticket-template-menu

Mandatory legacy anchors:

1. Section IDs such as A1, B4, and others from v1 contracts.

---

## JSON and Markdown Parity

1. JSON remains full-fidelity and preserves deterministic ordering.
2. Markdown preserves the same incident story and ranked order.
3. Visual-only metadata must not alter semantic payload.
4. Ranking and confidence factors remain present in JSON for auditability.

---

## Quality Gates

A v2 renderer passes only if all checks below are true:

1. Actionable triage is visible before any large table.
2. Critical issue is discoverable within 3 seconds by scan.
3. Action to provenance path is no more than 2 interactions.
4. Broken anchor count is zero.
5. Duplicate ID count is zero.
6. Mobile has no page-level horizontal overflow.
7. Summary counters are internally consistent.
8. Top action ordering is deterministic and explainable.
9. Confidence caveats are visible in summary.
10. HTML, JSON, markdown remain semantically aligned.
11. Incident and Forensics mode switch works without data loss.
12. Ticket payload templates are available from each top action.

---

## Backward Compatibility

1. v1 fields remain valid.
2. v2 remains opt-in via report style version.
3. If optional functional fields are unavailable, renderer must degrade gracefully and emit explicit caveat text.
