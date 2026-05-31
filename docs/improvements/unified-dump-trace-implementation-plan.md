# Unified Dump + Trace Implementation Plan

## Status
Proposed execution plan aligned to current codebase.

Companion doc:
- docs/improvements/unified-dump-trace-architecture.md

## Purpose
Turn the architecture proposal into an implementable plan with:
- phased delivery,
- project-level task slices,
- acceptance criteria,
- exit gates,
- test requirements.

## Baseline (already in code)
- Single dump orchestration: Load -> Index -> Analyze -> Build report -> Write output
- Multi-dump trend orchestration and comparer pipeline
- Cross-analyzer dump-domain correlation in reporting

No changes should regress these baselines.

## Target Modes
1. SingleDump (existing)
2. MultiDump (existing trend path)
3. TraceOnly (new)
4. Combined (new dump + trace)

## Workstream Overview
- W1: Mode model and orchestration shell
- W2: Trace ingestion + indexing foundation
- W3: Trace analyzers (v1 set)
- W4: Signal normalization + cross-source correlator
- W5: Reporting/UX integration
- W6: Hardening, perf, and rollout controls

---

## Phase 0 - Guardrails + Scaffolding (no behavior change)

### Goals
- Prepare extension points without changing runtime behavior.

### Task slices

#### src/DumpDetective.Core
- Add `AnalysisMode` enum:
  - `SingleDump`, `MultiDump`, `TraceOnly`, `Combined`
- Add mode to resolved execution contracts as optional/additive field.
- Keep defaults mapping to current behavior.

#### src/DumpDetective.Cli
- Add mode resolution logic that maps existing CLI inputs to current paths.
- Do not alter current output routing.
- Add feature flags in config model:
  - `Trace.Enabled` (default false)
  - `Combined.Enabled` (default false)

#### tests
- Add unit tests for mode resolution compatibility:
  - baseline path still single dump
  - `--trend` still multi-dump
  - baseline+current path still multi-dump

### Acceptance criteria
- Existing single and trend runs behave identically for same inputs.
- No diff in report shape for existing modes.
- All new fields are additive and backward compatible.

### Exit gate
- Green unit tests for CLI/config resolution.
- No startup validator regressions.

---

## Phase 1 - Trace Foundation (TraceOnly skeleton)

### Goals
- Introduce trace pipeline skeleton and `.nettrace` metadata/index foundation.

### Task slices

#### src/DumpDetective.Trace (preferred)
- Create project with namespaces:
  - `Input/`
  - `Indexing/`
  - `Analyzers/`
  - `Pipeline/`
  - `Models/`
- Add minimal contracts:
  - `ITraceLoader`
  - `ITraceIndexBuilder`
  - `ITraceAnalyzer`

#### src/DumpDetective.Core
- Add trace model primitives:
  - `TraceLoadContext`
  - `TraceIndexBuildResult`
  - `TraceAnalysisContext`
  - `TraceAnalyzerDomainResult` base

#### src/DumpDetective.Cli
- Add trace input args/config:
  - `--trace`
  - `TracePaths` in config
- Add `TraceOrchestrationService` skeleton:
  - load trace
  - build index
  - run zero/minimal analyzers
  - build trace-only report document

#### src/DumpDetective.Reporting
- Add trace report section shell and serializer-safe placeholders.
- Keep existing documents backward compatible.

#### dependencies
- Introduce trace parsing package selection and lock version (TraceEvent or equivalent).

### Acceptance criteria
- `TraceOnly` mode runs end-to-end on a valid `.nettrace` file.
- Metadata summary is emitted in report and CLI diagnostics.
- Large trace can be scanned with bounded memory (no full event materialization).

### Exit gate
- Integration test: trace-only happy path.
- Integration test: invalid trace file fails with scoped error.

---

## Phase 2 - Trace Analyzer v1 Pack

### Goals
- Deliver first actionable trace insights.

### Task slices

#### src/DumpDetective.Trace/Analyzers
- Implement analyzers:
  1. `CpuHotspotTraceAnalyzer`
  2. `ThreadContentionTraceAnalyzer`
  3. `GcPressureTraceAnalyzer`
  4. `ExceptionBurstTraceAnalyzer`
- Ensure all analyzers are streaming/aggregate based.
- Add analyzer options in Core options model.

#### src/DumpDetective.Cli
- Register trace analyzers via trace analyzer factory.
- Add include/exclude support parity for trace analyzers.

#### src/DumpDetective.Reporting
- Add finding generators and sections for trace analyzers.
- Ensure section ordering remains deterministic.

#### tests
- Unit tests per analyzer with synthetic traces.
- Snapshot tests for trace sections.

### Acceptance criteria
- Trace report includes ranked findings for CPU, contention, GC, exceptions.
- Analyzer failures are isolated (partial results still produced).
- Diagnostic mode prints analyzer status and timing for trace analyzers.

### Exit gate
- Integration tests for all 4 trace analyzers.
- Trend pipeline unaffected.

---

## Phase 3 - Normalized Signal Layer

### Goals
- Build shared signal abstraction used by both dump and trace outputs.

### Task slices

#### src/DumpDetective.Core
- Add contracts:
  - `DiagnosticSignal`
  - `SignalSource`
  - `SignalType`
  - `CorrelationKey`
  - `ISignalNormalizer`
- Add scoring model version constants.

#### src/DumpDetective.Analysis
- Add dump normalizers from selected analyzer domain results -> `DiagnosticSignal`.

#### src/DumpDetective.Trace
- Add trace normalizers -> `DiagnosticSignal`.

#### src/DumpDetective.Reporting
- Add optional signal payload serialization for debugging/report appendix.

### Acceptance criteria
- SingleDump and TraceOnly can each emit normalized signals.
- Signal payload is stable and version-stamped.
- No analyzer contract breakage.

### Exit gate
- Unit tests on normalization mapping and key stability.

---

## Phase 4 - Combined Mode (Dump + Trace)

### Goals
- Deliver first combined mode with deterministic cross-source correlation.

### Task slices

#### src/DumpDetective.Core
- Add correlator contracts:
  - `ISignalCorrelator`
  - `CorrelationRule`
  - `CombinedSignal`
- Add conflict/corroboration scoring policy primitives.

#### src/DumpDetective.Cli
- Add `CombinedOrchestrationService`:
  1. run dump pipeline
  2. run trace pipeline
  3. normalize both
  4. correlate
  5. render combined report
- Add input pairing policy:
  - explicit pair first,
  - fallback latest-by-time policy.

#### src/DumpDetective.Reporting
- Add “Correlated Findings” section at top for combined mode.
- Add source attribution fields in findings/evidence refs.
- Add confidence rationale rendering.

#### ruleset v1
- Implement small high-value rule set:
  - memory pressure + GC pause pressure
  - retained type + CPU hotspot method/module affinity
  - contention + blocked-thread dump evidence

### Acceptance criteria
- Combined run emits cross-source correlated findings.
- Correlated finding includes:
  - dump evidence refs,
  - trace evidence refs,
  - confidence/score inputs.
- If one source fails, run degrades gracefully to available source output.

### Exit gate
- Integration test: combined happy path.
- Integration test: dump succeeds, trace fails -> partial output with warning.

---

## Phase 5 - Multi-Dump + Trace Evolution

### Goals
- Extend trend to support signal-level evolution and future mixed-series analysis.

### Task slices

#### src/DumpDetective.Analysis + Trace
- Add snapshot emitters for normalized signals.
- Add signal trend comparer utilities.

#### src/DumpDetective.Cli
- Add optional trend-over-signals switch for diagnostics.

#### src/DumpDetective.Reporting
- Add trend views for correlated/signal deltas:
  - new
  - persistent
  - resolved

### Acceptance criteria
- Trend report can show selected signal-level deltas.
- Existing analyzer trend outputs remain available.

### Exit gate
- No regression in current trend sections.

---

## Phase 6 - Performance + Reliability Hardening

### Goals
- Validate scale behavior and production resilience.

### Task slices

#### performance
- Add benchmark scenarios:
  - large dump + medium trace
  - medium dump + long trace
  - combined with high analyzer count
- Track:
  - peak working set
  - elapsed time by stage
  - index throughput

#### reliability
- Add cancellation tests for trace and combined pipelines.
- Add corruption tolerance tests for malformed trace segments.
- Ensure output still writes partial report on scoped stage failures.

#### operability
- Extend diagnostic telemetry with stage-level stats for trace/combined runs.
- Add known-limitations section entries for unsupported trace/event variants.

### Acceptance criteria
- Bounded memory behavior verified on representative large inputs.
- No catastrophic failure when optional analyzers fail.
- Stage timings and memory diagnostics visible in diagnostic mode.

### Exit gate
- Benchmarks and integration suite pass thresholds.

---

## Project-by-Project Backlog Slice

### src/DumpDetective.Cli
- Mode resolution and routing
- New trace/combined orchestrators
- Input validation and pairing
- Diagnostic progress + stage telemetry

### src/DumpDetective.Core
- Mode enum and config contracts
- Trace models and options
- Signal normalization/correlation contracts
- Scoring model versioning

### src/DumpDetective.Analysis
- Dump signal normalizers
- Keep existing dump pipeline unchanged
- Optional adapter wrappers to participate in combined mode

### src/DumpDetective.Trace (new)
- Trace loader/index builder
- Trace analyzers
- Trace normalizers
- Trace artifacts (optional)

### src/DumpDetective.Reporting
- Trace sections + finding generators
- Combined correlated findings section
- Optional signal payload serialization
- Confidence explanation rendering

### tests/DumpDetective.Tests
- Mode resolution tests
- Trace-only integration tests
- Combined mode integration tests
- Normalizer/correlator unit tests
- Trend backward-compatibility snapshots

---

## Configuration Evolution Plan

### Step 1 (additive)
- Add optional fields only:
  - `Execution.Mode`
  - `Inputs.Traces`
  - `Correlation.*`
  - `Trace.*`

### Step 2 (promotion)
- Promote combined/trace defaults once stable.

### Compatibility rules
- Existing dump configs remain valid with no changes.
- Unknown new fields are ignored by old binaries (if serializer policy allows).

---

## Test Matrix (minimum)

1. SingleDump existing sample dump
2. MultiDump existing trend path (`--trend`)
3. TraceOnly valid `.nettrace`
4. Combined valid dump + trace pair
5. Combined with trace missing
6. Combined with dump missing
7. TraceOnly malformed trace
8. Cancellation mid-index build
9. Cancellation mid-analyzer run
10. Large input memory ceiling validation

---

## Rollout Strategy

1. Internal feature flag only (`Trace.Enabled`, `Combined.Enabled` false by default)
2. Dogfood on benchmark traces and selected real captures
3. Enable TraceOnly for broader users
4. Enable Combined in preview
5. Promote to default when reliability/perf thresholds are met

---

## Risks to Track

1. Correlation overconfidence
- Mitigation: explicit confidence factors and conflict penalties

2. Trace parser/event schema variance
- Mitigation: adapter layer and robust unknown-event handling

3. Report contract churn
- Mitigation: additive schema, version stamps, fallback render logic

4. Pipeline complexity growth
- Mitigation: shared stage abstractions and strict orchestration boundaries

---

## Deliverables Checklist

### D1 - Foundation
- Mode enum + routing in place
- No behavior change for existing modes

### D2 - TraceOnly v1
- End-to-end trace run
- 4 analyzers + sections

### D3 - Combined v1
- Signal normalization
- Cross-source correlator
- Correlated findings section

### D4 - Hardening
- Perf thresholds met
- Integration matrix green
- Feature flags ready for default-on decision

---

## Suggested Ownership Split
- CLI/orchestration: Platform runtime owner
- Trace indexing/parsing: Perf/runtime diagnostics owner
- Reporting contracts/UI: Reporting owner
- Correlation/scoring: Insights owner
- Integration/perf test harness: QA + perf owner

## Definition of Done (program-level)
- All four modes are implemented and selectable.
- Existing single/multi dump behavior remains stable and backward compatible.
- Combined mode surfaces stronger, source-attributed, confidence-scored findings.
- Scale constraints are validated for large dump/trace workloads.
