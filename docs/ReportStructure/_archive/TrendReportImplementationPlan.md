# Trend Report Implementation Plan

> **Superseded.** This document captured the initial high-level workstreams. The detailed,
> step-by-step implementation plan is now in
> [TrendReportImplementationPlan2.md](TrendReportImplementationPlan2.md), anchored to the schema
> defined in [TrendReportFormat.md](TrendReportFormat.md). Retain this file for historical context
> only — all new implementation work should reference Plan 2.

## Objective
Turn the trend blueprint into a dedicated, comparison-first report flow while keeping the shared `AnalysisReportDocument` contract intact.

## Scope
- In scope:
  - Trend report composition order.
  - Comparison-first summary sections.
  - Snapshot strip and per-analyzer trend timelines.
  - Trend-specific visuals and links.
- Out of scope:
  - New long-lived history storage.
  - Separate schema for trend mode.
  - Unbounded historical aggregation.

## Current Baseline
- `TrendReportComposer` already composes the trend document.
- `TrendSnapshotSectionComposer` already renders per-snapshot detail.
- `FindingLifecycleComparer` already produces lifecycle deltas.
- The current gap is composition clarity: trend content is present, but the reading order and mode-specific emphasis are not yet blueprint-driven.
- The current contract delta is already visible in code: single-dump serialization consumes the live analyzer run list only, while trend composition adds `TrendReportData`, snapshot-derived synthetic sections, and compare-mode metadata before the current-dump section set.

## Contract Differences to Honor
- Single-dump reporting should continue to treat the live analyzer run list as the complete source of sections, findings, and quality notes.
- Trend reporting must treat the live current-dump run list as only one input; snapshot comparison data and lifecycle findings are separate inputs that affect ordering and summary content.
- The report layer must branch on `IsTrendReport`, `TrendDumpCount`, and snapshot context instead of assuming a one-dump identity.
- Trend findings and executive deltas must preserve comparative scope so navigation and provenance remain coherent across JSON, markdown, and HTML.

## Workstreams

### P0 - Blueprint alignment
- Reorder the trend report output to match [TrendReportBlueprint.md](TrendReportBlueprint.md).
- Ensure the opening section is trend summary first, not the current-dump section set.
- Add or adjust headings so the report reads as compare-first.
- Files:
  - src/DumpDetective.Reporting/Services/TrendReportComposer.cs
  - src/DumpDetective.Reporting/Services/TrendSnapshotSectionComposer.cs
  - src/DumpDetective.Reporting/Formatters/CanonicalReportFormatter.cs

### P1 - Provenance and navigation
- Preserve snapshot scope in trend findings and summary blocks.
- Make snapshot references stable and easy to jump to.
- Surface analyzer coverage and skipped analyzer context early.
- Files:
  - src/DumpDetective.Reporting/Services/TrendReportComposer.cs
  - src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs
  - src/DumpDetective.Reporting/Templates/report.renderers.js

### P2 - Trend visuals
- Keep trend visuals compact and information-dense.
- Prefer waterfall, sparklines, and snapshot cards.
- Avoid full-width decorative charts that overwhelm the report.
- Files:
  - src/DumpDetective.Reporting/Templates/report.renderers.js
  - src/DumpDetective.Reporting/Templates/report.css

### P3 - Quality and parity
- Make trend output consistent across JSON, markdown, and HTML.
- Verify the same findings and ordering appear in each renderer.
- Add or refresh golden tests for small and large compare reports.
- Files:
  - tests/DumpDetective.Tests/
  - src/DumpDetective.Reporting/Serialization/ReportJsonContext.cs

## Delivery Order
1. Align section ordering and headings.
2. Tighten trend summary and regression blocks.
3. Improve navigation between snapshot and analyzer detail.
4. Polish visuals and finalize renderer parity.

## Acceptance Criteria
- Trend mode reads as a dedicated report shape, not an appended diff.
- The first content a user sees is the comparison story.
- Current-dump detail remains available but secondary.
- JSON, markdown, and HTML preserve the same trend narrative.
- Snapshot drilldowns remain bounded and stable.