# DumpDetective.Reporting Critical Review

**Status**: Nearly complete — one open item remains (validated 2026-07-20)
**Scope**: `src/DumpDetective.Reporting` — code/class structure, composition patterns, refactor opportunities

## Executive Summary

`DumpDetective.Reporting` houses two related but distinct systems:
- **Backend**: canonical document projection/composition (serializer, composers, builders)
- **Frontend**: interactive HTML report application (renderer, templates, browser-side logic)

All originally identified gaps are resolved except one. See [Open Item](#open-item) below.

## Open Item

### Group `FindingGenerators`/`SectionBuilders` by domain
**Problem**: Both directories are flat with no subfolder strategy — `FindingGenerators/` has 35 files, `SectionBuilders/` has 42, each one class, no grouping by feature area (Memory, GC, Threads, Async, Runtime, CrossCutting, etc.). Discoverability degrades as the count grows.

**Action**: Reorganize both directories into domain subfolders (e.g. `Memory/`, `GC/`, `Threads/`, `Async/`, `Runtime/`, `CrossCutting/`). Namespace can stay `DumpDetective.Reporting.FindingGenerators` / `...SectionBuilders` — this is a physical file reorg, not a namespace change.

## Resolved (verified against current source, 2026-07-20)

- **Dead HTML formatter removed** — `HtmlCanonicalReportFormatter` (~350 lines, unused in production DI) deleted; `HtmlReportRenderer` is now the only `IReportFormatter` for HTML, in both DI and test/benchmark code.
- **`CanonicalReportFormatter.cs` god file split** — now only `TextCanonicalReportFormatter` and `MarkdownCanonicalReportFormatter` remain co-located; each has its own render helpers, minimal shared surface.
- **`ReportSerializer` decomposed** — thin orchestrator (~155 lines) delegating to `ReportSectionAssembler`, `ReportDomainProjector`, `ReportCorrelationBuilder`, `ReportFindingMapper`, `ExecutiveSummaryProjector`.
- **`FindingGenerators` namespace fixed** — confirmed all 35 files declare `namespace DumpDetective.Reporting.FindingGenerators` (previously mismatched as `DumpDetective.Analysis.FindingGenerators`).
- **`TrendReportComposer` decomposed** — confirmed down to 493 lines (from ~1,400). `BuildTrendStory` extracted to `TrendStoryBuilder.cs`; `BuildTrendComparisonSection` deleted (dead code). Trend-only sections already lived in dedicated static builders (`TrendHealthScorecardBuilder`, `TrendSnapshotStripBuilder`, `TrendMetricTimelineSectionBuilder`, `TrendRegressionDashboardBuilder`, `TrendAppendixBuilder`, `TrendSnapshotSectionComposer`).
- **Base-projection path added** — `ReportSerializer.SerializeBaseProjection` / `CanonicalReportDocumentFactory.BuildBaseProjection` give the trend aggregate document a lean path (scalars/`HealthScorecard`/`ExecutiveSummary` only) instead of running the full single-dump `Factory`/`Serializer` path. Per-dump full-document rebuilds in `BuildPerDumpDocuments` are intentionally unchanged — client needs the full single-dump shape per historical snapshot, so no lighter projection applies there.
- **Formatter dispatch unified** — single-dump and trend paths both build documents then delegate to one `RenderDocument` dispatch path (`ReportBuilderFacade`), removing duplicated HTML-settings/version-defaulting logic.
- **`report.ui.js` modularized** — extracted `report.ui.tables.js`, `report.ui.actions.js`, `report.ui.search.js`, `report.ui.filters.js`, `report.ui.keyboard.js`, `report.ui.motion.js` (alongside pre-existing `report.ui.toc.js`, `report.ui.integrity.js`). `report.ui.js` now retains only tightly-coupled reading-mode/anchor-navigation/interaction-policy logic (545 lines).
- **`report.js` duplication resolved** — confirmed `wwwroot/js/report.js` (orphaned demo copy) is deleted. Only `Templates/report.js` remains, which is a deliberate lightweight module-loader/fallback renderer, not a duplicate.

## Current Architecture Pieces

| Component | Status | Notes |
|-----------|--------|-------|
| `ReportBuilderFacade` | Entry point | Wraps formatter dispatch, `ReportSerializer`, trend composer through single `RenderDocument` path |
| `ReportSerializer` | Thin orchestrator | ~155 lines; delegates to four collaborators below |
| `ReportSectionAssembler` | Extracted & working | Section build/merge/metadata/ordering/contract-slot normalization/appendix |
| `ReportDomainProjector` | Extracted & working | Domain grouping, cross-domain insights, shared severity/domain ordering primitives |
| `ReportCorrelationBuilder` | Extracted & working | Cross-domain correlation event derivation/merging |
| `ReportFindingMapper` | Extracted & working | Finding → `FindingRecord` mapping, evidence refs |
| `ExecutiveSummaryProjector` | Extracted & working | Used by both single-dump and trend flows |
| `HtmlReportRenderer` | Settings-based, live | No static state; immutable config via `HtmlRenderSettings`; only HTML formatter in production |
| `DefaultAnalyzerFeatureModuleCatalog` | Centralized | Owns feature registration, consumed by CLI |
| `TrendReportComposer` | Decomposed | 493 lines; orchestrates per-dump trend document generation via focused static builders |
| `FindingGenerators` | **Flat — open item** | Namespace correct, no domain subfolders |
| `SectionBuilders` | **Flat — open item** | No domain subfolders |
| `Templates/` | Modularized | Renderer logic split into ~8 files; `report.ui.js` retains only tightly-coupled orchestration logic |

## Verification Notes

- Traced both single-dump and trend call paths end-to-end (`ReportBuilderFacade`, `TrendReportComposer.ComposeCanonicalTrendReport`, `HtmlReportRenderer`). Rendering layer is fully unified — no separate trend template; the same four `IReportFormatter` implementations branch internally on `doc is TrendReportDocument`.
- Per-dump rebuild cost in `BuildPerDumpDocuments` is real, not incidental: an N-dump trend run does N+1 full document compositions (1 aggregate via base-projection + N per-dump full compositions), covered by `P0SmokeTests.TrendHtml_PerDumpJson_ContainsFullSingleDumpDocuments`.
- Golden/snapshot baselines live in `tests/DumpDetective.Tests/Golden/Baselines/`; CI verifies `ReportSerializer` section assembly, `TrendReportComposer` document shape, and `HtmlReportRenderer` payload against stored baselines.
