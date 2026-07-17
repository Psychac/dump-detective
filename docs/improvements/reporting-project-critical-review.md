# DumpDetective.Reporting Critical Review

**Status**: Current state (validated 2026-07-17)  
**Scope**: `src/DumpDetective.Reporting` — code/class structure, composition patterns, refactor opportunities

## Executive Summary

`DumpDetective.Reporting` houses two related but distinct systems:
- **Backend**: canonical document projection/composition (serializer, composers, builders)
- **Frontend**: interactive HTML report application (renderer, templates, browser-side logic)

Current state is functional but compressed. Key achievements and remaining gaps follow.

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

### 2. Trend Composition Has Structural Duplication
**Problem**: Rebuilds full document structures when simpler base projection would suffice.
- Base document build
- Separate trend findings computation
- Per-dump section materialization  
- Per-dump full document rebuild
- HTML-specific shaping for trend context

**Size**: `TrendReportComposer` ~1,400 lines / 40+ methods  
**Action**: Define shared base projection layer, treat trend as augmentation pipeline on top.

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

**Status**: Early modular extraction done (`report.ui.toc.js` ~739B, `report.ui.integrity.js` ~3.6KB), but main `report.ui.js` remains ~47.9KB.

**Action**: Continue splitting into focused modules (bootstrap, reading-mode, anchor-integrity, dynamic-lifecycle, accessibility helpers).

### 6. HtmlReportRenderer Payload Shaping
**Problem**: Single class still couples rendering, bundling, asset inlining, and fallback logic.  
**Status**: Static mutable state removed; still conflates concerns.  
**Action**: Separate HTML payload shaping from asset bundling strategy.

## Current Architecture Pieces

| Component | Status | Notes |
|-----------|--------|-------|
| `CanonicalReportDocumentFactory` | Thin wrapper | Delegates to `ReportSerializer`; useful if underlying projection is factored |
| `ExecutiveSummaryProjector` | Extracted & working | Used by both single and trend flows |
| `HtmlReportRenderer` | Settings-based | No static state; immutable configuration via `HtmlRenderSettings` |
| `DefaultAnalyzerFeatureModuleCatalog` | Centralized | Owns feature registration, consumed by CLI; expands Reporting scope |
| `TrendReportComposer` | Broad but functional | Orchestrates per-dump and trend document generation; complex overlap |
| `FindingGenerators` | Flat hierarchy | Namespace mismatch (Analysis vs Reporting) |
| `SectionBuilders` | Flat hierarchy | No domain grouping |
| `Templates/` | Partially modularized | Early extraction; main `report.ui.js` still dense |

## Recommended Cleanup Path

1. **Namespace alignment**: Rename `FindingGenerators` namespace to `DumpDetective.Reporting.FindingGenerators`.
2. **Test harness**: Add snapshot/golden tests for `ReportSerializer` output, `TrendReportComposer` document shape, `HtmlReportRenderer` payload.
3. **Decompose ReportSerializer**: Extract section assembler, finding mapper, domain projector, correlation builder into focused collaborators.
4. **Rationalize trend composition**: Define smaller base projection layer that both single and trend flows consume; reduce per-dump full-document rebuilds.
5. **Group builders/generators**: Reorganize `FindingGenerators` and `SectionBuilders` by domain (Memory, GC, Threads, Async, Runtime, Infrastructure, CrossCutting).
6. **Modularize report.ui.js**: Continue splitting template logic into smaller modules with clear responsibility boundaries.

## What to Preserve

- Canonical-document model and projection concept
- Embedded-resource delivery (offline-capable reports)
- Separation between analyzer-section and report-section concepts
- Multi-format rendering from single document model

## What to Avoid

- Replacing embedded-template architecture for boundary reasons
- Heavy web framework adoption without clear need
- Premature redesign of all report models before composition logic is decomposed
