# DumpDetective.Reporting Critical Review

**Status**: Current state (validated 2026-07-18)  
**Scope**: `src/DumpDetective.Reporting` — code/class structure, composition patterns, refactor opportunities

## Executive Summary

`DumpDetective.Reporting` houses two related but distinct systems:
- **Backend**: canonical document projection/composition (serializer, composers, builders)
- **Frontend**: interactive HTML report application (renderer, templates, browser-side logic)

Current state is functional but compressed. Key achievements and remaining gaps follow.

**Highest-priority finding (done):** `Formatters/CanonicalReportFormatter.cs` contained a complete, ~350-line second HTML renderer (`HtmlCanonicalReportFormatter`, lines 708-1059) with its own inline CSS/JS, alongside the real one (`HtmlReportRenderer.cs`). Production DI (`src/DumpDetective.Cli/Hosting/ServiceRegistration.cs`) only ever registered `HtmlReportRenderer` — `HtmlCanonicalReportFormatter` was dead in production, kept alive only by `P0SmokeTests.BuildFacade` and `ReportingHotspotBenchmark`. Removed; see [Gap 7](#7-dead-html-formatter-implementation-350-lines-done) below.

## Single-Dump vs Trend Report Path: Structural Consistency

Verified by tracing both call paths end-to-end (`ReportBuilderFacade`, `TrendReportComposer.ComposeCanonicalTrendReport`, `HtmlReportRenderer`, and literal call sites in `TrendReportComposer.cs`).

**Rendering layer is fully unified — good.** There is no separate "trend template." Single-dump and trend reports are rendered by the exact same four `IReportFormatter` implementations (`TextCanonicalReportFormatter`, `MarkdownCanonicalReportFormatter`, `HtmlReportRenderer`, `JsonCanonicalReportFormatter`). Each one branches internally on `doc is TrendReportDocument` (a subtype of `AnalysisReportDocument`) to add trend-only content (dump count, lifecycle counts, dumps-analyzed list). `HtmlReportRenderer` embeds the same `Templates/*.js` client app for both modes; `CompactReportJson`/`CompactPerDumpJson` (`HtmlReportRenderer.cs:92,108`) strip/reshape trend-only JSON fields for the client at render time rather than branching to different templates. Client-side JS (`report.renderers.*.js`) reads `isTrend`-style flags from the embedded JSON, same as the C# side.

**Document-construction layer diverges structurally — as expected, but worth naming.** The two paths are genuinely different shapes, not just different data:
- **Single-dump**: `ReportBuilderFacade.BuildRenderedReport` → `CanonicalReportDocumentFactory.BuildDocument` → `ReportSerializer.Serialize`. One hop of pure delegation into one composition engine.
- **Trend**: `ReportBuilderFacade.BuildRenderedTrendReport` → `TrendReportComposer.ComposeCanonicalTrendReport`, which (a) builds a base document through the *same* `CanonicalReportDocumentFactory`/`ReportSerializer` path, then (b) appends trend-only sections by calling six separate collaborators directly by name at fixed call sites in `ComposeCanonicalTrendReport`/`BuildPerDumpSections` (`TrendReportComposer.cs:45,115,119,122,131,390`): `TrendHealthScorecardBuilder.Build`, `TrendSnapshotStripBuilder.Build`, `TrendMetricTimelineSectionBuilder.Build`, `TrendRegressionDashboardBuilder.Build`, `TrendAppendixBuilder.Build`, `TrendSnapshotSectionComposer.Build`. All six are confirmed live (not dead code) — each has exactly one production call site in `TrendReportComposer.cs`, matching design notes in `docs/ReportStructure/TrendReportFormat.md` and `TrendReportImplementationPlan2.md`.
- **Per-dump rebuild cost (verified, not just claimed)**: `TrendReportComposer.BuildPerDumpDocuments` (`TrendReportComposer.cs:420-437`) reruns the *full single-dump path* (`CanonicalReportDocumentFactory`/`ReportSerializer`) once per historical snapshot in the trend set, purely so `HtmlReportRenderer.CompactPerDumpJson` can embed full per-dump JSON for client-side toggling (covered by `P0SmokeTests.TrendHtml_PerDumpJson_ContainsFullSingleDumpDocuments`). This is real, not incidental: an N-dump trend run does N+1 full document compositions (1 aggregate + N per-dump), all through the same expensive `ReportSerializer.Serialize`.

**Revises Gap 2 below**: `TrendReportComposer`'s ~1,400 lines are *not* a flat undifferentiated god method — 6 of its concerns are already extracted into single-purpose static builder classes, matching the pattern the review recommends elsewhere in this doc. The genuinely unextracted, still-inline logic is narrower than the original framing suggested: `BuildTrendComparisonSection` (~207 lines, `TrendReportComposer.cs:663-869`) and `BuildTrendStory` (~176 lines, `TrendReportComposer.cs:439-614`), plus `ComposeCanonicalTrendReport` itself (~170 lines, mostly orchestration/wiring of the six builders). These two methods are the actual decomposition targets, not the whole class.

## Remediated Issues

✓ **Static renderer overrides removed** — `HtmlReportRenderer` now uses explicit immutable `HtmlRenderSettings` passed to `Render()` method, eliminating hidden static state.

✓ **Executive summary extraction** — `ExecutiveSummaryProjector` extracted and reused by both single-dump and trend paths.

✓ **Capability module system** — `DefaultAnalyzerFeatureModuleCatalog` centralizes ~34 feature module registrations (analyzer, finding generator, trend comparer, section builder per domain), owned by Reporting and consumed by CLI hosting layer.

## Remaining Gaps

### 1. ReportSerializer Still Over-Broad
**Problem**: Named as serializer, behaves as composition orchestration engine.
- Builds analyzer sections
- Builds cross-cutting sections  
- Assembles, orders, annotates sections
- Maps findings and pipeline errors
- Derives summary values (managed bytes, etc.)
- Builds domains, correlations, appendix, executive summary

**Size**: ~1,400 lines / 40+ methods  
**Action**: Split into focused: section assembler, finding mapper, domain projector, appendix builder, correlation builder.

### 2. Trend Composition Has Structural Duplication (revised — see [structural consistency analysis](#single-dump-vs-trend-report-path-structural-consistency))
**Problem**: Rebuilds full document structures when simpler base projection would suffice.
- Base document build (shares `CanonicalReportDocumentFactory`/`ReportSerializer` with single-dump — verified, not duplicated logic)
- Trend-only sections already delegated to 6 focused static builders (`TrendHealthScorecardBuilder`, `TrendSnapshotStripBuilder`, `TrendMetricTimelineSectionBuilder`, `TrendRegressionDashboardBuilder`, `TrendAppendixBuilder`, `TrendSnapshotSectionComposer`) — **this part is not a gap**, it's a working example of the decomposition pattern Gap 1 wants applied to `ReportSerializer`
- `BuildTrendComparisonSection` (~207 lines) and `BuildTrendStory` (~176 lines) remain large, un-extracted inline methods — the real remaining decomposition targets
- Per-dump full document rebuild: `BuildPerDumpDocuments` reruns the entire single-dump `Factory`/`Serializer` path once per historical snapshot (N dumps → N+1 full compositions), verified via `P0SmokeTests.TrendHtml_PerDumpJson_ContainsFullSingleDumpDocuments` — genuine cost, not just structural noise
- HTML-specific shaping for trend context is handled downstream in `HtmlReportRenderer` (`CompactReportJson`/`CompactPerDumpJson`), not in the composer itself — correctly separated

**Size**: `TrendReportComposer` ~1,089 lines / 51 symbols (measured), of which 6 sections' worth of logic already lives in separate files  
**Action**: Extract `BuildTrendComparisonSection` and `BuildTrendStory` into their own static builder classes, following the existing `Trend*Builder` naming/shape convention exactly. Separately, evaluate whether `BuildPerDumpDocuments` needs a full `ReportSerializer.Serialize` pass per dump, or whether a lighter per-dump projection (skip cross-domain insights/correlations/appendix, which are only meaningful at the aggregate level) would satisfy what the client template actually renders per-dump.

### 3. Namespace/Project Mismatch
**Problem**: `FindingGenerators` live under `Reporting/FindingGenerators/*.cs` but declare `namespace DumpDetective.Analysis.FindingGenerators`.
- Files are compiled and owned by Reporting project  
- Namespace still claims Analysis ownership
- Creates cosmetic confusion; was leftover from migration

**Action**: Rename to `DumpDetective.Reporting.FindingGenerators`.

### 4. FindingGenerators and SectionBuilders Still Flat
**Problem**: No internal grouping by domain or feature family despite dozens of files.
- `FindingGenerators/` contains many classes with no subfolder strategy
- `SectionBuilders/` similarly flat
- Discoverability and feature ownership unclear as count grows

**Action**: Group by domain (Memory, GC, Threads, Async, Runtime, etc.) and CrossCutting.

### 5. Template Browser-Side App Has Grown
**Problem**: `Templates/report.ui.js` manages multiple concerns without clear module boundaries.
- Reading mode control
- Dynamic content sync
- Collapsible state management
- Accessibility sync
- Anchor recovery and navigation integrity
- Interaction policy

**Status (revised)**: More modular than previously credited. Alongside `report.ui.toc.js` (~739B) and `report.ui.integrity.js` (~3.6KB), rendering logic is already split across dedicated files: `report.renderers.blocks.js`, `report.renderers.charts.js`, `report.renderers.findings.js`, `report.renderers.header.js`, `report.renderers.nav.js`, `report.renderers.panels.js`, `report.renderers.sections.js`, `report.renderers.shared.js`, plus `report.dom.js` and `report.main.js`. `report.ui.js` itself (~47.9KB) remains the dense orchestration/state-management monolith (reading mode, collapsible state, accessibility sync, anchor recovery, interaction policy all still live there).

**New concern**: `Templates/report.js` and `wwwroot/js/report.js` both exist. Confirm whether one is a generated/mirrored artifact of the other (build output) or duplicated source — undocumented duplication here is a maintenance trap.

**Action**: Continue splitting `report.ui.js` specifically into focused modules (bootstrap, reading-mode, anchor-integrity, dynamic-lifecycle, accessibility helpers) — the renderer-level split is already largely done. Clarify/eliminate the `report.js` duplication.

### 6. HtmlReportRenderer Payload Shaping
**Problem**: Single class still couples rendering, bundling, asset inlining, and fallback logic.  
**Status**: Static mutable state removed; still conflates concerns.  
**Action**: Separate HTML payload shaping from asset bundling strategy.

### 7. Dead HTML Formatter Implementation (~350 lines) (done)
**Problem**: `Formatters/CanonicalReportFormatter.cs` defined `HtmlCanonicalReportFormatter` (lines 708-1059) — a full, independent HTML renderer with its own `AppendCss`/`AppendJs` inline styling, separate from and unrelated to `HtmlReportRenderer.cs` (which drives the actual `Templates/` app).
- Both implemented `IReportFormatter` with `Format => ReportFormat.Html`.
- **Verified**: `src/DumpDetective.Cli/Hosting/ServiceRegistration.cs::BuildHost` registered only `IReportFormatter, HtmlReportRenderer` in production DI. `HtmlCanonicalReportFormatter` was never registered there.
- Its only remaining references were `tests/DumpDetective.Tests/Integration/P0SmokeTests.cs::BuildFacade` and `src/BenchmarkSuite1/ReportingHotspotBenchmark.cs`, both of which constructed it directly (bypassing DI).
- **Done**: Deleted `HtmlCanonicalReportFormatter` and its private helpers (`RenderBlocksHtml`, `RenderHealthScorecardHtml` (HTML overload), `RenderTableHtml`, `AppendCss`, `AppendJs`); updated all call sites (`P0SmokeTests`, `ReportFlowIntegrationTests`, `ReportingCompositionTests`, `ReportingHotspotBenchmark`) to construct `HtmlReportRenderer` instead. `HtmlFormatter_ShouldRenderDetailedAnalyzerSections_AsCollapsibleBlocks` was removed rather than ported — it asserted on server-side-rendered `AnalyzerSections`/`Findings` markup, but those fields are `[JsonIgnore]` on `AnalysisReportDocument` and unreachable by `HtmlReportRenderer`, which drives content exclusively from `Domains` via client-side JS (consistent with the existing Golden-test precedent of using `JsonCanonicalReportFormatter` instead of raw HTML string comparison). Zero production behavior change, ~350 lines removed.

### 8. CanonicalReportFormatter.cs Is a 1,000+ Line God File With Three Unrelated Formatters
**Problem**: One file (1,059 lines) declared `TextCanonicalReportFormatter` (45-326), `MarkdownCanonicalReportFormatter` (330-704), and `HtmlCanonicalReportFormatter` (708-1059, dead — see [Gap 7](#7-dead-html-formatter-implementation-350-lines-done), now removed) plus a shared `ReportFormatterHelpers` class. The remaining two formatters share almost no code (each has its own `RenderBlocksXxx`/`RenderTableXxx`/`RenderHealthScorecardXxx`) and have no reason to co-locate.
**Action**: Split the remaining two into `TextCanonicalReportFormatter.cs` and `MarkdownCanonicalReportFormatter.cs`, one class per file, consistent with every other formatter (`HtmlReportRenderer.cs`, `JsonCanonicalReportFormatter.cs` already follow this convention). Not yet done.

### 9. Three-Layer Delegation: ReportBuilderFacade → CanonicalReportDocumentFactory → ReportSerializer
**Problem**: `CanonicalReportDocumentFactory` (10 lines of logic across 3 methods) does nothing but forward its arguments to `ReportSerializer.Serialize`/`SerializeSectionsOnly` with different defaults. `ReportBuilderFacade` holds a reference to both `CanonicalReportDocumentFactory` and (indirectly, via formatter list) render logic, plus its own `RenderDocument` method that calls `HtmlReportRenderer` directly rather than through the formatter list used by `BuildRenderedReport`. Two different dispatch paths to produce HTML (`_formatters.FirstOrDefault(f => f.Format == format)` vs. a hardcoded `HtmlReportRenderer` call in `RenderDocument`) is itself confusing independent of Gap 7.
**Action**: Fold `CanonicalReportDocumentFactory`'s three methods directly into `ReportBuilderFacade` (or keep as static helpers on `ReportSerializer`) and remove the pass-through class. Unify on a single formatter-dispatch path in the facade.

## Current Architecture Pieces

| Component | Status | Notes |
|-----------|--------|-------|
| `CanonicalReportDocumentFactory` | Thin wrapper | Pure delegation to `ReportSerializer`, no independent logic — candidate for removal (Gap 9) |
| `ReportBuilderFacade` | Entry point | Wraps formatter dispatch + document factory + trend composer; has two divergent HTML dispatch paths (Gap 9) |
| `ExecutiveSummaryProjector` | Extracted & working | Used by both single and trend flows |
| `HtmlReportRenderer` | Settings-based, live | No static state; immutable configuration via `HtmlRenderSettings`; the only HTML formatter, in production DI and now the only one at all |
| `DefaultAnalyzerFeatureModuleCatalog` | Centralized | Owns feature registration, consumed by CLI; expands Reporting scope |
| `TrendReportComposer` | Broad but functional | Orchestrates per-dump and trend document generation; complex overlap |
| `FindingGenerators` | Flat hierarchy | Namespace mismatch (Analysis vs Reporting) |
| `SectionBuilders` | Flat hierarchy | No domain grouping |
| `Templates/` | Modularized more than expected | Renderer logic already split into ~8 files; `report.ui.js` orchestration layer still dense; possible `report.js` duplication vs `wwwroot/js/report.js` |

## Recommended Cleanup Path

0. **Delete dead code first (done)**: Removed `HtmlCanonicalReportFormatter` (Gap 7) — zero risk, ~350-line reduction, removes the "which HTML renderer is live?" confusion.
1. **Namespace alignment**: Rename `FindingGenerators` namespace to `DumpDetective.Reporting.FindingGenerators`.
2. **Test harness**: Add snapshot/golden tests for `ReportSerializer` output, `TrendReportComposer` document shape, `HtmlReportRenderer` payload.
3. **Decompose ReportSerializer**: Extract section assembler, finding mapper, domain projector, correlation builder into focused collaborators.
4. **Rationalize trend composition**: Define smaller base projection layer that both single and trend flows consume; reduce per-dump full-document rebuilds.
5. **Group builders/generators**: Reorganize `FindingGenerators` and `SectionBuilders` by domain (Memory, GC, Threads, Async, Runtime, Infrastructure, CrossCutting).
6. **Modularize report.ui.js**: Continue splitting the orchestration layer into smaller modules with clear responsibility boundaries; resolve `report.js` duplication.
7. **Split CanonicalReportFormatter.cs** (Gap 8): one formatter class per file.
8. **Collapse CanonicalReportDocumentFactory** into `ReportBuilderFacade`/`ReportSerializer` and unify HTML dispatch (Gap 9).

## What to Preserve

- Canonical-document model and projection concept
- Embedded-resource delivery (offline-capable reports)
- Separation between analyzer-section and report-section concepts
- Multi-format rendering from single document model

## What to Avoid

- Replacing embedded-template architecture for boundary reasons
- Heavy web framework adoption without clear need
- Premature redesign of all report models before composition logic is decomposed
