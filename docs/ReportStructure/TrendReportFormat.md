# Trend Report Format

## Purpose

Define the composition, section map, and rendering rules for the trend (compare-mode) report.
This document is the authoritative schema spec for all trend renderers (HTML, JSON, markdown).
It works alongside `SingleDumpReportFormat.md` — the single-dump section map applies to each
per-snapshot detail block within this document.

The narrative contract and design principles live in `TrendReportBlueprint.md`.
The data availability audit lives in `ProfessionalTierReport.md` §14.

Visual and comparative readability upgrades for trend mode are defined in `TrendReportFormat.v2.md`.
Use this v1 document for schema/data contract authority and v2 for UI/UX and trend storytelling guidance.

---

## Core Principle

**The report must answer "what changed?" before "what is it now?"**

A reader should be able to identify the baseline, the current dump, and the most important
regressions within the first screen. Per-dump detail is secondary and must be collapsible.

---

## Document Schema

```
TrendReportDocument  (extends AnalysisReportDocument)
├── TrendHeader
├── TrendHealthScorecard        per-domain severity change vs baseline
├── TrendExecutiveSummary
│   ├── LifecycleSummary        new / persistent / resolved finding counts
│   ├── ScoreDeltas             leak / GC / thread contention score movement
│   ├── TopRegressions[]        top severity metric regressions
│   └── TopImprovements[]       top improvements (secondary)
├── RegressionDashboard
│   ├── SeverityEscalations[]   Warning → Critical transitions
│   ├── NewFindings[]           findings absent in baseline, present now
│   └── NewLeakSignals[]        types newly appearing in leak candidates
├── SnapshotStrip               one compact row per snapshot
├── PerAnalyzerTimelines[]      metric timelines, one per analyzer with history
├── SnapshotDetails[]           per-dump sections (from SingleDumpReportFormat.md)
│   └── SnapshotDetailSection   collapsed by default in HTML
└── TrendAppendix
    ├── ResolvedFindings[]
    ├── AnalyzerCoverageMap     which analyzers ran in which snapshots
    ├── AnalyzerRunSummary      current-dump run statuses
    └── TrendLimitations[]
```

All data in `TrendReportDocument` fields:

| Field | Type | Source |
|---|---|---|
| `SchemaVersion` | string | `"2.1"` |
| `GeneratedAtUtc` | DateTime | composer timestamp |
| `ElapsedSeconds` | double | pipeline elapsed |
| `IncidentContext` | `AnalysisIncidentContext` | current run context; `IsTrendReport = true` |
| `IncidentContext.TrendSnapshots` | `IReadOnlyList<TrendSnapshotContext>` | one entry per snapshot |
| `TrendDumpCount` | int | `trendData.Snapshots.Count` |
| `TrendDumpPaths` | `IReadOnlyList<string>` | path per snapshot |
| `Findings` | `IReadOnlyList<FindingRecord>` | trend-scope findings only (lifecycle + regression findings) |
| `ExecutiveSummary` | `ExecutiveSummaryRecord` | trend-extended summary with score deltas |
| `AnalyzerSections` | `IReadOnlyList<AnalyzerDetailSection>` | trend sections first, then per-dump detail |
| `AnalyzerRunStatuses` | `IReadOnlyList<AnalyzerRunStatusRecord>` | current dump's run statuses |

---

## Section Map

### T0. Trend Header

**Implementation:** embedded in `TrendReportDocument` top-level fields.  
**Rendered as:** a dedicated header block above all sections.

| Field | Source |
|---|---|
| Baseline dump path | `TrendDumpPaths[0]` |
| Current dump path | `TrendDumpPaths[^1]` |
| Baseline timestamp | `IncidentContext.TrendSnapshots[0].GeneratedAtUtc` |
| Current timestamp | `IncidentContext.TrendSnapshots[^1].GeneratedAtUtc` |
| Snapshot count | `TrendDumpCount` |
| Analyzer coverage | `IncidentContext.ActiveAnalyzerCount` / `ActiveAnalyzers` |
| GC mode | `IncidentContext.GcMode` |
| Runtime version | `IncidentContext.RuntimeVersion` |

---

### T1. Trend Health Scorecard

**Extends:** `HealthScorecard` from `SingleDumpReportFormat.md` Step 1.  
**Trend addition:** each domain row shows severity at baseline and current, plus the direction of change.

| Column | Source |
|---|---|
| Domain | domain name string |
| Baseline severity | max `InsightFinding.Severity` in that domain from `snapshots[0].Findings` |
| Current severity | max severity from `snapshots[^1].Findings` |
| Change | Improved / Regressed / Stable / New |
| Critical count | count of Critical findings in current |
| Warning count | count of Warning findings in current |

Rendered as a compact comparison table above the executive summary. The "Change" column uses
directional indicators: `↑ Worse`, `↓ Better`, `= Stable`, `★ New domain`.

**Data assembly:**
- Baseline domain severity: computed from `trendData.Snapshots[0].Findings`, grouped by domain via `SectionIdDomainMap`.
- Current domain severity: same from `trendData.Snapshots[^1].Findings`.
- Direction: compare baseline vs current severity ordinal.

---

### T2. Trend Executive Summary

**Source:** `TrendReportDocument.ExecutiveSummary` (`ExecutiveSummaryRecord`)

**Sub-blocks:**

#### T2a. Lifecycle Summary

| Metric | Source |
|---|---|
| Dumps compared | `TrendDumpCount` |
| Snapshot window | `snapshots[0].GeneratedAtUtc` → `snapshots[^1].GeneratedAtUtc` |
| New findings | `trendData.NewFindings.Count` |
| Persistent findings | `trendData.PersistentFindings.Count` |
| Resolved findings | `trendData.ResolvedFindings.Count` |
| Net finding movement | `NewFindings.Count - ResolvedFindings.Count` |

Rendered as a metrics strip (same pattern as single-dump key metrics strip).

#### T2b. Score Deltas

| Score | Baseline | Current | Delta | Source |
|---|---|---|---|---|
| Leak likelihood | `first.LeakLikelihoodScore` | `last.LeakLikelihoodScore` | `ExecutiveSummary.LeakScoreDelta` | `ExecutiveSummaryRecord` |
| GC pressure | `first.GcPressureScore` | `last.GcPressureScore` | `ExecutiveSummary.GcPressureScoreDelta` | `ExecutiveSummaryRecord` |
| Thread contention | `first.ThreadContentionScore` | `last.ThreadContentionScore` | `ExecutiveSummary.ThreadContentionScoreDelta` | `ExecutiveSummaryRecord` |

Positive delta = worse. Color-coded: red for regression, green for improvement.

`ScoreBreakdowns` (from `ExecutiveSummaryRecord`) rendered as expandable detail behind each delta.

#### T2c. Top Regressions (headline)

Top 5 from `Findings` where tag `"regression"` is present, sorted by `Severity` desc then `MetricValue` abs desc.

Columns: Severity | Analyzer | Metric | Baseline | Current | Delta%  
← Source: `FindingRecord.EvidenceRefs[0].MetricKey`, `MetricValue`, `MetricUnit`.

#### T2d. Top Improvements (secondary)

Top 3 from `Findings` where tag `"improvement"` is present (if any).  
Same columns as T2c. Shown below regressions; collapsed by default in HTML.

---

### T3. Regression Dashboard

**Source:** `TrendRegressionDashboardBuilder.Build(trendData, snapshots)`.

Implemented as a dedicated `AnalyzerDetailSection` with `SectionId = "T3"`.

#### T3a. Severity Escalations

Source: `BuildSeverityEscalations(snapshots)` → `SeverityEscalationEntry[]`

Table: Analyzer | Finding Title | Baseline Severity | Current Severity  
Lead finding: Critical when any escalations exist.

#### T3b. New Findings Detail

Source: `trendData.NewFindings` (full `InsightFinding` list)

Table: Severity | Analyzer | Category | Title | Evidence | Recommendation | Confidence  
Sorted by Severity desc. Limit: top 20 in table; remainder noted as count.

#### T3c. New Leak Signals

Source: `trendData.NewLeakSignalsByAnalyzer` → `NewLeakSignal[]`  
Fields: `TypeName`, `BaselineBytes`, `CurrentBytes`, `Source`

Table: TypeName | Source Analyzer | Baseline | Current | Growth  
Growth = `(CurrentBytes - BaselineBytes) / BaselineBytes * 100`.  
Sorted by CurrentBytes desc. Cap: top 10.

---

### T4. Metric Timeline Section

**Source:** `trendData.Timeline` and `trendData.ScopedTimeline` (`IReadOnlyList<AnalyzerMetricTimeline>`)  
**Implementation:** `TrendMetricTimelineSectionBuilder.Build(trendData, snapshots)` as a dedicated `AnalyzerDetailSection` with `SectionId = "T4"`.

T4 is organized hierarchically to mirror single-dump structure:

- Domain
- Analyzer
- Metric-specific tables

Within each analyzer, T4 includes two table groups when data exists:

- Headline metric timelines (unscoped metrics) in a single analyzer-level table.
- Table comparisons (scoped/entity metrics), grouped per metric key.

Only table comparisons are metric-specific (one metric key per table).

Per-analyzer timeline table:

| Column | Source |
|---|---|
| Metric / Entity | Headline tables: `MetricTimelinePoint.Key`. Comparison tables: scope/entity label (`MetricTimelinePoint.Scope`) only; metric key is represented by the table caption |
| Trend (sparkline) | `MetricTimelinePoint.Values` — rendered as inline sparkline in HTML, as `baseline → … → current` in markdown |
| Dump 1..N | one column per snapshot value from `MetricTimelinePoint.Values`; rendered as compact directional chips (`↗/↘/→`) with the value text visible in each cell. `Dump 1` maps to snapshot index 0 and `Dump N` maps to the latest snapshot |
| Δ (delta) | last value − first value, formatted in `MetricTimelinePoint.Unit` |
| Δ% | `(last − first) / first * 100` |
| Pattern | derived movement signature across snapshots: Stable / Single jump / Gradual drift / Oscillating / Volatile |
| Status | Stable / Improvement / Regression / ⚠⚠ Severe |

Ordering:

- Domains follow `SectionIdDomainMap.DomainsInOrder`.
- Analyzers in each domain are ordered by regression count desc.
- Comparison metric tables in each analyzer are ordered by metric key.

T4 remains a single collapsible section but internally grouped by domain and analyzer headings.

Scoped comparison tables are emitted for any analyzer that provides scoped metrics.
Memory Analysis comparison tables are intentionally restricted to `type.bytes` and `type.count` (top-type comparisons).
Other analyzers include all scoped comparison metric keys they emit.
No artificial row cap is applied to comparison tables.
Comparison table first-column headers are context-aware (for example: `Type`, `Category`, `Kind`, `Module`, `Source`, `Target`, `Edge`, `Name`, fallback `Entity`).

Current scoped comparison coverage includes (non-exhaustive):

- Memory Analysis (`type.bytes`, `type.count` only)
- Crash Analysis (`crash.exception.type`)
- Hang Analysis (`hang.wait.category`)
- Thread Analysis (`thread.wait.category`)
- GC Generation Analysis (`gc.loh.type.*`)
- GC Handle Analysis (`gchandle.kind.*`, `gchandle.target.type.*`, `gchandle.pinned.type.*`)
- Dependent Handle Analysis (`dephandle.source/target/edge.type.*`)
- String Analysis (`string.duplicate.type.count`)
- Async Task Analysis (`task.pending/faulted/continuation.type.count`)
- Allocation Pattern Analysis (`alloc.transient/shortish/longlived.type.*`)
- Object Shape Analysis (`shape.ref/value.heavy.type.ratio`)
- GC Root Analysis (`gcroot.kind.count`, `gcroot.top.target.*`)
- Finalizable Object Analysis (`finalizable.type.gen2.count`, `finalizable.queue.type.retained.bytes`)
- Array Analysis (`array.type.bytes`, `array.type.count`)
- Module Analysis (`modules.heap.*`)
- Retention Analysis (`leak.retention.type.*`)
- Static Root Leak Detection (`static.root.byname.bytes`)
- Dominator Analysis (`dominator.type.bytes`)
- Leak Candidate Analysis (`leak.candidate.type.*`)
- DB Connection / WCF / HTTP / Timer analyzers (`*.type.*` scoped breakdowns)

Metadata per metric row: `MetricTrendDirection` (`HigherIsWorse` / `LowerIsWorse` / `Neutral`).
Metric cells use `TableCell.LinkTarget` with `detail-{snapshotIndex}` to jump to the most relevant snapshot section.

**Step movement** (between adjacent snapshot pairs): source is `MetricTimelinePoint.Values` and represented visually in Dump 1..N cells (direction marker per step), with movement summarized in `Pattern`.  
One step = one pair of adjacent snapshots. No separate Step-Delta table is required in HTML.

T4 MUST preserve intermediate snapshot detail using explicit dump-wise columns (`Dump 1..N`). Baseline/current-only presentation is not allowed.
T4 should prioritize visual readability over dense numeric text in HTML while still showing numeric values in each dump column.

---

### T5. Snapshot Strip

**Source:** `trendData.Snapshots` (`IReadOnlyList<AnalysisSnapshot>`)  
**Implementation:** `TrendSnapshotStripBuilder.Build(snapshots)` renders a dedicated `AnalyzerDetailSection` with `SectionId = "T5"`.

One compact row per snapshot rendered **before** the per-dump detail sections.

Per row:

| Field | Source |
|---|---|
| Index | `snapshot.Index + 1` of `TrendDumpCount` |
| Dump filename | `Path.GetFileName(snapshot.DumpPath)` |
| Generated (UTC) | `snapshot.GeneratedAtUtc` |
| Analyzer count | `snapshot.Runs.Count` |
| Finding count | `snapshot.Findings.Count` |
| Critical count | `snapshot.Findings.Count(f => f.Severity == Critical)` |
| Warning count | `snapshot.Findings.Count(f => f.Severity == Warning)` |
| Total bytes | `MemoryDomainResult.TotalBytes` when available |
| Δ vs baseline | `%` change of total bytes vs snapshot 0 (when available) |
| Role | `Baseline` / `Intermediate` / `Current` derived from index |
| Anchor | `detail-{snapshot.Index}` (stable link target) |

Rendered as a compact table in canonical output and HTML, with dump names linked to `#detail-{index}`.

---

### T6. Per-Dump Detail Sections (SnapshotDetails)

**Source:** `BuildPerDumpSections(snapshots, builders, audience)` via `TrendSnapshotSectionComposer.Build()`

Each snapshot produces one `AnalyzerDetailSection`:
- `AnalyzerName`: `"Snapshot {index+1}: {filename}"`
- `SortOrder`: `index * 10 + 200`
- `SectionId`: `"detail-{index}"` ← stable anchor matching snapshot strip links
- `Domain`: `"SnapshotDetail"`

Each snapshot section contains:
- **Dump header block:** path, timestamp, finding count, elapsed, incident context key metrics.
- **Findings list** (top N, sorted severity desc).
- **Per-analyzer detail blocks** (from `SingleDumpReportFormat.md` domain sections), each wrapped in `CollapsibleSectionBeginBlock` / `CollapsibleSectionEndBlock`.

Collapsed by default in HTML. Expanding a snapshot section shows the full single-dump domain sections for that snapshot.

Current `TrendSnapshotSectionComposer` includes `SectionId = "detail-{index}"` and emits a `Snapshot Key Metrics` table with `Δ vs Baseline` when snapshot data is available.

Target metric set for snapshot header block (partially implemented):

| Metric | Source |
|---|---|
| Total managed bytes | `MemoryDomainResult.TotalBytes` from `snapshot.DomainResults` |
| Gen2 % | `GCGenerationDomainResult.Gen2Pct` |
| GC pressure | `AllocationPatternDomainResult.GCPressure` |
| Leak candidates | `LeakCandidateDomainResult.TotalCandidates` |
| Blocked threads | `ThreadDomainResult.BlockedThreadCount` |
| Deadlock cycles | `LockGraphDomainResult.DeadlockCandidateCount` |
| Active exceptions | `CrashDomainResult.ActiveExceptions` |
| Finalizer queue | `FinalizableObjectDomainResult.FinalizerQueueCount` |

Currently emitted in `Snapshot Key Metrics`: Total managed bytes, Gen2 %, leak candidates, blocked threads, active exceptions, finalizer queue (each with Δ vs baseline when baseline exists).
Not yet emitted here: GC pressure, deadlock cycles.

---

### T7. Trend Appendix

#### T7a. Resolved Findings

Source: `trendData.ResolvedFindings`

Table: Severity | Analyzer | Category | Title  
Note: findings that were present in baseline but absent in current. Collapsed in HTML.

#### T7b. Analyzer Coverage Map

Source: `trendData.Snapshots` — for each snapshot, which analyzers completed/failed/skipped.

Table:

| Analyzer | S1 | S2 | … | SN |
|---|---|---|---|---|
| MemoryAnalyzer | ✅ | ✅ | | ✅ |
| LohFragmentationAnalyzer | ✅ | ⚠️ | | ✅ |
| LeakCandidateAnalyzer | ✅ | ✅ | | ✅ |

Legend: ✅ Completed / ⚠️ Failed / ⏭ Skipped / — Not run  
Source: `snapshot.Runs` per snapshot, keyed by `AnalyzerName` and `Status`.

#### T7c. Analyzer Run Summary (current dump)

Same as single-dump Appendix Z1 — current dump's `AnalyzerRunResult[]`.

#### T7d. Trend Limitations

| Limitation | Scope |
|---|---|
| Lifecycle comparison is fingerprint-based; renamed findings are treated as new | T3b |
| Score deltas require at least 2 snapshots with successful ExecutiveSummary builds | T2b |
| New type detection is capped to top-N memory types per snapshot | T3c |
| Severity escalations only track Warning → Critical; Info → Warning not flagged | T3a |
| Table-comparison rows are capped per analyzer to keep T4 readable; remaining rows are summarized as hidden count | T4 |
| Step movement is summarized visually in timeline cells and Pattern labels; exact adjacent deltas are not printed as a separate table in HTML | T4 |
| Snapshot detail sections reuse current-dump section builders; no cross-snapshot diff within section tables | T6 |

---

## Section Ordering

```
T0  Trend Header                     (always first)
T1  Trend Health Scorecard           (always second)
T2  Trend Executive Summary          (always third)
T3  Regression Dashboard             (before timelines; omitted if no regressions)
T4  Metric Timeline                  (single collapsible section; omitted if no history)
T5  Snapshot Strip                   (compact table; always present)
T6  Per-Dump Details [0..N]          (collapsed by default)
T7  Trend Appendix                   (always last)
```

Sections T3 and T4 are omitted entirely (not rendered as empty sections) when their source data is empty.

---

## Rendering Rules

### HTML
- T0–T2 are always visible on load.
- T3 (Regression Dashboard) is visible when any regressions exist; collapsed when no regressions.
- T4 (Metric Timeline) is a single collapsible section containing per-analyzer tables.
- T5 (Snapshot Strip) is always visible; dump links scroll to `#detail-{index}`.
- T6 (Per-Dump Details) are fully collapsed; each dump section has "Expand" toggle.
- `IsTrendReport = true` must gate any rendering logic that differs from single-dump mode.
- Sparklines in T4: rendered client-side from `__SPARK__{json}` token in `TableCell.Display`.

### Markdown
- T5 Snapshot Strip: compact table (no cards).
- T4 sparklines: replaced by `baseline → [mid] → current` text.
- T6 per-dump sections: rendered as level-3 headings with collapsing not available; top-N findings only.
- T4 step movement in markdown: summarize via Pattern and dump-wise columns; avoid per-step flat tables by default.

### JSON
- Section ordering in `AnalyzerSections` follows the T0–T7 order.
- All lists are fully emitted (no top-N truncation).
- `TrendDumpCount`, `TrendDumpPaths`, and `IncidentContext.TrendSnapshots` are required.
- `HealthScorecard` (trend-extended) is the second key after header fields.

---

## Stable Section Anchors

| Section | `SectionId` | HTML anchor |
|---|---|---|
| Trend Header | `T0` | `#trend-header` |
| Trend Scorecard | `T1` | `#trend-scorecard` |
| Trend Executive | `T2` | `#trend-executive` |
| Regression Dashboard | `T3` | `#trend-regressions` |
| Metric Timeline | `T4` | `#trend-timeline` |
| Snapshot Strip | `T5` | `#trend-snapshots` |
| Snapshot Detail N | `detail-{N}` | `#detail-{N}` |
| Trend Appendix | `T7` | `#trend-appendix` |
