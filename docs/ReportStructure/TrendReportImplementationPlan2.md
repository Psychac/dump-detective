# Trend Report Implementation Plan

## Overview

This plan implements the format defined in `TrendReportFormat.md`. It extends the single-dump
implementation (Steps 1–13 in `SingleDumpReportImplementationPlan.md`) which must be completed
first — this plan depends on `SectionId`, `Domain`, `HealthScorecard`, and `ConfidenceBandBlock`
being available.

Changes are organized by the T0–T7 section order and by dependency.

---

## Prerequisites (from single-dump plan)

The following must be done before any step here:

| Single-dump step | What this plan depends on |
|---|---|
| Step 1 | `HealthScorecard` model, `SectionId`/`Domain` on `AnalyzerDetailSection`, `SectionIdDomainMap` |
| Step 2 | Domain severity ordering logic (reused here for trend scorecard) |
| Step 12 | `ReportJsonContext` updated for new types |

---

## Step T1 — Trend Health Scorecard

**Goal:** Add per-domain severity-change comparison (baseline vs current) to the scorecard.

### T1.1 Extend `HealthScorecard` model — `AnalysisReportDocument.cs`

```csharp
internal enum DomainSeverityChange { Stable, Improved, Regressed, NewDomain, Removed }

internal sealed record TrendDomainHealthEntry(
    string Domain,
    DomainSeverity BaselineSeverity,
    DomainSeverity CurrentSeverity,
    DomainSeverityChange Change,
    int CurrentCriticalCount,
    int CurrentWarningCount);

internal sealed record TrendHealthScorecard(
    IReadOnlyList<TrendDomainHealthEntry> Domains,
    DomainSeverity OverallBaselineSeverity,
    DomainSeverity OverallCurrentSeverity,
    DomainSeverityChange OverallChange) : HealthScorecard(
        Domains: [],          // base Domains unused in trend mode
        OverallSeverity: DomainSeverity.Unknown);
```

Alternatively extend `HealthScorecard` with nullable baseline fields rather than a subtype — simpler, avoids JSON polymorphism.

**Recommended:** extend `DomainHealthEntry` with nullable baseline fields:

```csharp
internal sealed record DomainHealthEntry(
    string Domain,
    DomainSeverity Severity,
    int FindingCount,
    int CriticalCount,
    int WarningCount,
    // Trend-mode additions (null in single-dump mode):
    DomainSeverity? BaselineSeverity = null,
    DomainSeverityChange? Change = null);
```

### T1.2 New service — `TrendHealthScorecardBuilder.cs` (new file in `Services/`)

```
Input:
  IReadOnlyList<InsightFinding> baselineFindings   (from snapshots[0].Findings)
  IReadOnlyList<InsightFinding> currentFindings    (from snapshots[^1].Findings)
Output: HealthScorecard (with BaselineSeverity and Change populated on each DomainHealthEntry)
```

Algorithm:
1. Compute domain severity for baseline and current using `SectionIdDomainMap` (same as `HealthScorecardBuilder`).
2. For each domain: determine `DomainSeverityChange`:
   - Both absent → skip
   - Absent in baseline, present in current → `NewDomain`
   - Present in baseline, absent in current → `Removed`
   - Current severity > baseline severity → `Regressed`
   - Current severity < baseline severity → `Improved`
   - Equal → `Stable`

### T1.3 Wire in `TrendReportComposer.ComposeCanonicalTrendReport()`

Replace the plain `HealthScorecard` build (from single-dump Step 1.5) with `TrendHealthScorecardBuilder.Build()` when composing a trend document.

**Files changed:** `AnalysisReportDocument.cs`, `Services/TrendReportComposer.cs`  
**Files added:** `Services/TrendHealthScorecardBuilder.cs`

---

## Step T2 — Trend Executive Summary Extensions

**Goal:** Surface score deltas, lifecycle counts, and top-regression rows in the executive summary block.

### T2.1 Extend `ExecutiveSummaryRecord` rendering — `ExecutiveSummarySectionBuilder.cs`

The `ExecutiveSummaryRecord` already has `LeakScoreDelta`, `GcPressureScoreDelta`, `ThreadContentionScoreDelta`. What is missing is rendering them in the section builder.

**Add when `IsTrendReport = true`:**

```
HeadingBlock("Score Movement (Baseline → Current)")
TableBlock("Score Deltas",
    Headers: ["Dimension","Baseline","Current","Delta","Direction"],
    Rows: [
        ("Leak Likelihood",  first.LeakLikelihoodScore, last.LeakLikelihoodScore,  delta, arrow),
        ("GC Pressure",      first.GcPressureScore,     last.GcPressureScore,      delta, arrow),
        ("Thread Contention",first.ThreadContentionScore,last.ThreadContentionScore,delta, arrow),
    ])
```

Delta arrow: ↑ Worse (red) / ↓ Better (green) / = Stable.

Also render `ScoreBreakdowns` per dimension as expandable sub-tables:
```
CollapsibleSectionBeginBlock("Score Breakdown — {Dimension}")
    TableBlock(Headers: ["Signal","Source","Points","Detail"], Rows: from Contributors)
CollapsibleSectionEndBlock()
```

### T2.2 Lifecycle summary block — `ExecutiveSummarySectionBuilder.cs`

**Add when `IsTrendReport = true`:**

```
HeadingBlock("Finding Lifecycle")
MetricBlock("Dumps compared",    TrendDumpCount)
MetricBlock("Snapshot window",   "{from} → {to}")
MetricBlock("New findings",      NewFindings.Count)
MetricBlock("Persistent",        PersistentFindings.Count)
MetricBlock("Resolved",          ResolvedFindings.Count)
MetricBlock("Net movement",      NewFindings.Count - ResolvedFindings.Count, sign-annotated)
```

The finding counts come from `trendData.NewFindings.Count` etc. — they must be stored on the
`TrendReportDocument` directly or on a new `TrendSummaryRecord`:

```csharp
// Add to TrendReportDocument:
public int TrendNewFindingCount { get; init; }
public int TrendPersistentFindingCount { get; init; }
public int TrendResolvedFindingCount { get; init; }
```

Populate from `trendData.NewFindings.Count` etc. in `TrendReportComposer`.

### T2.3 Top regressions in executive — `ExecutiveSummarySectionBuilder.cs`

The trend `Findings` list already contains regression findings (tagged `"regression"`).
Filter them and emit:

```
HeadingBlock("Top Regressions")
TableBlock("Top Metric Regressions",
    Headers: ["Severity","Analyzer","Metric","From","To","Δ%"],
    Rows: from Findings where Tags contains "regression", sorted Severity desc, take 5)
```

For each regression finding, the metric values come from `FindingRecord.EvidenceRefs[0].MetricKey`
and `MetricValue` / `MetricUnit`. Currently `MetricValue` stores the delta percent.
To show "From" and "To" values separately, extend `FindingRecord`:

```csharp
// Add to FindingRecord partial:
public double? MetricBaseline { get; init; } = null;
public double? MetricCurrent  { get; init; } = null;
public string? MetricUnit     { get; init; } = null;    // already exists — ensure populated
```

Populate `MetricBaseline` and `MetricCurrent` in `TrendReportComposer.BuildTopRegressionFindings()`
from `delta.Baseline` and `delta.Current`.

**Files changed:** `AnalysisReportDocument.cs`, `Models/AnalysisReportDocument.cs` (FindingRecord partial), `SectionBuilders/ExecutiveSummarySectionBuilder.cs`, `Services/TrendReportComposer.cs`

---

## Step T3 — Regression Dashboard Section

**Goal:** Extract the regression, new-findings, severity-escalation, and new-leak-signal blocks from `BuildTrendComparisonSection()` into a dedicated `AnalyzerDetailSection` with `SectionId = "T3"`.

### T3.1 New section builder — `TrendRegressionDashboardBuilder.cs` (new file in `Services/`)

Called from `TrendReportComposer.ComposeCanonicalTrendReport()` instead of including these blocks in the general comparison section.

Input:
- `trendData.NewFindings` (`IReadOnlyList<InsightFinding>`)
- Severity escalations (`BuildSeverityEscalations(snapshots)`)
- New leak signals (`trendData.NewLeakSignalsByAnalyzer`)

Output: `AnalyzerDetailSection` with:
- `AnalyzerName = "TrendRegressionDashboard"`
- `SectionId = "T3"`
- `Domain = "Trend"`
- `SortOrder = 30` (after scorecard and executive, before timeline)

Blocks:

```
HeadingBlock("Regression Dashboard")

// T3a — Severity Escalations
HeadingBlock("Severity Escalations", 1)
if any escalations:
    TableBlock(
        Caption: "Findings that escalated from Warning to Critical",
        Headers: ["Analyzer","Title","Baseline","Current"],
        Rows: from SeverityEscalationEntry list)
else:
    TextBlock("No severity escalations detected.")

// T3b — New Findings
HeadingBlock("New Findings", 1)
TableBlock(
    Caption: "Findings present in current dump but absent in baseline",
    Headers: ["Severity","Analyzer","Category","Title","Evidence","Confidence"],
    Rows: from NewFindings sorted Severity desc, capped at 20)
if NewFindings.Count > 20:
    TextBlock($"{NewFindings.Count - 20} additional new findings not shown.")

// T3c — New Leak Signals
HeadingBlock("New Leak Signals", 1)
if any signals:
    TableBlock(
        Caption: "Types newly appearing or significantly growing in leak candidates",
        Headers: ["TypeName","Source Analyzer","Baseline","Current","Growth%"],
        Rows: from NewLeakSignal list, sorted CurrentBytes desc, cap 10)
else:
    TextBlock("No new leak signals detected.")
```

### T3.2 Remove from `BuildTrendComparisonSection()`

Remove the NEW FINDINGS, NEW TYPES, SEVERITY ESCALATIONS, and NEW LEAK SIGNALS blocks from
`BuildTrendComparisonSection()`. The comparison section retains only: lifecycle metrics and the
metric timeline table (which moves to T4 section builder).

**Files changed:** `Services/TrendReportComposer.cs`  
**Files added:** `Services/TrendRegressionDashboardBuilder.cs`

---

## Step T4 — Metric Timeline Section

**Goal:** Extract metric timeline content from `BuildTrendComparisonSection()` into a dedicated section.
Add per-step sub-tables. Fix `__LINK__` token into a proper `TableCell` field.

### T4.1 Add `LinkTarget` to `TableCell` — `AnalyzerDetailSection.cs`

```csharp
internal sealed record TableCell(
    string Display,
    long? RawValue = null,
    string? LinkTarget = null);   // ADD — stable anchor, e.g. "detail-2"
```

Update renderers:
- HTML: when `LinkTarget != null`, wrap `Display` in `<a href="#{LinkTarget}">{Display}</a>`.
- Markdown: append ` (→ #{LinkTarget})` or omit if not meaningful.
- JSON: serialize as-is.

Remove the `||__LINK__detail-{n}` string concatenation from `TrendReportComposer` — use
`TableCell(Display: point.Key, LinkTarget: $"detail-{linkSnapshot}")` instead.

### T4.2 Add `SparklineBlock` to `AnalyzerDetailSection.cs`

```csharp
[JsonDerivedType(typeof(SparklineBlock), "sparkline")]
...
internal sealed record SparklineBlock(
    string MetricKey,
    string Unit,
    IReadOnlyList<double> Values,
    string Direction) : SectionBlock;  // Direction: "HigherIsWorse" | "LowerIsWorse" | "Neutral"
```

Renderers:
- HTML: render client-side as inline SVG sparkline from `Values`.
- Markdown: `{firstVal} → {midCount-truncated} → {lastVal}` text.
- JSON: serialize as structured data (client can render however it wants).

Remove the `__SPARK__{json}` string token from `TrendReportComposer` — use `SparklineBlock` instead.
The timeline table row for each metric becomes two rows (SparklineBlock + TableRow) or a paired structure.

**Simplest approach:** keep the `TableBlock` for the metric rows; insert a `SparklineBlock` immediately before each metric's `TableRow` to pair them visually. The HTML renderer renders them as adjacent cells or a combined row.

### T4.3 New section builder — `TrendMetricTimelineSectionBuilder.cs` (new file in `Services/`)

Input: `trendData.Timeline`, `trendData.Steps`, `trendData.Overall`, `snapshots`  
Output: `AnalyzerDetailSection` with `SectionId = "T4"`, `Domain = "Trend"`, `SortOrder = 40`.

Algorithm (extracted from `BuildTrendComparisonSection()`):
- For each `AnalyzerMetricTimeline` in `timeline` (ordered by regression count desc):
  - Build the per-metric rows as before (with `TableCell.LinkTarget` instead of `||__LINK__`).
  - Add a collapsible per-step sub-section:

```
CollapsibleSectionBeginBlock("{analyzerName} — Step-by-Step Δ")
    TableBlock("Step Deltas",
        Headers: ["Step","From Dump","To Dump","Metric","Δ","Δ%","Severity"],
        Rows: from steps[stepIndex] where analyzerName matches)
CollapsibleSectionEndBlock()
```

Step source: `trendData.Steps[stepIdx]` contains `IReadOnlyList<AnalyzerTrendResult>` for the pair
`(snapshots[stepIdx], snapshots[stepIdx+1])`. Filter by `AnalyzerName` to get metrics for this analyzer.

If `trendData.Timeline` is empty (only 1 snapshot or no comparer registered), omit the section.

### T4.4 Remove from `BuildTrendComparisonSection()`

After extracting, `BuildTrendComparisonSection()` retains only the lifecycle summary metrics block.
Move the remaining lifecycle metrics to the T2 executive summary (Step T2.2 above) and retire
`BuildTrendComparisonSection()` entirely, replacing it with the three dedicated builders (T2, T3, T4).

**Files changed:** `AnalyzerDetailSection.cs`, `Services/TrendReportComposer.cs`, HTML/markdown/JSON renderers  
**Files added:** `Services/TrendMetricTimelineSectionBuilder.cs`

---

## Step T5 — Snapshot Strip Section

**Goal:** Render a compact card strip for all snapshots before the per-dump detail sections.

### T5.1 New section builder — `TrendSnapshotStripBuilder.cs` (new file in `Services/`)

Input: `trendData.Snapshots`, `trendData.Snapshots[0].DomainResults` (for baseline metric values)

Output: `AnalyzerDetailSection` with `SectionId = "T5"`, `Domain = "Trend"`, `SortOrder = 50`.

Blocks:
```
HeadingBlock("Snapshot Overview")
TableBlock("Snapshots",
    Headers: ["#","Dump","Generated (UTC)","Analyzers","Findings","Critical","Warning","Total Bytes","Δ vs Baseline","Role"],
    Rows: one per snapshot)
```

Per row:
- `#`: `snapshot.Index + 1`
- Dump: `Path.GetFileName(snapshot.DumpPath)` with `LinkTarget = $"detail-{snapshot.Index}"`
- Generated: `snapshot.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")`
- Analyzers: `snapshot.DomainResults.Count`
- Findings / Critical / Warning: from `snapshot.Findings`
- Total Bytes: `MemoryDomainResult.TotalBytes` from `snapshot.DomainResults` if available (look up `"MemoryAnalyzer"` key)
- Δ vs Baseline: `(currentBytes - baselineBytes) / baselineBytes * 100` — `"—"` for snapshot 0
- Role: `"Baseline"` / `"Current"` / `"Intermediate"` (from `TrendSnapshotContext`)

In HTML: render as a horizontal card strip in addition to (or instead of) the table, using CSS.

**Files added:** `Services/TrendSnapshotStripBuilder.cs`

---

## Step T6 — Per-Snapshot Key Metrics & Delta in Snapshot Sections

**Goal:** Extend `TrendSnapshotSectionComposer.Build()` to include:
1. A `SectionId` of `"detail-{index}"` (anchor for links from T4 and T5).
2. A per-snapshot key metrics strip with Δ vs baseline.

### T6.1 Set `SectionId` on snapshot sections — `TrendSnapshotSectionComposer.cs`

```csharp
return new AnalyzerDetailSection(
    AnalyzerName: title,
    DisplayTitle: title,
    SortOrder: dumpIndex * 10 + 200,
    Blocks: blocks,
    SectionId: $"detail-{dumpIndex}",    // ADD
    Domain: "SnapshotDetail");            // ADD
```

### T6.2 Add per-snapshot key metrics block — `TrendSnapshotSectionComposer.cs`

Helper: `ExtractKeyMetrics(snapshot)` — reads from `snapshot.DomainResults` using the same
domain result type lookups used in the single-dump executive summary (Step 7 of single-dump plan).

For snapshots[0] (baseline): show absolute values only.  
For snapshots[i > 0]: show absolute values + Δ vs baseline.

```
HeadingBlock("Key Metrics")
TableBlock("Snapshot Key Metrics",
    Headers: ["Metric","Value","Δ vs Baseline"],
    Rows: [
        ("Total Bytes",      format(bytes),       Δ),
        ("GC Pressure",      pressure.ToString(), Δ or "—"),
        ("Gen2 %",           pct,                 Δ),
        ("Leak Candidates",  count,               Δ),
        ("Blocked Threads",  count,               Δ),
        ("Deadlock Cycles",  count,               Δ),
        ("Active Exceptions",count,               Δ),
        ("Finalizer Queue",  count,               Δ),
    ])
```

Δ values: computed by comparing to `snapshots[0]`'s domain result for the same metric.
String format for Δ: `+N` (red for worse), `-N` (green for better), `—` for baseline or no data.

**Files changed:** `Services/TrendSnapshotSectionComposer.cs`

---

## Step T7 — Trend Appendix Section

**Goal:** Add resolved findings, analyzer coverage map, and trend limitations as a dedicated appendix section.

### T7.1 New section builder — `TrendAppendixBuilder.cs` (new file in `Services/`)

Input: `trendData.ResolvedFindings`, `trendData.Snapshots`  
Output: `AnalyzerDetailSection` with `SectionId = "T7"`, `Domain = "Trend"`, `SortOrder = 9999`.

Blocks:

**T7a — Resolved Findings:**
```
CollapsibleSectionBeginBlock("Resolved Findings ({count})")
    TableBlock(
        Headers: ["Severity","Analyzer","Category","Title"],
        Rows: from ResolvedFindings sorted Severity desc)
CollapsibleSectionEndBlock()
```

**T7b — Analyzer Coverage Map:**

For each analyzer name across all snapshots:
```
CollapsibleSectionBeginBlock("Analyzer Coverage Map")
    TableBlock(
        Headers: ["Analyzer", "S1", "S2", ..., "SN"],
        Rows: one row per analyzer,
              cell = ✅/⚠️/⏭/— based on snapshot.Runs status)
CollapsibleSectionEndBlock()
```

Algorithm:
1. Collect union of all `AnalyzerName` values across all `snapshot.Runs`.
2. For each analyzer + snapshot pair: look up `AnalyzerRunResult.Status` from `snapshot.Runs`.
3. Status symbols: `Success` → ✅, `Failed` → ⚠️, `SkippedByFilter` → ⏭, `SkippedByCancellation` → ⏭, absent → `—`.

**T7c — Current Dump Analyzer Run Summary:**

Same as single-dump Appendix Z1, but scope label is "Current Dump":
```
HeadingBlock("Current Dump Analyzer Summary")
TableBlock(same columns as single-dump Z1)
```

**T7d — Trend Limitations:**
```
HeadingBlock("Trend Analysis Limitations")
TableBlock(
    Headers: ["Limitation","Affected Sections"],
    Rows: from TrendReportFormat.md §T7d table)
```

**Files added:** `Services/TrendAppendixBuilder.cs`

---

## Step T8 — Wire All New Sections in `TrendReportComposer`

Replace the existing section assembly in `ComposeCanonicalTrendReport()`:

**Current (to be replaced):**
```csharp
analyzerSections.Add(BuildTrendComparisonSection(...));  // monolithic
analyzerSections.AddRange(BuildPerDumpSections(...));
```

**New:**
```csharp
// T2 lifecycle/delta blocks are added to ExecutiveSummary (rendered by ExecutiveSummarySectionBuilder)

// T3 — Regression Dashboard
if (trendData.NewFindings.Count > 0 || escalations.Count > 0 || leakSignals.Count > 0)
    analyzerSections.Add(TrendRegressionDashboardBuilder.Build(trendData, escalations));

// T4 — Metric Timeline
if (trendData.Timeline.Count > 0)
    analyzerSections.Add(TrendMetricTimelineSectionBuilder.Build(trendData, snapshots));

// T5 — Snapshot Strip
analyzerSections.Add(TrendSnapshotStripBuilder.Build(trendData.Snapshots));

// T6 — Per-dump details (existing, but now with SectionId)
analyzerSections.AddRange(BuildPerDumpSections(...));

// T7 — Trend Appendix
analyzerSections.Add(TrendAppendixBuilder.Build(trendData, currentRuns));
```

Also: set `TrendNewFindingCount`, `TrendPersistentFindingCount`, `TrendResolvedFindingCount` on `TrendReportDocument`.

**Files changed:** `Services/TrendReportComposer.cs`, `Models/AnalysisReportDocument.cs`

---

## Step T9 — Update `FindingRecord` for Trend Regression Baseline/Current

Add `MetricBaseline` and `MetricCurrent` to `FindingRecord` and populate them in `BuildTopRegressionFindings()`.

```csharp
// In FindingRecord partial:
public double? MetricBaseline { get; init; } = null;
public double? MetricCurrent  { get; init; } = null;
```

In `BuildTopRegressionFindings()`:
```csharp
findings.Add(new InsightFinding(...) { MetricValue = delta.DeltaPercent ?? delta.Delta, ... });
// When mapped to FindingRecord via MapFinding():
// Set MetricBaseline = delta.Baseline, MetricCurrent = delta.Current
```

Update `MapFinding()` to accept an optional `MetricDelta` parameter and populate the new fields.

**Files changed:** `Models/AnalysisReportDocument.cs`, `Services/TrendReportComposer.cs`

---

## Step T10 — Renderer Updates

### T10.1 Markdown renderer — `CanonicalReportFormatter.cs`

- Detect `TrendReportDocument` (`$kind == "trend"`) and render T0–T7 in order.
- For T4 sparklines: use `SparklineBlock` → text format `{first} → [{n} points] → {last}`.
- For T5 snapshot strip: render as compact markdown table.
- For T6 per-dump sections: render as `### Dump N: {filename}` headings.
- For `TableCell.LinkTarget`: append `(→ #{LinkTarget})` inline.

### T10.2 HTML renderer — `HtmlReportRenderer.cs` / JS template

- `IsTrendReport` flag from `IncidentContext.IsTrendReport` (already present in JSON) gates trend layout.
- Add CSS classes for trend sections: `.trend-scorecard`, `.trend-executive`, `.snapshot-strip`, `.snapshot-card`, `.snapshot-card--baseline`, `.snapshot-card--current`.
- `SparklineBlock` rendered as `<svg>` or `<canvas>` element from `Values` array.
- `TableCell.LinkTarget` → `<a href="#{LinkTarget}">` wrapping the display text.
- Snapshot strip (T5): horizontal flex/grid row of cards, each card linking to `#detail-{n}`.
- T6 sections: `<details id="detail-{n}"><summary>{title}</summary>` elements.

### T10.3 JSON renderer — `JsonCanonicalReportFormatter.cs` / `ReportJsonContext.cs`

Register new types:
```csharp
[JsonSerializable(typeof(SparklineBlock))]
[JsonSerializable(typeof(TrendDomainHealthEntry))]
```

Ensure `TrendReportDocument` new fields serialize:
```csharp
[JsonSerializable(typeof(TrendReportDocument))]
```

---

## Step T11 — `TrendReportBlueprint.md` Update

Once T0–T10 are implemented, update `TrendReportBlueprint.md`:
- Replace "Required Trend Blocks" bullet list with a reference to `TrendReportFormat.md §Section Map`.
- Mark "Non-Goals" section with implementation status.
- Update "Acceptance" criteria with links to the stable section anchors.

---

## Implementation Order (dependency graph)

```
T1  (Trend Scorecard model + builder)
  └── requires Single-dump Step 1 (HealthScorecard model, SectionIdDomainMap)

T2  (Executive summary extensions)
  └── requires Single-dump Step 7 (ExecutiveSummaryRecord extensions)
  └── requires T9 (MetricBaseline/MetricCurrent on FindingRecord)

T3  (Regression Dashboard section builder)
  └── independent of T4, T5, T6

T4  (Metric Timeline section builder)
  └── T4.1 TableCell.LinkTarget — required by T5 (snapshot strip links)
  └── T4.2 SparklineBlock — required by T10.2 (HTML sparklines)

T5  (Snapshot Strip section builder)
  └── requires T4.1 (TableCell.LinkTarget)

T6  (Snapshot section SectionId + key metrics)
  └── requires Single-dump Step 1.2 (SectionId on AnalyzerDetailSection)

T7  (Trend Appendix section builder)
  └── independent

T8  (Wire all in TrendReportComposer)
  └── requires T3, T4, T5, T6, T7

T9  (FindingRecord MetricBaseline/Current)
  └── independent; wire in T2 and T8

T10 (Renderer updates)
  └── requires T4.1 (LinkTarget), T4.2 (SparklineBlock)
  └── requires T1 (TrendDomainHealthEntry JSON registration)
```

Recommended batching:
- **Batch 1:** T9 + T1 (data model additions — no rendering yet)
- **Batch 2:** T3 + T4.1 + T4.3 (regression dashboard + timeline extraction + LinkTarget)
- **Batch 3:** T4.2 + T5 (SparklineBlock + snapshot strip)
- **Batch 4:** T2 + T6 (executive summary + snapshot section upgrades)
- **Batch 5:** T7 + T8 (appendix + wire-up in composer)
- **Batch 6:** T10 (renderer updates — all three formats)
- **Batch 7:** T11 (doc update)

---

## Testing Checklist

| Test | Method |
|---|---|
| Trend report sections appear in T0→T7 order | assert `AnalyzerSections` order by `SortOrder` |
| T3 (Regression Dashboard) absent when no regressions or new findings | render with identical snapshots |
| T4 (Timeline) absent when only 1 snapshot | render with single-snapshot trend |
| T5 snapshot strip table has one row per snapshot | assert row count = `TrendDumpCount` |
| T6 `SectionId = "detail-{N}"` matches T5 link targets | assert `LinkTarget` matches `SectionId` |
| `TableCell.LinkTarget` renders as `<a href>` in HTML | HTML golden test |
| `SparklineBlock` serializes with `"type": "sparkline"` in JSON | JSON golden test |
| `TrendDomainHealthEntry.Change` correctly identifies regression | unit test with mock findings |
| `TrendReportDocument.TrendNewFindingCount` = `NewFindings.Count` | composer unit test |
| `FindingRecord.MetricBaseline` and `MetricCurrent` populated for regression findings | assert on composed document |
| Analyzer coverage map has row for every analyzer that ran in any snapshot | assert with 3-snapshot input |
| Resolved findings in T7 are absent from T3 new findings | finding lifecycle invariant test |
| HTML renders `<details id="detail-0">` for first snapshot | HTML golden test |
| Markdown renders snapshot strip as a table | markdown golden test |
| JSON `AnalyzerSections` contains `"$type"` discriminator | JSON schema test |
