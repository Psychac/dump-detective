# Report Display Vision — Implementation Tracker

See also: `docs/improvements/report-design-system.md` for the cross-mode design-system baseline (Single, Trend, Trace, Combined).

**Scope:** Single-dump and trend HTML reports.  
**Goal:** Answer the primary diagnostic question in the first viewport; visual hierarchy earns attention in order of urgency.

**Current architecture note (source of truth):** runtime HTML is rendered from embedded template modules under `src/DumpDetective.Reporting/Templates/` via `HtmlReportRenderer`; references below use template module paths (not `wwwroot`).

Status legend: `[ ]` not started · `[~]` in progress · `[x]` done

---

## Foundational Principles

- Report must answer its key question above the fold — no scroll required.
- Visual hierarchy ordered by urgency (Critical → Warning → OK).
- Trend = comparison-first. Single dump = triage-first.
- Dark mode: all chart SVG colors must reference CSS vars — no hardcoded hex in chart builders.

---

## Phase 1 — High Visual Impact, Low Risk (No schema changes)

### 1.1 KPI Dashboard Panel
Replaces the current key-metrics chip strip with a proper scannable panel.

- [x] Design 4-tile grid layout (`src/DumpDetective.Reporting/Templates/report.header.css` or companion module CSS under `Templates/`)
- [x] Each tile: label, formatted value, magnitude context (GB/MB/%), status indicator (neutral/warning/critical) based on thresholds
- [x] Threshold definitions for each KPI (heap %, LOH %, finalizer queue count, Gen2 %, blocked threads, deadlock cycles)
- [x] Wire tile status colors to existing severity CSS vars (`--sev-critical-*`, `--sev-warning-*`)
- [x] Replace `key-metrics` chip strip rendering in `src/DumpDetective.Reporting/Templates/report.renderers.header.js`

### 1.2 Triage Card Deck (Executive Summary)
Replaces the flat finding-list in executive summary with visually distinct cards.

- [x] Card layout: left severity color strip, title, one-line evidence metric, recommendation, confidence meter bar
- [x] 2-column grid on wide screens (`>= 900px`), 1-column on narrow
- [x] Confidence display: 4-segment filled/unfilled bar (replacing `●●●○` text)
- [x] "Top 3 Actions" numbered strip below card deck, linked to relevant section anchors
- [x] Update `src/DumpDetective.Reporting/Templates/report.renderers.findings.js` to render new card shape
- [x] Update `src/DumpDetective.Reporting/Templates/report.renderers.header.js` executive summary block

### 1.3 Domain Section Severity Bands
Make severity immediately scannable across domains without reading text.

- [x] Full-width color band on domain header matching max severity within that domain
- [x] Critical and Warning domains start expanded by default; OK domains start collapsed
- [x] Small inline severity histogram (5-bar SVG) in domain header showing finding distribution
- [x] Update `src/DumpDetective.Reporting/Templates/report.renderers.sections.js` or `src/DumpDetective.Reporting/Templates/report.renderers.panels.js` for domain header rendering
- [x] Add supporting CSS classes to `src/DumpDetective.Reporting/Templates/report.body.css` or `src/DumpDetective.Reporting/Templates/report.detail.css`

### 1.4 Table: Search, Sort, Lazy-expand
Client-side enhancements only — no C# changes.

- [x] Search/filter input above table rows when a `<details>` table is expanded
- [x] Column sort on `<th>` click (ascending/descending toggle)
- [x] Row limit toggle: "Show all N rows" replaces the footer truncation note
- [x] For the top-types table: inline treemap panel (~120px tall) above table rows using existing `buildChartSvg('treemap', …)`
- [x] Implement in `src/DumpDetective.Reporting/Templates/report.ui.js` (or a new template module file if split)

---

## Phase 2 — Navigation & Single-Dump Command View

### 2.1 Side Navigation Improvements

- [x] Severity dot (color-coded) next to each nav item in the TOC
- [x] Sticky active-section highlighting as user scrolls (`IntersectionObserver`)
- [x] Collapse/expand nav groups by domain
- [ ] "Jump to Critical" shortcut link pinned at top of nav
- [x] Keyboard shortcut exists: `Shift+N` jumps to next critical signal (implemented in `src/DumpDetective.Reporting/Templates/report.ui.js`)
- [x] Update `src/DumpDetective.Reporting/Templates/report.renderers.nav.js` and nav CSS in `src/DumpDetective.Reporting/Templates/report.header.css`

### 2.2 Sticky Critical Findings Bar
Persistent top-of-page banner while scrolling.

- [ ] `position: sticky; top: 0` overlay bar showing "N Critical findings require immediate attention"
- [ ] Visible only when Critical findings exist; hides if none
- [ ] Dismissible (session-local, no server state)
- [ ] CSS in `src/DumpDetective.Reporting/Templates/report.header.css`; JS in `src/DumpDetective.Reporting/Templates/report.ui.js`

### 2.3 Domain Tile Click-to-Scroll
Health scorecard domain tiles link to their section.

- [x] Add anchor IDs to each domain section matching scorecard tile IDs
- [x] Scorecard tile `<button>` or `<a>` scrolls to domain section
- [x] Update `src/DumpDetective.Reporting/Templates/report.renderers.header.js` scorecard renderer

### 2.4 Empty Domain States
Replace silent omission with explicit positive-state cards.

- [x] "No findings in this domain — system appears healthy" card for domains with only Info/OK findings
- [x] Distinct visual style (green tint, check icon) — not just blank
- [x] Add to `src/DumpDetective.Reporting/Templates/report.renderers.sections.js`

### 2.5 Heuristic / Approximate Inline Warnings
Surface provenance caveats earlier than the collapsed provenance footer.

- [x] If `lead.caveats` contains "heuristic" or "approximate", render a caution banner inline above the finding, not only in the provenance footer
- [x] Style as amber banner, collapsible
- [x] Update `src/DumpDetective.Reporting/Templates/report.renderers.sections.js` lead-finding renderer

---

## Phase 3 — Trend Core Visualizations

### 3.1 Regression Banner (Above-the-Fold for Trend)
Replaces the separate trend-header + scorecard for trend mode.

- [ ] Split layout: left = identity (dump count, date range), right = lifecycle pill strip (new/persistent/resolved)
- [ ] Domain delta row: per-domain score change with up/down arrow and magnitude label
- [ ] Guard on `isTrendReport` flag in `src/DumpDetective.Reporting/Templates/report.renderers.header.js`

### 3.2 Multi-Series Timeline Chart
Primary trend visualization — full-width SVG line chart.

- [ ] New SVG builder `buildTimelineChart(payload)` in `src/DumpDetective.Reporting/Templates/report.renderers.charts.js`
- [ ] X-axis: snapshot dates (formatted, evenly spaced); Y-axis: metric value with unit label
- [ ] Series: heap size, Gen2 %, leak candidate count, blocked threads — togglable via legend
- [ ] Baseline snapshot annotated with `▲ BASE` label; current with `▲ NOW`
- [ ] Regression bands: amber/red fill behind line for periods where value exceeded threshold
- [ ] Click on snapshot point → scroll to that snapshot's detail section
- [ ] Integrate as a new chart kind `'timeline'` in `buildChartSvg`
- [ ] Wire via a new `ChartBlock` of kind `timeline` emitted by trend section builders (C# side)

### 3.3 Regression Delta Cards
Replace flat regression text blocks with structured delta cards.

- [ ] 2-column card grid layout
- [ ] Each card: metric name, baseline value, current value, delta (absolute + percent), mini progress bar
- [ ] Severity escalation cards (Warning→Critical) get a red border treatment
- [ ] "Show all N regressions" expand beyond top-4
- [ ] New renderer function `buildRegressionCard` in `src/DumpDetective.Reporting/Templates/report.renderers.panels.js`
- [ ] Ensure the C# `RegressionDashboard` section builder emits a block type consumable by this renderer

### 3.4 Snapshot Swimlane
Horizontal timeline strip replacing the compact-row snapshot strip.

- [ ] Horizontal SVG or flex-based swimlane: one node per snapshot
- [ ] Each node: date label, health badge (severity color), heap size, `[View]` button
- [ ] "BASE" anchor on index 0; "NOW" anchor on last snapshot
- [ ] Click node → expand that dump's detail section and scroll to it
- [ ] New renderer `buildSnapshotSwimlane` in `src/DumpDetective.Reporting/Templates/report.renderers.panels.js`
- [ ] Responsive: collapses to vertical list on narrow screens

### 3.5 Per-Analyzer Accordion Timelines
Upgrade from sparklines in tables to proper accordion charts.

- [ ] One accordion per analyzer with tracked metrics
- [ ] Expand → small responsive SVG line chart (labeled axes, snapshot dates on X)
- [ ] Baseline and current annotated with dotted reference lines
- [ ] If only 2 snapshots: render a before/after "delta bar" instead of a line chart
- [ ] New renderer `buildAnalyzerTimeline` in `src/DumpDetective.Reporting/Templates/report.renderers.charts.js`

---

## Phase 4 — Diff Highlights & Per-Dump Section Polish

### 4.1 Per-Dump Section Headers (Trend)
Make collapsed dump sections informative without expanding.

- [ ] Each dump section header shows a health summary badge: severity + finding count
- [ ] Collapsed header includes heap size and date at a glance
- [ ] Update `src/DumpDetective.Reporting/Templates/report.renderers.sections.js` trend dump-group renderer

### 4.2 Diff Highlights in Per-Dump Sections (Trend)
Metrics that changed vs the previous snapshot highlighted inline.

- [ ] Requires snapshot-delta metadata in the JSON output (C# side: `SnapshotDeltaContext` on `AnalyzerDetailSection` or `KeyMetric`)
- [ ] C#: emit `deltaValue` and `deltaPct` alongside each `KeyMetric` in trend sections
- [ ] JS: render delta badge (↑ +12%, ↓ −5%) next to metric value, colored by direction and magnitude
- [ ] Update `src/DumpDetective.Reporting/Templates/report.renderers.sections.js` key-metrics strip renderer

---

## Phase 5 — Print, Export & Accessibility Polish

### 5.1 Print Stylesheet
- [ ] Page 1: command view + triage cards (single dump) or regression banner (trend)
- [ ] Domain sections on subsequent pages with `page-break-before: auto` hints
- [ ] Suppress nav, sticky bar, and action buttons in print
- [ ] Verify `src/DumpDetective.Reporting/Templates/report.utilities.css` print rules cover new components

### 5.2 Share / Clipboard Export
- [ ] "Share" button copies minimal summary to clipboard: finding titles + key metrics, no file paths
- [ ] Format: plain text or Markdown, user-selectable
- [ ] No PII (no dump file path in clipboard payload)
- [ ] Add to hero action bar in `src/DumpDetective.Reporting/Templates/report.renderers.header.js`

### 5.3 Accessibility Completions
- [ ] `aria-live` update regions for expand/collapse of domain sections
- [ ] Keyboard-navigable chart legend (toggle series via Enter/Space)
- [ ] All severity color choices verified against WCAG AA contrast in both light and dark themes
- [ ] All new SVG charts include `<title>` and `<desc>` elements

---

## C# Backend Tasks (Required for Phase 3–4)

- [ ] **Timeline chart block emission** — trend section builders emit `ChartBlock { Kind = "timeline", PayloadJson = … }` with snapshot series data
- [ ] **Regression delta block** — `RegressionDashboard` section builder emits a structured block consumable by `buildRegressionCard`
- [ ] **Snapshot-delta metadata** — `KeyMetric` model gains optional `DeltaValue` and `DeltaPct` fields; trend section builders populate these
- [ ] **Health summary per dump** — `AnalyzerDetailSection` (or the trend dump group wrapper) exposes `MaxSeverity` and `FindingCount` so JS can render the per-dump header badge without expanding

---

## Visual Language Reference

| Element | Single Dump | Trend |
|---|---|---|
| First screen | Command view + triage cards | Regression banner + timeline chart |
| Primary question | "What is broken now?" | "What is getting worse?" |
| Charts | Treemap (top types), KPI bars | Multi-series line, delta cards |
| Tables | Search + sort + lazy-expand | Same + snapshot column |
| Severity signal | Color band on domain header | Arrow + magnitude on domain tile |
| Navigation | Domain-grouped + severity dots | Same + snapshot jump links |

---

## Notes & Decisions

- Chart rendering stays SVG-only (no external chart lib) to keep the report self-contained.
- All new CSS must use existing design tokens from `src/DumpDetective.Reporting/Templates/report.base.css` — no new color hex values outside `:root`.
- JS changes stay within the existing module split under `src/DumpDetective.Reporting/Templates/report.renderers.*.js` — new files only if a module exceeds ~300 lines.
- Dark mode compatibility is a hard requirement for every new visual component.
- Statuses in this tracker were re-verified against current template modules on 2026-05-30.
