# DumpDetective Report Design System (Architecture-Grounded)

## Status
Active design baseline for current report architecture.

Validated against source on 2026-05-30.

## Scope
This spec defines a unified visual system across:
- Single dump reports (implemented)
- Multi-dump trend reports (implemented)
- Trace-only reports (future)
- Combined dump+trace reports (future)

It is grounded in the current reporting pipeline and file layout, not legacy docs.

## Current Architecture Baseline

### Report pipeline (today)
1. CLI orchestration executes analyzers (single or trend mode)
2. `CanonicalReportDocumentFactory` builds `AnalysisReportDocument`
3. `ReportSerializer` projects domains/findings/appendix/correlation data
4. Formatter renders output (`Text`, `Markdown`, `Html`, `Json`)
5. `HtmlReportRenderer` inlines CSS/JS from embedded template resources

### Document contracts currently in use
- `SingleDumpReportDocument`
- `TrendReportDocument`
- Shared base: `AnalysisReportDocument`

### Correlation currently present
- `InsightEngine` adds cross-analyzer findings
- `ReportSerializer.BuildCorrelationEvents` emits `CorrelationEventRecord`

Note: this is dump-domain correlation only; trace correlation is future scope.

## Source-of-Truth Assets

Primary renderer assets are embedded under:
- `src/DumpDetective.Reporting/Templates/report.html`
- `src/DumpDetective.Reporting/Templates/report.base.css`
- `src/DumpDetective.Reporting/Templates/report.header.css`
- `src/DumpDetective.Reporting/Templates/report.body.css`
- `src/DumpDetective.Reporting/Templates/report.findings.css`
- `src/DumpDetective.Reporting/Templates/report.detail.css`
- `src/DumpDetective.Reporting/Templates/report.utilities.css`
- `src/DumpDetective.Reporting/Templates/report.main.js`
- `src/DumpDetective.Reporting/Templates/report.renderers.*.js`
- `src/DumpDetective.Reporting/Templates/report.ui.js`

`wwwroot` files are useful as references/snapshots, but embedded template files are the runtime source of truth.

## Design Principles

1. Triage-first information hierarchy
First viewport must answer the mode’s core question immediately.

2. Flat-first visual language
Use solid semantic surfaces, border contrast, and minimal elevation. Avoid gradients by default.

3. Deterministic severity semantics
Critical/Warning/OK/Info must have identical meaning and styling across all modes.

4. Evidence-forward presentation
Every finding card and key panel should include evidence and recommended action.

5. Additive extensibility
Future trace/combined modes extend existing components instead of forking style systems.

## Visual Token System (Current + Required)

### Existing tokens already available
Current base CSS already defines semantic severity tokens, spacing scale, radii, typography, and shadows.

### Required token discipline
- No direct hex in component blocks where a semantic token exists.
- Severity colors must come only from `--sev-*` tokens.
- Flat mode default:
  - solid backgrounds
  - one-pixel semantic borders
  - low-contrast shadows only

### Gradient policy
- Default: off
- Allowed only for explicit hero/branding surfaces and only if readability remains AA compliant.

## Layout Contracts by Mode

### Single dump (implemented)
Above fold:
- Header summary
- Health scorecard
- Executive summary and top actions

Below fold:
- Domain sections
- Analyzer detail sections
- Appendix/incident context

### Trend (implemented)
Above fold:
- Trend-aware header summary
- Lifecycle counts (new/persistent/resolved)
- Health deltas and trend summary

Below fold:
- Trend analyzer sections
- Per-dump groups
- Trend appendix/context

### Trace-only (future)
Above fold:
- CPU/top contention summary + timeline snapshot

Below fold:
- Trace domains (CPU, contention, GC pauses, exceptions)

### Combined dump+trace (future)
Above fold:
- Correlated findings with confidence and source attribution

Below fold:
- Dump and trace domains with shared correlation links

## Component Contracts

### Core components to keep consistent now
- Severity badge
- Confidence band
- KPI/metric tile
- Finding card
- Domain header with severity signal
- Table wrapper (sticky header + sort + filter where available)

### Existing data-driven blocks in model/renderer
- `SectionBlock` variants
- `TableBlock`
- `ChartBlock`
- `ConfidenceBandBlock`

Guideline:
- Prefer extending these contracts before inventing new ad-hoc markup paths.

## Content Standards
- Titles must be diagnostic and specific.
- Evidence should include magnitude and unit where possible.
- Recommendations must be scoped and actionable.
- Heuristic/approximate caveats must be visible near the affected finding.

## Accessibility Standards
- WCAG AA contrast in light and dark themes.
- Keyboard-accessible summaries, navigation, and table interactions.
- ARIA live announcements for interactive state changes.
- Chart blocks must include textual fallback when visuals are unavailable.

## Modernization Plan from Current State

### Phase 1: Flatten and normalize current styles
- Remove remaining gradient-first visuals in template CSS except approved exceptions.
- Consolidate severity and confidence styles into shared utility classes.
- Keep spacing/radius/typography aligned with existing base tokens.

### Phase 2: Harden shared renderer primitives
- Centralize repeated card/table/header patterns in renderer modules.
- Avoid mode-specific visual drift between single and trend outputs.

### Phase 3: Prepare for trace and combined modes
- Add optional source-attribution affordances in finding cards.
- Reserve component slots for trace summary and correlation panels.
- Keep new fields additive in report JSON contracts.

### Phase 4: Validate quality bar
- Accessibility pass (keyboard, ARIA, contrast)
- Print parity checks for executive and triage content
- Visual regression fixtures for single/trend templates

## Forward Compatibility Rules
- Do not break `AnalysisReportDocument` compatibility for existing renderers.
- Add optional fields first; avoid hard required schema jumps.
- Maintain consistent severity rendering across `Text`, `Markdown`, and `Html` outputs.

## Acceptance Criteria
- Single and trend reports render with one consistent flat-first design system.
- Visual semantics for severity/confidence are identical across sections and modes.
- Template assets under `Templates/` remain the canonical implementation path.
- Trace and combined modes can be added without replacing the design foundation.