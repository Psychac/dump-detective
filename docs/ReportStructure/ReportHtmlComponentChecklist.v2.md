# Report HTML Component Checklist v2

## Purpose

Provide a concrete implementation checklist for HTML renderers in style v2.
This maps semantic sections to DOM IDs/classes, expected behaviors, and test assertions.

Use with:

- [SingleDumpReportFormat.v2.md](SingleDumpReportFormat.v2.md)
- [TrendReportFormat.v2.md](TrendReportFormat.v2.md)
- [SingleDumpReportImplementationPlan.v2.md](SingleDumpReportImplementationPlan.v2.md)
- [TrendReportImplementationPlan.v2.md](TrendReportImplementationPlan.v2.md)

---

## Global Requirements

1. Every major section must have a stable `id` and a stable top-level class.
2. Severity must be encoded by class and ARIA label.
3. Confidence bands must have machine-readable value (`data-confidence-score`).
4. Collapsible blocks must expose expanded state (`aria-expanded`).
5. All anchor links must resolve to existing IDs.

---

## CSS Token Checklist

Required root variables:

- Colors: `--bg-canvas`, `--bg-surface`, `--bg-elevated`, `--text-primary`, `--text-secondary`, `--text-muted`, `--severity-critical`, `--severity-warning`, `--severity-info`, `--severity-unknown`
- Domain accents: `--accent-memory`, `--accent-gc`, `--accent-threads`, `--accent-async`, `--accent-runtime`
- Trend extras: `--trend-regressed`, `--trend-improved`, `--trend-stable`, `--trend-volatile`
- Typography: `--font-ui`, `--font-mono`, `--fs-12`, `--fs-14`, `--fs-16`, `--fs-20`, `--fs-28`
- Spacing/elevation: `--space-4`, `--space-8`, `--space-12`, `--space-16`, `--space-24`, `--space-32`, `--radius-sm`, `--radius-md`, `--radius-lg`, `--shadow-1`, `--shadow-2`

Test assertions:

- Root style contains all required token names.
- No hard-coded severity hex values in component CSS blocks.

---

## Single-Dump Component Mapping

| Semantic section | DOM id | Required classes | Key data attrs | Required test assertions |
|---|---|---|---|---|
| Header | `report-header` | `report-header`, `report-meta` | `data-style-version` | Header renders dump path, timestamp, runtime version |
| Health scorecard | `health-scorecard` | `scorecard`, `scorecard-domain-row` | `data-domain`, `data-severity` | First major content block appears before executive summary |
| Executive summary | `executive-summary` | `summary-card`, `summary-grid` | `data-critical-count`, `data-warning-count` | Critical/warning lists render top-N only |
| Top actions | `top-actions` | `action-queue`, `action-item` | `data-rank` | Top 3 actions visible without expanding tables |
| Domain container | `domain-{letter}` | `domain-section`, `domain-accent-*` | `data-domain-order` | Domains ordered by max severity descending |
| Report section card | `section-{id}` | `section-card`, `severity-*` | `data-section-id`, `data-lead-severity` | Section anchor IDs are stable (`A1`, `B4`, etc.) |
| Lead finding | `finding-{id}` | `lead-finding`, `confidence-band` | `data-confidence-score` | Lead finding visible by default |
| Key metrics strip | `metrics-{id}` | `key-metrics-strip`, `metric-chip` | `data-metric-key` | Metrics visible when table is collapsed |
| Table block | `table-{id}-{n}` | `evidence-table`, `collapsible` | `data-row-count`, `data-collapsed-default` | Table collapsed by default in HTML |
| Provenance | `provenance-{id}` | `provenance`, `collapsible` | `data-analyzer`, `data-status` | Contains analyzer, status, duration, diagnostics fields |
| Appendix | `appendix` | `appendix-section` | `data-has-memory-diagnostics` | Memory diagnostics block shown only when enabled |

---

## Trend Component Mapping

| Semantic section | DOM id | Required classes | Key data attrs | Required test assertions |
|---|---|---|---|---|
| Trend header (T0) | `trend-header` | `trend-header`, `trend-meta` | `data-snapshot-count` | Shows baseline/current identity and snapshot window |
| Change story (T0b) | `trend-change-story` | `change-story`, `narrative-card` | `data-inflection-index` | Appears immediately after trend header |
| Trend scorecard (T1) | `trend-scorecard` | `scorecard`, `trend-scorecard-row` | `data-baseline-severity`, `data-current-severity`, `data-change` | Includes change direction and velocity fields |
| Trend executive summary (T2) | `trend-executive-summary` | `summary-card`, `delta-grid` | `data-new-findings`, `data-resolved-findings` | Lifecycle and score delta blocks rendered |
| Regression dashboard (T3) | `trend-regressions` | `regression-dashboard`, `regression-row` | `data-regression-class`, `data-significance` | Rows filterable by class (`NewRisk`, `AmplifiedRisk`, `VolatileRisk`) |
| Correlation timeline (T3b) | `trend-correlation-timeline` | `timeline-lane`, `timeline-event` | `data-correlation-strength`, `data-snapshot-index` | Events render in chronological order |
| Metric timelines (T4) | `trend-metric-timelines` | `timeline-table`, `dump-column` | `data-dump-index`, `data-delta-pct`, `data-direction` | Keeps explicit `Dump 1..N` columns |
| Snapshot strip (T5) | `trend-snapshot-strip` | `snapshot-strip`, `snapshot-row` | `data-role`, `data-anchor` | Snapshot links resolve to `detail-{index}` IDs |
| Snapshot detail (T6) | `trend-snapshot-detail-{index}` | `snapshot-detail`, `collapsible` | `data-snapshot-index`, `data-default-collapsed` | Collapsed by default |
| Trend appendix (T7) | `trend-appendix` | `appendix-section`, `coverage-map` | `data-resolved-count` | Coverage map and resolved findings are present |

---

## Accessibility Checklist

Required:

1. Severity badges include readable labels (`aria-label="Severity: Critical"`).
2. Confidence indicators include readable labels (`aria-label="Confidence: Medium"`).
3. All collapsible toggles keyboard-operable and expose `aria-expanded`.
4. Skip links exist: Summary, Domains, Appendix.
5. Focus-visible style present for links, buttons, toggles.
6. Direction in trend views is not color-only (must include text/icon).

Test assertions:

- Axe/Lighthouse checks show no critical accessibility violations.
- Keyboard-only traversal can open every collapsible block.

---

## Motion and Reduced Motion Checklist

Required:

1. Expand/collapse transition <= 180 ms.
2. On-load stagger only for summary cards.
3. Anchor jump highlight no more than one pulse cycle.
4. `prefers-reduced-motion: reduce` disables stagger and pulses.

Test assertions:

- Under reduced motion, no animation classes are applied.

---

## Responsive Checklist

Breakpoints:

- Desktop: three-zone layout visible.
- Tablet: right rail converted to drawer.
- Mobile: single-column with sticky summary.

Test assertions:

- No horizontal overflow at 360 px width.
- Section anchors and collapse toggles remain reachable at all sizes.

---

## Integration Test Matrix

Minimum automated suite:

1. `singledump_v2_structure.spec`: verifies required IDs and ordering.
2. `singledump_v2_accessibility.spec`: verifies ARIA and keyboard paths.
3. `singledump_v2_responsive.spec`: desktop/tablet/mobile snapshots.
4. `trend_v2_structure.spec`: verifies `T0`, `T0b`, `T1`, `T2`, `T3`, `T3b`, `T4`, `T5`, `T6`, `T7` ordering.
5. `trend_v2_regression_filters.spec`: verifies class filtering and significance suppression.
6. `trend_v2_anchor_navigation.spec`: verifies `snapshot-strip -> detail-{index}` links.
7. `style_token_completeness.spec`: verifies required CSS tokens exist.
8. `semantic_parity.spec`: verifies JSON payload unchanged between v1 and v2 style mode.

---

## Release Gate

A renderer can claim v2 compliance only if all required IDs/classes are present and all integration tests above pass.