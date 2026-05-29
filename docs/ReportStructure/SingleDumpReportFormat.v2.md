# Single-Dump Report Format v2

## Purpose

Define the v2 contract for a professional single-dump report that is:

1. Actionable in incident triage.
2. Trustworthy in technical evidence.
3. Readable across executive and engineer audiences.
4. Consistent across HTML, JSON, and markdown outputs.

This document extends the structural contract in [SingleDumpReportFormat.md](SingleDumpReportFormat.md).

v1 remains authoritative for baseline field availability. v2 introduces both presentation and functional behavior expectations.

---

## Design Intent (v2)

1. Answer in 90 seconds: what is wrong, how severe, what should happen next.
2. Make risk explicit and ranked, not just listed.
3. Preserve deep technical detail without overwhelming first-screen triage.
4. Present confidence and caveats clearly so decisions are calibrated.
5. Keep narrative and conclusions consistent across HTML, JSON, markdown.

---

## Functional Contract

### F1. Action Prioritization Engine

Top actions must be generated from findings using explicit ranking dimensions:

1. Severity weight.
2. Estimated blast radius.
3. User-facing impact likelihood.
4. Time-to-mitigate estimate.
5. Confidence score.
6. Dependency risk (cross-domain coupling).

Each action card must include:

1. One-line action statement.
2. Why now (risk rationale).
3. Supporting evidence pointer(s).
4. Suggested owner role (for example, runtime, memory, service team).
5. Expected validation step.

Ranking output requirements:

1. Top actions list must be stable for identical input.
2. Ties must be resolved deterministically (fingerprint then analyzer name).
3. JSON must expose ranking factors for auditability.

### F2. Confidence and Trust Model

Every lead finding must carry a confidence band and optional caveats.

Confidence signals should combine:

1. Evidence completeness.
2. Signal consistency across analyzers.
3. Heuristic versus deterministic detection path.
4. Data freshness and coverage (for trend-aware sections, if present).

Rules:

1. Confidence must never be implied only by color.
2. Low-confidence critical findings must include an explicit verification recommendation.
3. Confidence downgrades must propagate to action priority.

### F3. Cross-Analyzer Correlation

The report must synthesize relationships between domains.

Required correlation outcomes:

1. Duplicate finding suppression via fingerprint clustering.
2. Correlated incident hypotheses (for example, thread pool starvation plus retention growth).
3. Conflict flags when analyzers disagree materially.

Each correlation entry must include:

1. Source findings.
2. Why they are linked.
3. Net confidence.
4. Impacted subsystem/domain labels.

### F4. Noise Suppression and Focus

The report must minimize cognitive noise while preserving drill-down access.

Rules:

1. Hide empty sections and empty tables.
2. Collapse low-value detail blocks by default.
3. De-duplicate repeated recommendations.
4. Preserve raw evidence in JSON even when HTML is summarized.

### F5. Investigation Workflow

The report must support a practical analyst workflow:

1. Triage: first-screen health, critical findings, top actions.
2. Validate: open section evidence and provenance.
3. Decide: view confidence, caveats, blast radius.
4. Handoff: copy/share concise action-ready summary.

HTML must provide fast jump paths between:

1. Top action -> supporting finding.
2. Finding -> section evidence.
3. Finding -> provenance.

### F6. Incident Handoff Output

The report must include a concise handoff block suitable for tickets/channels:

1. Incident summary (2-4 lines).
2. Top actions (ranked).
3. Risks if unaddressed.
4. Evidence references.
5. Known limitations.

Markdown must preserve this handoff narrative in deterministic order.

### F7. Determinism and Reproducibility

For identical input and configuration:

1. Ordering must be deterministic.
2. Scores must be deterministic.
3. Component IDs and anchors must be deterministic.

All ranking/scoring formulas used in rendering must be versioned.

---

## Visual Language Contract

### V2.1 Design Tokens (Required)

All HTML renderers must expose CSS variables (or equivalent token map):

- Color tokens:
  - `--bg-canvas`, `--bg-surface`, `--bg-elevated`
  - `--text-primary`, `--text-secondary`, `--text-muted`
  - `--severity-critical`, `--severity-warning`, `--severity-info`, `--severity-unknown`
  - `--border-subtle`, `--border-strong`
  - `--accent-memory`, `--accent-gc`, `--accent-threads`, `--accent-async`, `--accent-runtime`
- Typography tokens:
  - `--font-ui`: report UI font family
  - `--font-mono`: evidence/table raw values
  - `--fs-12`, `--fs-14`, `--fs-16`, `--fs-20`, `--fs-28`
  - `--lh-tight`, `--lh-normal`, `--lh-relaxed`
- Spacing/radius/shadow tokens:
  - `--space-4`, `--space-8`, `--space-12`, `--space-16`, `--space-24`, `--space-32`
  - `--radius-sm`, `--radius-md`, `--radius-lg`
  - `--shadow-1`, `--shadow-2`

Renderer may choose exact values but token names must be stable.

### V2.2 Severity Semantics

Severity must be encoded with both color and shape or icon (never color alone):

- Critical: filled badge plus high-contrast border plus icon
- Warning: outlined badge plus icon
- Info: neutral badge
- Unknown: muted badge

Confidence symbols remain inline (`●●●●`, `●●●○`, `●●○○`, `●○○○`) and must appear near the finding title.

### V2.3 Typography Hierarchy

- H1: report title and incident identity
- H2: domain headers
- H3: section headers
- Body: finding evidence and recommendation
- Mono: addresses, metric keys, stack frames, IDs

Line length target for prose: 70-95 characters.

---

## Layout Contract

### V2.4 Shell Layout (HTML)

Use a three-zone layout for large screens:

1. Left rail: section navigation, severity chips, progress.
2. Main column: findings and evidence.
3. Right rail: sticky top actions and quick metrics.

Responsive behavior:

- >= 1280 px: three-zone layout.
- 900-1279 px: left rail plus main, right rail collapsible drawer.
- < 900 px: single-column stack, sticky summary bar replaces side rails.

### V2.5 First-Screen Composition

Above the fold must include, in order:

1. Header
2. HealthScorecard
3. ExecutiveSummary critical/warning slices
4. Top actions panel
5. Key metrics strip

No large tables above the fold.

---

## Section Anatomy (Visual + Data)

Each `ReportSection` uses a consistent card anatomy:

1. Section header (title, severity, confidence, provenance status)
2. Lead finding block (always visible)
3. Key metrics strip (always visible)
4. Evidence tables (collapsed by default)
5. Caveats
6. Provenance (collapsed)

Lead finding visual template:

```
[Severity Badge] Section title                       Confidence ●●●○
Finding: one-sentence actionable statement
Evidence: metric-grounded sentence
Recommendation: one concrete next action
Caveats: heuristic or coverage notes
```

---

## Information Density Rules

1. Default to summary view; details on demand.
2. Avoid duplicate metrics across adjacent blocks.
3. Hard-limit summary lists (`top N`) in HTML and markdown; JSON remains full.
4. Prefer ranked tables over long prose.
5. Hide empty tables and empty sub-panels.

---

## Data Visualization Rules

Use compact, decision-oriented visuals:

- Memory composition: stacked bars with absolute bytes and percent labels.
- Severity movement in-domain: mini trend chips if historical context exists.
- Distribution metrics: histogram and sparkline pairs.
- Thread and wait profiles: horizontal bars and heat strips.

Do not render decorative charts without action value.

---

## Motion and Interaction (HTML)

Motion is functional, minimal, optional:

- On-load stagger for summary cards (100-200 ms step).
- Expand and collapse transitions <= 180 ms.
- Scroll-to-anchor highlight pulse <= 1 cycle.

Respect `prefers-reduced-motion`: disable non-essential animation.

---

## Accessibility Contract

1. WCAG AA contrast minimum for all text and badges.
2. Keyboard navigation for all toggles, tables, and anchors.
3. ARIA labels for severity badges, confidence symbols, and collapsible controls.
4. Screen-reader summary at top with critical counts and top actions.
5. No critical workflow blocked behind pointer-only interaction.

---

## Domain and Section Ordering (inherits v1)

Ordering rules from v1 remain unchanged:

1. `HealthScorecard` first.
2. Domains sorted by max severity.
3. Sections in domain sorted by lead severity.
4. Unknown or skipped domains last.

v2 adds emphasis and decision support only; no schema-order divergence.

---

## HTML-Specific Component Catalog (Required IDs)

Renderer must provide stable component classes and IDs for testability:

- `report-header`
- `health-scorecard`
- `executive-summary`
- `top-actions`
- `domain-{letter}`
- `section-{id}` (for example `section-A1`)
- `finding-{id}`
- `provenance-{id}`

Section anchor IDs from v1 (`A1`, `B4`, and others) remain mandatory.

---

## JSON and Markdown Parity

- JSON preserves full data and ordering.
- Markdown preserves narrative order with concise top-N tables.
- Visual-only metadata (color, motion) must not alter semantic payload.
- Functional scoring fields (priority factors, confidence factors) must be present in JSON for auditability.

---

## Print / Export Mode

When print or export mode is enabled:

1. Expand only lead findings and key metrics by default.
2. Keep long tables truncated with continuation note.
3. Repeat section header metadata on page breaks.
4. Include generation timestamp, analyzer version, and known limitations footer.

---

## Quality Gates (v2)

A v2 renderer is acceptable only if:

1. First-screen shows actionable triage without scrolling into tables.
2. Critical finding is identifiable within 3 seconds by visual scan.
3. Any finding can be traced to evidence and provenance in <= 2 interactions.
4. Mobile layout remains readable and fully navigable.
5. Markdown and JSON still tell the same incident story.
6. Top action ordering is deterministic and explainable.
7. Confidence caveats are visible before deep drill-down.

---

## Test and Validation Matrix

Minimum validation expectations:

1. Snapshot tests for first-screen ordering and required component IDs.
2. CSS token presence checks for required token names.
3. Accessibility smoke tests for keyboard flow and ARIA labels.
4. JSON parity tests for v1 versus v2 payload semantics.
5. Ranking determinism tests for top actions and tie-breaks.
6. Correlation tests for duplicate suppression and conflict flags.

---

## Backward Compatibility

- v1 field map remains valid.
- v2 introduces presentation and decision-support behavior.
- Existing pipelines may opt in via `ReportStyleVersion = "v2"`.
- If any functional field is unavailable, renderer must degrade gracefully and emit explicit caveat text.
