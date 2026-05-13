# Trend Report Format

## Purpose

Define the composition, section map, and rendering rules for the trend (compare-mode) report.
This document is the authoritative schema spec for all trend renderers (HTML, JSON, markdown).
It works alongside `SingleDumpReportFormat.md` — the single-dump section map applies to each
per-snapshot detail block within this document.

The narrative contract and design principles live in `TrendReportBlueprint.md`.
The data availability audit lives in `ProfessionalTierReport.md` §14.

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
├── SnapshotStrip               one compact card per snapshot
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

**Current implementation:** embedded in `TrendReportDocument` top-level fields.  
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

**Source:** `TrendReportComposer` output blocks — currently inline in the `"Trend Comparison"` `AnalyzerDetailSection`.

This must be a **dedicated section** with `SectionId = "T3"`, separate from the timeline section.

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

**Source:** `trendData.Timeline` (`IReadOnlyList<AnalyzerMetricTimeline>`)  
**Current implementation:** part of `BuildTrendComparisonSection` — must be extracted as a distinct `AnalyzerDetailSection` with `SectionId = "T4"`.

One sub-section per analyzer with at least one non-stable metric point.

Per-analyzer timeline table:

| Column | Source |
|---|---|
| Metric | `MetricTimelinePoint.Key` |
| Trend (sparkline) | `MetricTimelinePoint.Values` — rendered as inline sparkline in HTML, as `baseline → … → current` in markdown |
| Δ (delta) | last value − first value, formatted in `MetricTimelinePoint.Unit` |
| Δ% | `(last − first) / first * 100` |
| Status | Stable / Improvement / Regression / ⚠⚠ Severe |

Ordering: analyzers with regressions first (by regression count desc).  
Each analyzer sub-table is collapsed by default in HTML.

Metadata per metric row: `MetricTrendDirection` (`HigherIsWorse` / `LowerIsWorse` / `Neutral`).

The `__LINK__detail-{snapshotIndex}` token in the metric cell (currently embedded in the table cell display string) must become a proper `TableCell.LinkTarget` field — see Step 2 of implementation plan.

**Step deltas** (between adjacent snapshot pairs): source is `trendData.Steps` (`IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>>`).  
One step = one pair of adjacent snapshots. Steps are currently not rendered — they should appear as an expandable sub-table per analyzer showing per-step movement, not just overall baseline→current.

Per-step table (collapsed):

| Step | Baseline Dump | Current Dump | Δ | Δ% | Severity |
|---|---|---|---|---|---|
| 1→2 | path[0] | path[1] | value | % | Minor/Moderate/Severe |

Source: `steps[stepIndex]` where each element is `IReadOnlyList<AnalyzerTrendResult>` for that step.

---

### T5. Snapshot Strip

**Source:** `trendData.Snapshots` (`IReadOnlyList<AnalysisSnapshot>`)  
**Current implementation:** not explicitly rendered as a strip — snapshots appear only as full per-dump sections.

One compact card per snapshot rendered **before** the per-dump detail sections.

Per card:

| Field | Source |
|---|---|
| Index | `snapshot.Index + 1` of `TrendDumpCount` |
| Dump filename | `Path.GetFileName(snapshot.DumpPath)` |
| Generated (UTC) | `snapshot.GeneratedAtUtc` |
| Analyzer count | `snapshot.DomainResults.Count` |
| Finding count | `snapshot.Findings.Count` |
| Critical count | `snapshot.Findings.Count(f => f.Severity == Critical)` |
| Warning count | `snapshot.Findings.Count(f => f.Severity == Warning)` |
| Is baseline | `TrendSnapshotContext.IsBaseline` |
| Is current | `TrendSnapshotContext.IsCurrent` |
| Anchor | `detail-{snapshot.Index}` (stable link target) |

Rendered as a horizontal row of cards in HTML, as a compact table in markdown/JSON.

In HTML: each card is a clickable link to `#detail-{index}`. Active card (current) has distinct styling.

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

**Missing from current `TrendSnapshotSectionComposer`:**
- `SectionId = "detail-{index}"` not set (currently no `SectionId` field on sections — added in single-dump implementation Step 1.2).
- Per-snapshot health scorecard card (from T5) is not embedded in the snapshot section header.
- Snapshot key metrics strip (TotalBytes, Gen2%, BlockedThreads etc.) is not emitted — only `incidentContext` fields are shown.

Per-snapshot key metrics to add to snapshot header block:

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

Each metric also shows a Δ vs baseline (computed by comparing snapshot[0] and snapshot[i] values).

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
| Metric timelines only cover headline (unscoped) metrics; per-type trends not tracked | T4 |
| Step deltas reflect adjacent-pair movement only; non-monotonic trends are visible only in the full timeline | T4 |
| Snapshot detail sections reuse current-dump section builders; no cross-snapshot diff within section tables | T6 |

---

## Section Ordering

```
T0  Trend Header                     (always first)
T1  Trend Health Scorecard           (always second)
T2  Trend Executive Summary          (always third)
T3  Regression Dashboard             (before timelines; omitted if no regressions)
T4  Metric Timeline                  (collapsed analyzers; omitted if no history)
T5  Snapshot Strip                   (compact cards; always present)
T6  Per-Dump Details [0..N]          (collapsed by default)
T7  Trend Appendix                   (always last)
```

Sections T3 and T4 are omitted entirely (not rendered as empty sections) when their source data is empty.

---

## Rendering Rules

### HTML
- T0–T2 are always visible on load.
- T3 (Regression Dashboard) is visible when any regressions exist; collapsed when no regressions.
- T4 (Metric Timeline) tables are collapsed per-analyzer.
- T5 (Snapshot Strip) cards are always visible; clicking a card scrolls to `#detail-{index}`.
- T6 (Per-Dump Details) are fully collapsed; each dump section has "Expand" toggle.
- `IsTrendReport = true` must gate any rendering logic that differs from single-dump mode.
- Sparklines in T4: rendered client-side from `__SPARK__{json}` token in `TableCell.Display`, or from a dedicated `SparklineBlock` (see implementation plan Step 3).

### Markdown
- T5 Snapshot Strip: compact table (no cards).
- T4 sparklines: replaced by `baseline → [mid] → current` text.
- T6 per-dump sections: rendered as level-3 headings with collapsing not available; top-N findings only.
- Step deltas in T4: flat table, one row per step.

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
