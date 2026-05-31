# Unified Dump + Trace Architecture (Code-Grounded)

## Status
Baseline implemented for dump workflows. Trace and dump+trace correlation are roadmap items.

Last validated against source tree under src/ on 2026-05-30.

## Why this doc
Previous architecture writeups drifted from implementation details. This document records:
- what exists now in code,
- what gaps remain,
- how to add trace and combined analysis without destabilizing current pipelines.

## Current Reality (from code)

### Implemented modes
- Single dump analysis
- Multi-dump analysis through trend sequence

### Not yet implemented as execution modes
- Trace-only analysis mode
- Combined dump + trace analysis mode

### Existing cross-analyzer correlation
Cross-domain correlation already exists inside report generation for dump findings:
- InsightEngine emits cross-cutting findings from multiple analyzer domain results.
- Reporting serializer emits CorrelationEventRecord entries based on finding tags/evidence references.

Important: this is dump-domain correlation, not trace ingestion/correlation.

## Current Architecture (actual)

### Solution modules in active use
- src/DumpDetective.Cli: command parsing, config resolution, mode routing, orchestration
- src/DumpDetective.Analysis: dump loading, heap index build, analyzers, trend comparers, insight engine
- src/DumpDetective.Core: shared abstractions, models, options
- src/DumpDetective.Reporting: canonical report document, section builders, format renderers, correlation event projection

### CLI mode routing
DumpAnalysisService selects execution path:
- Single mode: SingleDumpOrchestrationService
- Trend mode: TrendOrchestrationService

Trend mode is activated by:
- TrendDumpPaths (--trend), or
- BaselineDumpPath (--baseline) + current dump path

### Single dump pipeline stages (implemented)
1. Load dump
2. Scan + index heap
3. Run analyzers
4. Build report
5. Write output

### Trend pipeline flow (implemented)
1. Execute per-dump pipeline for each dump in sequence
2. Build snapshots from per-dump analyzer domain results
3. Compare using TrendAnalyzer + registered IAnalyzerTrendComparer
4. Compose trend report document and render output

## Data/Indexing Design in Production

### Heap index strategy
- HeapAnalysisCache implements both IHeapAnalysisCache and build-time IHeapIndexBuilder
- Index mode: Auto, Memory, Disk
- Auto mode currently uses file-size threshold to pick memory vs disk

### Build behavior
- Single pass heap scan for index build
- Precomputed aggregates and satellite candidates are included in HeapIndexBuildResult
- Disk-backed and memory-backed paths are both supported

### Runtime metadata access
- RuntimeFacade caches method-table to ClrType lookups
- Reduces repeated ClrMD metadata cost across analyzers

## Analyzer and Reporting Contracts (actual)

### Analyzer contract
- All analyzers implement IAnalyzer
- Output is AnalyzerDomainResult
- Startup validation enforces registration coverage for finding generators and trend comparers

### Reporting model
- AnalysisReportDocument has polymorphic forms:
  - SingleDumpReportDocument
  - TrendReportDocument
- Includes domains, findings, appendix, scorecards, and optional CorrelationEvents

### Correlation already present
- ReportSerializer.BuildCorrelationEvents clusters related findings and emits correlation events.
- Correlation confidence is currently categorical (High/Medium) and derived from finding relationships.

## Capability Matrix (Now vs Target)

| Capability | Current | Target |
| --- | --- | --- |
| Single dump analysis | Implemented | Keep |
| Multi-dump trend analysis | Implemented | Extend with richer baselining/windows |
| Trace file ingestion/indexing | Not implemented | Add as first-class pipeline |
| Trace analyzers (CPU/thread/GC/exception) | Not implemented | Add incremental analyzer set |
| Dump+trace unified mode | Not implemented | Add orchestrator mode |
| Cross-source scoring | Not implemented | Add signal-level correlation engine |

## Target Architecture Extension (minimal disruption)

### Principle
Keep raw ingest/index paths source-specific. Fuse only at normalized signal layer.

### New execution modes
Add explicit mode enum in CLI/core:
- SingleDump
- MultiDump
- TraceOnly
- Combined

Current behavior maps as:
- existing single path => SingleDump
- existing trend path => MultiDump

### New module boundary
Preferred:
- Add src/DumpDetective.Trace

Contains:
- trace input loader/parsers
- trace index writers/readers
- trace analyzers
- trace-specific options

Alternative short-term:
- host under DumpDetective.Analysis/Trace namespace and extract later.

### Shared signal layer (new in Core)
Introduce normalized signal contracts independent of source:
- DiagnosticSignal
  - SignalType
  - Source (Dump, Trace)
  - CorrelationKey
  - ImpactScore
  - ConfidenceScore
  - EvidenceRefs

- ISignalNormalizer (dump and trace variants)
- ISignalCorrelator
- CombinedSignal

This layer should feed reporting, not replace existing analyzer domain result contracts immediately.

## Trace Pipeline (v1 proposal)

### Input
- .nettrace first

### Stages
1. Trace metadata scan
2. Streaming trace index build (disk-backed)
3. Trace analyzer execution
4. Normalize to DiagnosticSignal
5. Report composition

### First analyzers to prioritize
1. CPU hotspot analyzer (inclusive/exclusive sample counts)
2. Thread contention/wait analyzer
3. GC pause/allocation pressure analyzer
4. Exception burst analyzer

## Combined Dump + Trace Mode (v1 proposal)

### Flow
1. Execute dump pipeline (existing)
2. Execute trace pipeline (new)
3. Normalize both into common signals
4. Correlate by keys (type/method/module/thread/runtime dimensions)
5. Rank and emit correlated findings section, then source-specific sections

### Scoring bootstrap
Use simple deterministic formula first:

FinalScore = ImpactScore * ConfidenceScore * CrossSourceMultiplier

- single-source: multiplier = 1.00
- corroborated by both sources: multiplier > 1.00
- conflicting signals: multiplier < 1.00

Keep constants in config and stamp model version in report.

## Multi-Dump (Trend) Enhancements

Current trend implementation is solid and should be preserved. Improvements to add:
- input-window matching policies (time/app/build metadata)
- stronger baseline selection semantics
- optional rolling trend windows for N dumps
- trend over normalized signals (future) in addition to analyzer-specific deltas

## Orchestration Refactor Plan

Current state has two concrete orchestrators. Evolve to mode-driven orchestration while preserving behavior.

### Phase A (no behavior change)
- Introduce mode enum and adapter routing
- Keep current SingleDumpOrchestrationService and TrendOrchestrationService intact

### Phase B (trace onboarding)
- Add TraceOrchestrationService
- Add CLI/config support for trace inputs

### Phase C (combined mode)
- Add CombinedOrchestrationService
- Add normalizer + correlator stage between analysis and reporting

### Phase D (convergence)
- Optionally converge to composable stage pipeline shared by all modes

## Reporting Evolution Plan

Current report model already has useful extensibility hooks (Domains, CrossDomainInsights, CorrelationEvents, scorecards).

Additions for trace/combined:
- trace domain sections
- source marker in finding/evidence records
- correlated findings section for dump+trace
- confidence explanation payload for cross-source scoring

Avoid breaking renderer contracts by adding optional fields first.

## Performance and Safety Constraints (must keep)
- Streaming enumerations only for large data sources
- No full heap or full trace materialization
- Disk-backed index support for heavy workloads
- Analyzer failures remain scoped and non-fatal where safe
- Preserve cancellation and diagnostics hooks in orchestration stages

## Risks and Mitigations

1. Risk: mode explosion and orchestration complexity
Mitigation: incremental adapter model, retain existing services until stable

2. Risk: report contract churn
Mitigation: additive fields, versioned scoring model, formatter fallback logic

3. Risk: trace volume and memory pressure
Mitigation: streaming index build, bounded top-N expansions, disk-backed by default

4. Risk: false confidence in correlation
Mitigation: explicit confidence model, conflict penalties, surfaced caveats

## Definition of Done for Unified Architecture v1
- Existing single and multi-dump behavior remains stable
- Trace-only mode runs end-to-end with at least CPU + contention analysis
- Combined mode emits correlated findings from dump and trace signals
- Report includes explicit source attribution and confidence model version
- Large-input runs remain bounded by streaming/disk index strategy
