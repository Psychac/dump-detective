# Trend Report Format v2

## Purpose

Define the v2 composition, visual system, and rendering rules for trend-mode reporting.
This document extends [TrendReportFormat.md](TrendReportFormat.md) with a stronger visual and narrative contract focused on change detection, risk velocity, and actionability.

v1 remains the baseline structural map. v2 upgrades presentation and comparative readability.

---

## Core Principle (v2)

The report must answer:

1. What changed?
2. How fast is risk moving?
3. Which changes are noise vs meaningful regressions?
4. What should be acted on now?

Per-dump detail stays secondary and collapsible.

---

## V2 Narrative Order (Required)

1. T0 Trend Header
2. T0b Change Story (new)
3. T1 Trend Health Scorecard with velocity
4. T2 Trend Executive Summary
5. T3 Regression Dashboard
6. T3b Correlation Timeline (new)
7. T4 Per-analyzer timelines
8. T5 Snapshot strip
9. T6 Snapshot details (collapsed)
10. T7 Appendix

---

## New v2 Blocks

### T0b. Change Story (new)

A compact narrative card immediately after header:

- First major regression timestamp
- Largest inflection snapshot index
- Top worsening domains (max 3)
- Likely cross-domain coupling signals (max 3)

This is plain-language text with metric references.

### T1+. Risk Velocity Overlay (new)

Extend each domain row in the scorecard with:

- `VelocityScore` (slope of normalized severity-relevant metrics)
- `VolatilityScore` (oscillation/noise indicator)
- `ConfidenceTrend` (up/down/stable)

Direction chips:

- `↑ accelerating risk`
- `→ stable risk`
- `↓ recovering`

### T3b. Correlation Timeline (new)

A compact timeline lane showing snapshot points where multi-domain signals co-move.

Each event includes:

- Snapshot index/time
- Domains involved
- Signal keys
- Correlation strength (Low/Med/High)

---

## Visual Language Contract (Trend)

Trend mode must share the token system from single-dump v2 and add temporal tokens:

- `--trend-regressed`, `--trend-improved`, `--trend-stable`, `--trend-volatile`
- `--timeline-baseline`, `--timeline-current`, `--timeline-intermediate`

Time direction is always left-to-right baseline -> current.

No ambiguous directional color use; pair color with arrows/icons.

---

## Trend Layout Contract

### L1. Above-the-fold (HTML)

Must include, without scrolling on desktop:

1. Header identity (baseline/current/window)
2. Change Story
3. Health scorecard with velocity
4. Top regressions (headline list)

Do not place per-snapshot deep tables above the fold.

### L2. Timeline Readability

For T4 tables:

- Keep explicit dump-wise columns (`Dump 1..N`) as required by v1.
- Add compact step direction markers in each adjacent transition.
- Render numeric value and direction in same cell.

### L3. Snapshot details

T6 remains collapsed by default and visually separated from comparison blocks.
Use clear divider label: `Current state deep dive`.

---

## Regression Classification Contract (v2)

Each regression finding must be tagged as one of:

- `NewRisk`: absent in baseline, present now.
- `AmplifiedRisk`: present in baseline, significantly worse now.
- `VolatileRisk`: oscillating with high volatility and uncertain direction.

All trend dashboards should support filtering by this class.

---

## Significance and Noise Suppression

Trend renderer must suppress non-meaningful delta rows by default.

Minimum significance criteria (configurable, defaults):

1. Relative delta >= 10% OR
2. Absolute delta crosses severity threshold OR
3. Persistent worsening over >= 2 consecutive steps

Suppressed rows are counted and shown as "N low-significance changes hidden".

---

## Confidence-in-Change Rules

Trend confidence is separate from single-snapshot confidence.

Each trend finding shows:

- Evidence confidence band (from source finding)
- Change confidence band (based on sample depth, consistency, metric stability)

Display format:

```
Evidence ●●●○   Change ●●○○
```

---

## Component System (HTML)

Required component IDs/classes:

- `trend-header`
- `trend-change-story`
- `trend-scorecard`
- `trend-regressions`
- `trend-correlation-timeline`
- `trend-metric-timelines`
- `trend-snapshot-strip`
- `trend-snapshot-detail-{index}`
- `trend-appendix`

Anchors from v1 (`T0..T7`, `detail-{index}`) remain mandatory.

---

## Motion and Interaction

Use motion only to support temporal comprehension:

- Timeline reveal: left-to-right staged reveal.
- Row expansion: <= 180 ms.
- Snapshot jump highlight: brief pulse.

Respect `prefers-reduced-motion` and disable staged motion when set.

---

## Accessibility Contract

1. Trend direction communicated via text/icon and color.
2. Keyboard access for timeline focus points and snapshot links.
3. Screen-reader summary includes baseline vs current risk movement.
4. All condensed trend visuals provide text fallback in markdown and ARIA descriptions in HTML.

---

## JSON / Markdown Parity

- JSON includes full trend metadata and unsuppressed raw lists.
- Markdown keeps same narrative order and explicitly labels suppressed low-significance rows.
- Trend-only visual affordances (sparklines/chips/animations) cannot hide semantic values.

---

## Print / Export Mode

In print mode:

1. Expand T0b Change Story and T2 Executive Summary.
2. Keep T4 timeline to top significant metrics only.
3. Keep T6 snapshot deep sections collapsed summary unless explicitly expanded before export.
4. Include limitation footer with compare-mode caveats.

---

## Quality Gates (v2)

A trend v2 renderer is acceptable only if:

1. Reader can identify largest regression and first inflection in first screen.
2. Regressions are clearly separated from noise.
3. Cross-domain coupling events are discoverable without opening snapshot detail.
4. Current state deep dive remains reachable in <= 2 clicks from snapshot strip.
5. JSON/markdown preserve the same regression narrative.

---

## Backward Compatibility

- v1 trend schema remains valid.
- v2 is a presentation and comparison-readability upgrade.
- Enable with `ReportStyleVersion = "v2"` while keeping `IsTrendReport = true` behavior unchanged.

---

## Functional Extension Addendum (v2+)

This addendum extends trend v2 from visual-comparative reporting into decision-support behavior while preserving all existing sections above.

### FT1. Prioritized Trend Actions

Trend top actions must be ranked using explicit factors:

1. Current severity.
2. Velocity of worsening.
3. Persistence across snapshots.
4. Blast radius (domains/services impacted).
5. Confidence in change.
6. Mitigation urgency.

Each action entry must include:

1. What to do now.
2. Why now (rank rationale).
3. Supporting trend evidence references.
4. Suggested owner role.
5. Validation check after mitigation.

### FT2. Trend Confidence Model

Trend confidence must be represented as two dimensions:

1. Evidence confidence (single-snapshot quality).
2. Change confidence (time-series reliability).

Change confidence should consider:

1. Snapshot count.
2. Metric stability vs oscillation.
3. Missing-data gaps.
4. Consistency across analyzers/domains.

Rules:

1. High-severity, low-change-confidence regressions must include explicit verification guidance.
2. Confidence caveats must be visible before deep snapshot drill-down.

### FT3. Correlation and Causality Hints

Correlation timeline events should emit a machine-readable payload that includes:

1. Event type (`co-move`, `lead-lag`, `conflict`).
2. Domain set involved.
3. Signal keys.
4. Confidence level.
5. Optional causal hypothesis text.

Correlation is suggestive, not definitive; UI must label this clearly.

### FT4. Noise Governance and Explainability

For any suppressed trend row, renderer must expose explainability metadata:

1. Suppression reason (`below-relative-threshold`, `below-absolute-threshold`, `non-persistent`).
2. Threshold values used.
3. Whether row is recoverable in expanded mode.

Suppression must never remove critical-severity transitions.

### FT5. Determinism and Versioned Scoring

Trend scoring and ordering must be deterministic for identical inputs/config.

Required metadata:

1. `TrendScoringModelVersion`.
2. `TrendThresholdProfile` (name or hash).
3. Stable tie-break strategy declaration.

### FT6. Investigation Workflow Contract

Trend workflow must support:

1. Detect: identify the main regression in first screen.
2. Compare: inspect timeline evidence and significance.
3. Attribute: inspect cross-domain coupling hints.
4. Decide: choose mitigations with confidence context.
5. Handoff: export concise incident story.

Expected navigation affordances:

1. Top regression -> timeline row -> snapshot detail.
2. Correlation event -> involved domains and findings.
3. Action item -> supporting evidence anchors.

### FT7. Incident Handoff Block (Trend)

Trend report should include a concise handoff section containing:

1. Baseline-to-current incident summary.
2. Top regressions and acceleration indicators.
3. Suggested actions with urgency.
4. Caveats and data limitations.
5. Evidence anchor references.

Markdown must preserve this narrative in deterministic order.

### FT8. Parity and Audit Requirements

In addition to existing JSON/markdown parity rules:

1. JSON must include ranking factors and suppression reasons.
2. JSON must include correlation event payload.
3. Markdown must carry suppression and caveat summaries (even without charts).

### FT9. Extended Quality Gates

Trend v2+ is acceptable only if:

1. Top 3 actions are deterministic and explainable.
2. Suppressed rows are auditable with reasons.
3. Correlation timeline events include confidence and rationale.
4. High-severity low-confidence items visibly require verification.
5. Handoff block enables incident transfer without opening deep details.
