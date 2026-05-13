# Trend Report Blueprint

## Purpose
Define the composition and reading order for trend mode in the professional report. Trend mode must feel like a comparison report first, not a single-dump report with deltas appended.

## Shared Contract

The base quality rules (schema/version emission, status normalization, renderer parity, top-N/depth visibility, golden tests) are defined in [ProfessionalTierReport.md](ProfessionalTierReport.md) and apply here unchanged.

Trend-mode specific invariants:
- Render from the same `AnalysisReportDocument` as single-dump mode; no trend-only schema semantics unless representable in JSON and markdown.
- Trend mode is a first-class report shape — not an appended diff section — with its own opening summary, lifecycle view, regression view, and snapshot drilldowns.
- Trend findings carry the same provenance as single-dump findings, plus snapshot scope and comparative metric context.
- Required trend blocks: lifecycle summary, metric regression summary, top regressions, per-snapshot overview cards, per-analyzer metric timelines, current-dump section set.
- The comparison layer must be readable as a standalone view even when per-dump sections are collapsed or skipped.
- Preserve the same findings, status values, provenance, and limits across all renderers.
- Prefer compact trend summaries before verbose per-dump detail.

## Reporting Contract Delta

Single-dump reporting hands the renderer one analyzer run set and one set of per-analyzer sections. Trend reporting hands the renderer a composed document with extra comparison context and two section sources: the current dump's run set plus snapshot-derived trend sections.

### Single-dump pipeline contract
- Input is one `AnalyzerRunResult` set for one dump.
- Output focuses on the current dump's findings, executive summary, confidence, and per-analyzer sections.
- `AnalyzerSections` are built directly from the live run list.
- `TrendDumpCount`, `TrendDumpPaths`, and snapshot context are empty or unset.

### Trend pipeline contract
- Input is one current run set plus `TrendReportData` containing snapshots, per-step deltas, overall comparisons, and lifecycle data.
- Output must include trend findings, trend executive deltas, and a trend comparison section before the current-dump section set.
- `AnalyzerSections` are composed from a trend comparison block plus per-snapshot blocks, then appended with the current-dump section set.
- `IsTrendReport`, `TrendDumpCount`, `TrendDumpPaths`, and snapshot context are required so the renderer can distinguish compare mode from single-dump mode.

### Contract implications
- The renderer must not assume that all sections came from the live analyzer run list.
- Trend mode may contain synthetic snapshot-derived sections that are not present in the current-dump run set.
- Trend findings need snapshot scope and comparative metric context so cross-snapshot evidence remains understandable in JSON, markdown, and HTML.
- Any UI shortcuts that rely on a single dump identity must guard on `IsTrendReport`.

## Trend Narrative Order
1. Trend header
- Baseline vs current dump identity.
- Dump count, snapshot window, compare mode, timestamps, and analyzer coverage.

2. Trend summary
- Net lifecycle movement: new, persistent, resolved.
- Score deltas and major executive shifts.
- Top regressions and top improvements.

3. Regression dashboard
- Metric regressions by analyzer and type.
- Severity escalations.
- Newly critical or newly suspicious findings.

4. Snapshot strip
- One compact card per snapshot.
- Per-snapshot health indicators, analyzer counts, and notable changes.

5. Per-analyzer timelines
- Only for analyzers with meaningful history.
- Collapsed by default.
- Keep the timeline signal visible without forcing a full section expansion.

6. Current-dump detail
- Reuse the normal professional section set.
- Place it after the comparison layer so the report answers “what changed?” before “what is it now?”.

7. Limitations and provenance
- Compare-mode caveats.
- Skipped or filtered analyzers.
- Heuristic metrics and approximate data sources.

## Required Trend Blocks

The authoritative section map, field-to-source mapping, rendering rules, and stable section anchors
(`T0`–`T7`) are defined in [TrendReportFormat.md](TrendReportFormat.md). Implementation steps,
dependency order, and testing checklist are in [TrendReportImplementationPlan2.md](TrendReportImplementationPlan2.md).

Required blocks at a glance (see `TrendReportFormat.md` for full field details):
- T0 Trend header (baseline/current identity, snapshot count, analyzer coverage).
- T1 Trend health scorecard (per-domain severity change vs baseline).
- T2 Trend executive summary (lifecycle metrics, score deltas, top regressions/improvements).
- T3 Regression dashboard (severity escalations, new findings, new leak signals).
- T4 Per-analyzer metric timelines (sparklines, step deltas; collapsed per analyzer).
- T5 Snapshot strip (one compact card per snapshot linking to detail).
- T6 Per-dump detail sections (reuses single-dump domain sections; collapsed by default).
- T7 Trend appendix (resolved findings, analyzer coverage map, trend limitations).

## Visual Rules
- Use compact comparison visuals over large decorative charts.
- Favor waterfall, sparklines, and small multiples over full-width charts.
- Keep chart labels short and count-oriented.
- Avoid duplicating the same metric in both trend summary and detailed tables unless the detail adds new information.

## Rendering Rules
- Trend mode must stand alone even if current-dump sections are collapsed.
- The opening view must still make sense when rendered as markdown.
- JSON output must preserve the same ordering and comparison metadata.
- Snapshot links should be stable and addressable from the report body.
- Rendering logic should branch on `IsTrendReport` rather than inferring mode from section labels.

## Non-Goals
- A separate data schema for trend mode.
- Heavy charting libraries in the browser.
- Unbounded historical timelines.
- Recomputing expensive analysis during rendering.

## Acceptance
- A reader can identify the baseline, the current dump, and the most important regressions within the first screen.
- The report remains readable when the per-dump detail is hidden.
- The same trend story appears in JSON, markdown, and HTML.