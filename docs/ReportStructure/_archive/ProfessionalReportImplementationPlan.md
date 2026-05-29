# Professional Report Implementation Plan

## Objective
Implement a production-grade report experience that matches the target structure in ProfessionalTierReport.md, while preserving performance constraints for very large dumps and supporting both single-dump and trend modes.

## Scope
- In scope:
  - Report schema, report composition, and rendering behavior.
  - Missing report sections and signal aggregation required by the professional report spec.
  - Single-dump and trend-mode parity for executive, diagnostic, and action layers.
- Out of scope:
  - Full graph algorithms that violate bounded-memory constraints.
  - ETW-only features from dump-only runs (these should be clearly labeled as unavailable).

## Source Of Truth
- Target spec: docs/ReportStructure/ProfessionalTierReport.md
- Current coverage baseline: docs/ReportStructure/AnalyzerCoverageAnalysis.md
- Existing UI/report work: docs/ReportStructure/ReportingRefactorPlan.md
- Report pipeline: docs/PipelineStages/ReportPipelineDesign.md

## Comparison: Current State vs ProfessionalTierReport

### Already aligned (keep and harden)
- Canonical document pipeline and format separation (serializer + formatter pattern).
- Executive summary section exists.
- Developer action plan exists.
- Confidence notes and dedup diagnostics exist.
- Trend composition exists, including timeline and trend-to-snapshot linking.
- HTML accessibility and pagination foundations exist.

### Partially aligned (upgrade required)
- Executive scoring model:
  - Current: category string heuristics.
  - Needed: explicit weighted, explainable scoring with confidence.
- Findings/actionability:
  - Current: title/evidence/recommendation.
  - Needed: owner/team, effort, verification step, and status-ready fields.
- Root intelligence and retention narratives:
  - Current: present through multiple analyzers but not unified into a coherent root-cause layer.
  - Needed: structured synthesis across sections 4, 5, 6, and 16.
- Trend parity:
  - Current: strong trend comparison section.
  - Needed: mode-aware parity for executive and action sections, plus lifecycle/regression impact in the same structure as single-dump outputs.

### Missing (new implementation)
- Professional sections 18-25 and analyzer support where absent.
- First-class evidence provenance model (source analyzer, metric keys, addresses, artifact references).
- Report-level quality gate and analyzer run-status matrix inside the report body.
- Incident/context envelope (runtime, GC mode, heap mode, environment metadata).

## Guiding Constraints
- Never materialize full heap/object graphs for reporting.
- Keep expensive computations bounded and scoped to top-N suspects.
- Use disk-backed/index-driven data when available.
- Preserve deterministic schema and stable anchors for automation.

## Mode Model (must be explicit in every phase)
- Single-dump mode:
  - Primary: deep forensic snapshot and root-cause narrative.
- Trend mode:
  - Primary: regression/lifecycle and cross-snapshot evolution.
  - Must include snapshot-specific drill-down with links to detailed evidence.

## Priority Roadmap

## P0 - Correctness and Trust (ship first)
Target: eliminate report behavior bugs and establish trustworthy outputs before expanding coverage.

### P0.1 Fix renderer data consistency
- Work:
  - Ensure JSON export uses the same loaded document object used by the renderer.
  - Ensure separate-json mode and embedded-json mode behave identically.
- Files:
  - src/DumpDetective.Reporting/Templates/report.js
  - src/DumpDetective.Cli/Pipeline/Stages/WriteOutputStage.cs
- Single-dump: required.
- Trend mode: required.
- Acceptance:
  - JSON download works in both embedded and external JSON modes.
  - Exported JSON exactly matches in-memory rendered document.

### P0.2 Fix filtering/pagination semantics
- Work:
  - Filter across full findings dataset, then paginate filtered subset.
  - Correct count labels to reflect total vs filtered counts.
- Files:
  - src/DumpDetective.Reporting/Templates/report.js
- Single-dump: required.
- Trend mode: required.
- Acceptance:
  - Search/filter results are identical regardless of page size.
  - Counts remain correct for large finding sets.

### P0.3 Add report quality panel
- Work:
  - Add Analyzer Run Status table and quality summary in report body.
  - Include completed/failed/skipped/timed-out counts.
- Files:
  - src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs
  - src/DumpDetective.Reporting/Services/ReportSerializer.cs
  - src/DumpDetective.Reporting/Services/TrendReportComposer.cs
  - src/DumpDetective.Reporting/Templates/report.js
- Single-dump: include all run statuses.
- Trend mode: include latest-run statuses and trend snapshot status summary.
- Acceptance:
  - Report always displays completeness status and caveats.

## P1 - Professional Information Architecture
Target: make report decision-ready for leaders and execution-ready for engineers.

### P1.1 Expand schema for traceability/actionability
- Work:
  - Extend finding/action records with:
    - ConfidenceScore (0-1)
    - EvidenceRefs (analyzer, metric key, addresses, artifact path)
    - SuggestedOwner, Effort, ValidationStep, TrackingStatus
- Files:
  - src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs
  - src/DumpDetective.Reporting/Serialization/ReportJsonContext.cs
  - src/DumpDetective.Reporting/Services/ReportSerializer.cs
- Single-dump: mandatory.
- Trend mode: mandatory, including snapshot index in EvidenceRefs.
- Acceptance:
  - Every Critical/Warning finding has confidence + evidence provenance.

### P1.2 Replace heuristic executive scoring with explicit scoring engine
- Work:
  - Implement score inputs and weights for leak, GC pressure, and contention.
  - Publish score contributors in executive section.
- Files:
  - src/DumpDetective.Reporting/Services/ReportSerializer.cs
  - src/DumpDetective.Reporting/Templates/report.js
- Single-dump: score from current snapshot signals.
- Trend mode: include score delta vs baseline/previous snapshot.
- Acceptance:
  - Scores are reproducible and explainable from shown components.

### P1.3 Add incident context block
- Work:
  - Add runtime/process/dump context and analysis configuration summary.
- Files:
  - src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs
  - src/DumpDetective.Cli/Pipeline/Stages/BuildReportStage.cs
  - src/DumpDetective.Reporting/Templates/report.js
- Single-dump: include full context.
- Trend mode: include latest context + per-snapshot context differences.
- Acceptance:
  - Context block available in all reports and JSON output.

## P2 - Close Core Coverage Gaps (Sections 1-17)
Target: complete high-value professional sections before new analyzer expansion.

### P2.1 Unified retention/root intelligence synthesis
- Work:
  - Consolidate sections 4, 5, 6 into a coherent root-cause layer:
    - Root distribution
    - Top root severity ranking
    - Root path evidence for top leak candidates
- Files:
  - src/DumpDetective.Reporting/Services/ReportSerializer.cs
  - section builders in src/DumpDetective.Reporting
- Single-dump: full detail.
- Trend mode: per-snapshot summary + net changes.
- Acceptance:
  - Top leak candidates include root kind, path, and retained/impact metrics.

### P2.2 Narrative generation for critical findings
- Work:
  - Add structured Cause -> Effect -> Evidence -> Fix narratives for Critical/Warning findings.
- Files:
  - src/DumpDetective.Reporting/Services/ReportSerializer.cs
  - finding generation integration points in reporting layer
- Single-dump: required.
- Trend mode: include lifecycle label (new/persistent/resolved/regressed).
- Acceptance:
  - Each high-severity finding has a consistent narrative template.

### P2.3 Confidence and limitations normalization
- Work:
  - Standardize confidence tiers and map all analyzer caps/limits to report section 17.
- Files:
  - src/DumpDetective.Reporting/Services/ReportSerializer.cs
  - src/DumpDetective.Reporting/Templates/report.js
- Single-dump: required.
- Trend mode: required, with snapshot-level caveat aggregation.
- Acceptance:
  - Confidence section is complete, standardized, and mode-aware.

## P3 - Extended Coverage (Sections 18-25)
Target: implement new analyzers and report builders for full ProfessionalTierReport coverage.

### P3.1 New analyzer track
- Work packages:
  - AppDomainAnalyzer (section 18)
  - JitAnalyzer (section 19)
  - BoxingAnalyzer (section 20)
  - FinalizableObjectAnalyzer (section 21)
  - ArrayAnalyzer deep report upgrades (section 22)
  - AsyncStateMachineAnalyzer deep report upgrades (section 23)
  - WeakReferenceAnalyzer (section 24)
  - SegmentReservationAnalyzer (section 25)
- Integration:
  - Add domain results.
  - Add analyzer section builders.
  - Add finding generators.
  - Register in analyzer factory.

### P3.2 Mode behavior for new sections
- Single-dump:
  - Full section detail with bounded metrics.
- Trend mode:
  - Delta and regression blocks for section-level KPIs where meaningful.
  - Non-delta sections rendered as latest snapshot plus notable changes.

### P3.3 Acceptance
- At least 90 percent of ProfessionalTierReport sections materially populated in single-dump.
- Trend report includes section-level deltas for all KPI-capable sections.

## Cross-Cutting Implementation Work

### Data model and serialization
- Version schema to 2.1 when new fields are introduced.
- Keep backward-compatible defaults for missing fields.

### Performance and memory safety
- Keep top-N and depth limits explicit in section outputs.
- Avoid eager graph materialization.
- Use indexes and bounded traversals only.

### Rendering/UX
- Two viewing lenses in HTML:
  - Executive lens: decision summary and top actions.
  - Engineering lens: full evidence and analyzer sections.
- Keep stable IDs for all section anchors in both modes.

### Output parity
- Ensure JSON, HTML, and markdown are semantically aligned for new fields.

## Test Plan

### Unit tests
- Serializer tests:
  - score calculations
  - narrative generation
  - confidence aggregation
  - mode-specific projections
- Formatter tests:
  - new fields render in HTML/markdown/text
- Trend composer tests:
  - lifecycle labels and deltas

### Integration tests
- Single-dump golden report verification.
- Trend-mode golden report verification.
- Separate JSON mode verification.
- Large finding-set filter/pagination correctness.

### Non-functional
- Benchmark report build and render stages.
- Verify no significant memory growth in report generation path for large dumps.

## Delivery Plan By Sprint

### Sprint 1 (P0)
- P0.1, P0.2, P0.3
- Exit criteria: trustworthy rendering and quality transparency.

### Sprint 2 (P1)
- P1.1, P1.2, P1.3
- Exit criteria: professional decision and action structure in both modes.

### Sprint 3-4 (P2)
- P2.1, P2.2, P2.3
- Exit criteria: complete and coherent sections 1-17.

### Sprint 5+ (P3)
- P3 analyzer rollout in dependency order.
- Exit criteria: broad coverage for sections 18-25 with trend-mode deltas.

## Definition Of Done
- Professional report is complete, explainable, and action-oriented in single-dump mode.
- Trend mode presents lifecycle and regression context with drill-down parity.
- Report confidence and limitations are explicit and trustworthy.
- Rendering and export behavior is correct and stable for large reports.
