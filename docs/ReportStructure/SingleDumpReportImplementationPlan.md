# Single-Dump Report Implementation Plan

## Overview

This plan implements the format defined in `SingleDumpReportFormat.md`. It works within the
existing reporting infrastructure (`AnalysisReportDocument`, `AnalyzerDetailSection`, `SectionBlock`
polymorphic model, `IAnalyzerSectionBuilder` / `IReportSectionBuilder`, `ReportSerializer`,
`CanonicalReportDocumentFactory`, HTML/JSON/markdown renderers).

For visual system and UX-focused rollout in style version v2, see `SingleDumpReportImplementationPlan.v2.md`.

Changes are organized in dependency order. Each step lists the exact files and additions required.

---

## Step 1 — Add `HealthScorecard` to `AnalysisReportDocument`

**Goal:** Surface a per-domain traffic-light scorecard as the first element in every report.

### 1.1 New model records — `AnalysisReportDocument.cs`

```csharp
internal enum DomainSeverity { Unknown, OK, Warning, Critical }

internal sealed record DomainHealthEntry(
    string Domain,          // "Memory", "GC", "Leaks", "Threads", "Async", "Exceptions", "Runtime"
    DomainSeverity Severity,
    int FindingCount,
    int CriticalCount,
    int WarningCount);

internal sealed record HealthScorecard(
    IReadOnlyList<DomainHealthEntry> Domains,
    DomainSeverity OverallSeverity);
```

Add to `AnalysisReportDocument`:

```csharp
public HealthScorecard? HealthScorecard { get; init; }
```

### 1.2 New record for stable section ID — `AnalyzerDetailSection.cs`

Add to `AnalyzerDetailSection`:
```csharp
string SectionId,           // e.g. "A1", "B4" — stable anchor for cross-refs
string Domain,              // "Memory" | "GC" | "Leaks" | "Threads" | "Async" | "Exceptions" | "Runtime"
FindingSeverity? LeadSeverity = null,   // severity of the lead finding (null = informational)
```

### 1.3 New service — `HealthScorecardBuilder.cs` (new file in `Services/`)

```
Input:  IReadOnlyList<AnalyzerRunResult> runs
Output: HealthScorecard
```

Algorithm:
- For each domain in the map (see `SectionIdDomainMap` below), collect all `InsightFinding` from
  `AnalyzerRunResult.Findings` whose `Analyzer` maps to that domain.
- `DomainSeverity` = max severity of those findings (none → OK, any Warning → Warning, any Critical → Critical).
- If all analyzers for a domain were skipped or failed → `Unknown`.
- `OverallSeverity` = max across all domains.

### 1.4 Static map — `SectionIdDomainMap.cs` (new file in `Services/`)

Maps each `AnalyzerName` string to its domain string and section ID prefix:

```csharp
internal static class SectionIdDomainMap
{
    // Key: AnalyzerName, Value: (Domain, SectionIdPrefix)
    private static readonly Dictionary<string, (string Domain, string Id)> _map = new()
    {
        ["LeakCandidateAnalyzer"]      = ("Leaks",     "A1"),
        ["MemoryAnalyzer"]             = ("Memory",    "A2"),
        ["DominatorAnalyzer"]          = ("Memory",    "A3"),
        ["RetentionAnalyzer"]          = ("Memory",    "A4"),    // MemoryLeakAnalyzer in code
        ["GCRootAnalyzer"]             = ("Memory",    "A5"),
        ["StaticRootLeakDetector"]     = ("Memory",    "A6"),
        ["StringAnalyzer"]             = ("Memory",    "A7"),
        ["GCGenerationAnalyzer"]       = ("GC",        "B1"),
        ["AllocationPatternAnalyzer"]  = ("GC",        "B2"),
        ["SegmentAnalyzer"]            = ("GC",        "B3"),
        ["LohFragmentationAnalyzer"]   = ("GC",        "B4"),
        ["SegmentReservationAnalyzer"] = ("GC",        "B5"),
        ["FinalizableObjectAnalyzer"]  = ("GC",        "B6"),
        ["GCHandleAnalyzer"]           = ("GC",        "B7"),
        ["WeakReferenceAnalyzer"]      = ("GC",        "B7"),
        ["DependentHandleAnalyzer"]    = ("GC",        "B7"),
        ["ObjectShapeAnalyzer"]        = ("TypeSystem","C2"),
        ["CollectionAnalyzer"]         = ("TypeSystem","C3"),
        ["ArrayAnalyzer"]              = ("TypeSystem","C4"),
        ["BoxingAnalyzer"]             = ("TypeSystem","C5"),
        ["ThreadAnalyzer"]             = ("Threads",   "D1"),
        ["HangAnalyzer"]               = ("Threads",   "D2"),
        ["LockGraphAnalyzer"]          = ("Threads",   "D3"),
        ["ThreadStackClusterAnalyzer"] = ("Threads",   "D3"),
        ["EventLeakAnalyzer"]          = ("Threads",   "D4"),
        ["AsyncTaskAnalyzer"]          = ("Async",     "E1"),
        ["AsyncStateMachineAnalyzer"]  = ("Async",     "E2"),
        ["CrashAnalyzer"]              = ("Exceptions","F1"),
        ["ModuleAnalyzer"]             = ("Runtime",   "G1"),
        ["AppDomainAnalyzer"]          = ("Runtime",   "G1"),
        ["JitAnalyzer"]                = ("Runtime",   "G2"),
    };
    // Type system sections C1 (type table) is a cross-analyzer section — handled by TypeSystemSectionBuilder
}
```

### 1.5 Wire into `CanonicalReportDocumentFactory.cs`

In `BuildDocument()`:
- Call `HealthScorecardBuilder.Build(runs)` → attach to document.
- For each `AnalyzerDetailSection` built, look up `SectionIdDomainMap` → set `SectionId` and `Domain`.
- Set `LeadSeverity` from the max severity of `InsightFinding` in that section's analyzer run.

**Files changed:** `AnalysisReportDocument.cs`, `AnalyzerDetailSection.cs`, `CanonicalReportDocumentFactory.cs`  
**Files added:** `Services/HealthScorecardBuilder.cs`, `Services/SectionIdDomainMap.cs`

---

## Step 2 — Dynamic Section Ordering by Severity

**Goal:** Domains with Critical findings render before domains with no findings.

### 2.1 Add domain ordering to `ReportSerializer.cs`

After all `AnalyzerDetailSection` objects are assembled, sort them:

```csharp
sections = sections
    .OrderBy(s => DomainOrder(s.Domain))         // domain priority (Leaks < Memory < GC < ...)
    .ThenBy(s => SeverityOrder(s.LeadSeverity))  // within domain: Critical first
    .ThenBy(s => s.SortOrder)                    // original sort order as tiebreak
    .ToList();

static int DomainOrder(string domain) => domain switch
{
    "Leaks"      => 0,
    "Memory"     => 1,
    "GC"         => 2,
    "Threads"    => 3,
    "Async"      => 4,
    "Exceptions" => 5,
    "TypeSystem" => 6,
    "Runtime"    => 7,
    _            => 99
};
static int SeverityOrder(FindingSeverity? s) => s switch
{
    FindingSeverity.Critical => 0,
    FindingSeverity.Warning  => 1,
    FindingSeverity.Info     => 2,
    null                     => 3
};
```

**Files changed:** `Services/ReportSerializer.cs`

---

## Step 3 — Section Builder: Missing Data Surfaces

Each item below adds tables or metric blocks that are currently not emitted. All changes
are additive — existing blocks are preserved.

### 3.1 `ThreadSectionBuilder.cs` — three missing tables

**Missing data:**
- `ThreadDomainResult.ThreadsWithActiveExceptions` (`IReadOnlyList<ThreadExceptionSnapshot>`)
- `ThreadDomainResult.SampledThreads` (`IReadOnlyList<ThreadStateSnapshot>`)
- `ThreadDomainResult.AppDomainDistribution` (`IReadOnlyDictionary<string, int>`)

**Add:**
```
TableBlock("Threads with Active Exceptions",
    Headers: ["ThreadId","OSThreadId","ExceptionType","ExceptionMessage","LockCount","GcMode","TopFrames"],
    Rows: from ThreadsWithActiveExceptions)

TableBlock("AppDomain Thread Distribution",
    Headers: ["AppDomain","ThreadCount"],
    Rows: from AppDomainDistribution)

CollapsibleSectionBeginBlock("Sampled Thread Snapshots ({SampledSnapshotCount} of {CapturedSnapshotCount})")
  TableBlock("Sampled Threads",
      Headers: ["ThreadId","OSThreadId","LockCount","ThreadState","GcMode","WaitCategory","StackSizeBytes","TopFrame"],
      Rows: from SampledThreads (take first frame only))
CollapsibleSectionEndBlock()
```

Add MetricBlock entries: `SampledSnapshotCount`, `CapturedSnapshotCount`, `SamplingCapacity`, `SamplingSeed`.

### 3.2 `ThreadConcurrencySectionBuilder.cs` — HealthScore surfaced

**Missing:** `HangDomainResult.HealthScore` is never shown in section output.

**Add:**
```
MetricBlock("Blocking Health Score", HealthScore.ToString(), RawValue: HealthScore)
// Place immediately after the scalar KPI group, before wait category table.
// Add LeadFinding derivation: HealthScore < 50 → Warning, < 25 → Critical
```

### 3.3 `HeapSegmentDiagnosticsSectionBuilder.cs` — POH/FOH type tables

**Missing:**
- `SegmentAnalysisDomainResult.TopPohTypes` (was ❌ in old format)
- `SegmentAnalysisDomainResult.TopFrozenTypes` (was ❌ in old format)

**Add:**
```
if TopPohTypes not null and not empty:
    TableBlock("Top POH Types by Size",
        Headers: ["TypeName","Count","TotalBytes","AverageSize","EstimatedRetainedBytes"],
        Rows: from TopPohTypes)

if TopFrozenTypes not null and not empty:
    TableBlock("Top Frozen (FOH) Types by Size",
        Headers: ["TypeName","Count","TotalBytes","AverageSize","EstimatedRetainedBytes"],
        Rows: from TopFrozenTypes)
```

### 3.4 `LohFragmentationSectionBuilder.cs` — TopLargeObjects table (identify file first)

**File:** Find the section builder for `LohFragmentationAnalyzer`. Check `LohFragmentationSectionBuilder.cs` or similar.

**Missing:** `LohFragmentationDomainResult.TopLargeObjects` (`IReadOnlyList<LargeObjectSnapshot>`) — was ❌ in old format; now exists in model.

**Add:**
```
if TopLargeObjects not null and not empty:
    TableBlock("Top Large LOH Objects",
        Headers: ["Address","TypeName","Size","SampleAddress"],  // use actual LargeObjectSnapshot fields
        Rows: from TopLargeObjects)
```

### 3.5 `StringSectionBuilder.cs` — percentiles and type distribution

**Missing:**
- `StringDomainResult.Distribution.Percentiles` (p50, p90, p99 char lengths)
- `StringDomainResult.TopDuplicateTypes`

**Add:**
```
if Distribution?.Percentiles not null:
    TableBlock("String Length Percentiles",
        Headers: ["Percentile","CharLength"],
        Rows: from Percentiles dict)

if TopDuplicateTypes not null:
    TableBlock("Duplicate String Type Distribution",
        Headers: ["TypeName","Count"],
        Rows: from TopDuplicateTypes)
```

### 3.6 `TypeSystemSectionBuilder.cs` — TopValueHeavyTypes table

**Missing:** `ObjectShapeAnalyzerDomainResult.TopValueHeavyTypes` — absent from old format.

**Add:**
```
TableBlock("Top Value-Heavy Types",
    Headers: ["TypeName","TotalFields","ReferenceFields","ValueFields","ReferenceFieldRatio",
              "InstanceCount","IsValueType","IsArray","BaseTypeChainDepth","InterfaceCount","Category"],
    Rows: from TopValueHeavyTypes)
```

### 3.7 `CollectionSectionBuilder.cs` — collection inventory breakdown

**Missing:** `CollectionDomainResult` scalar counts per collection kind are not shown (only `TotalWastedMemory` and wasteful list).

**Add at top of section (before wasteful collections table):**
```
TableBlock("Collection Type Inventory",
    Headers: ["Kind","Count"],
    Rows: [
        ("Dictionary",  Dictionaries),
        ("List<T>",     Lists),
        ("HashSet<T>",  HashSets),
        ("Queue<T>",    Queues),
        ("Stack<T>",    Stacks),
        ("SortedList",  SortedLists),
        ("SortedSet",   SortedSets),
        ("ArrayList",   ArrayLists),
    ])
```

Add `WasteCountsByKind` table if populated:
```
TableBlock("Wasteful Collections by Kind",
    Headers: ["Kind","WastefulCount"],
    Rows: from WasteCountsByKind)
```

Also add `WastefulCollectionSnapshot` fields not yet emitted: `Head`, `Tail`, `LargestContiguousFreeSegmentBytes`, `FreeSegmentCount`, `SizeEstimateConfidence`, `DetectionMethod`, `RootDescription`.

### 3.8 `EventLeakSectionBuilder.cs` — SubscriberDetail per-method expansion

**Missing:** `EventLeakInstanceSnapshot.SubscriberDetails` (`IReadOnlyList<SubscriberDetail>`) — method name per subscriber is never shown.

**Add per `TopLeakInstances` row, inside a collapsible expansion:**
```
CollapsibleSectionBeginBlock("Subscriber Details — {PublisherType}.{EventFieldName}")
    TableBlock("Subscriber Method Details",
        Headers: ["Type","MethodName","Size","Count"],
        Rows: from SubscriberDetails)
CollapsibleSectionEndBlock()
```

### 3.9 `WeakReferenceSectionBuilder.cs` — per-kind breakdown

**Missing:** `WeakReferenceDomainResult.WeakHandleKinds` — the total `TotalWeakHandles` is shown but not per-kind breakdown (Weak vs WeakLong vs SizedRef).

**Add:**
```
TableBlock("Weak Handle Kinds",
    Headers: ["Kind","Count"],
    Rows: from WeakHandleKinds)
```

### 3.10 `DependentHandleSectionBuilder.cs` — source→target pair edges

**Missing:** `DependentHandleDomainResult.TopSourceTargetEdges` — was in model but not emitted.

**Add:**
```
TableBlock("Top Source → Target Type Pairs",
    Headers: ["SourceType → TargetType","Count"],
    Rows: from TopSourceTargetEdges)
```

### 3.11 `AppDomainAssemblySectionBuilder.cs` — HeavyTypeDensityModules

**Missing:** `ModuleDomainResult.HeavyTypeDensityModules` — absent from old format entirely.

**Add:**
```
TableBlock("High Type-Density Modules",
    Headers: ["ModuleName","AssemblyName","UniqueTypeCount","ObjectCount","TotalBytes","BytesPerType"],
    Rows: from HeavyTypeDensityModules)
```

### 3.12 `ExceptionAnalysisSectionBuilder.cs` — inference confidence column

**Missing:** `CrashThreadCandidateSnapshot.OriginalStackTraceConfidence` and `OriginalStackTraceInferredFrom` — exist in model but not shown.

**Add columns to crash thread table:**
```
Headers: [...existing..., "TraceConfidence", "TraceSource"]
Rows: add OriginalStackTraceConfidence.ToString(), OriginalStackTraceInferredFrom ?? ""
```

### 3.13 `GCHandleSectionBuilder.cs` — HandlesByKind breakdown

**Missing:** `GCHandleDomainResult.HandlesByKind` (per-kind counts) is in the model but the section only shows Strong/Weak summary totals.

**Add:**
```
TableBlock("GC Handle Breakdown by Kind",
    Headers: ["Kind","Count"],
    Rows: from HandlesByKind)
```

### 3.14 `ConfidenceSectionBuilder.cs` — per-analyzer memory diagnostics

**Missing:** `AnalyzerRunResult.Diagnostics.MemoryStats` — never surfaced in report output.

**Add a new sub-table when `--memory-diagnostics` is enabled** (detect via `MemoryStats != null` on at least one run):
```
CollapsibleSectionBeginBlock("Per-Analyzer Memory Diagnostics")
    TableBlock("Analyzer Memory Impact",
        Headers: ["Analyzer","WS Before","WS After","WS Delta","MH Before","MH After","MH Delta"],
        Rows: from runs where MemoryStats != null)
CollapsibleSectionEndBlock()
```

Also extend `AnalyzerRunStatusRecord` in `AnalysisReportDocument.cs` to include `CacheHits` and `CacheMisses` (currently only `ObjectScanCount` is captured):
```csharp
internal sealed record AnalyzerRunStatusRecord(
    string AnalyzerName,
    string Status,
    double DurationMs,
    int FindingCount,
    int WarningCount,
    long ObjectScanCount,
    long CacheHits,       // ADD
    long CacheMisses,     // ADD
    string? ErrorMessage,
    string? FindingGeneratorError,  // ADD — from Diagnostics.FindingGeneratorError
    string? SkipReason);            // ADD
```

Update `ReportSerializer.BuildRunStatusRecord()` to populate the new fields from `AnalyzerRunResult.Diagnostics`.

---

## Step 4 — Add `CrossDomainInsights` Section

**Goal:** Surface `InsightEngine` cross-correlation findings that span multiple domains as a dedicated section after all domain sections.

### 4.1 New `IReportSectionBuilder` — `CrossDomainInsightsSectionBuilder.cs`

Input: all `InsightFinding` records.  
Logic: findings whose `Tags` contain `"cross-analyzer"` or whose `Analyzer = "InsightEngine"` — render as the cross-domain section.

Output: `AnalyzerDetailSection` with `SectionId = "X1"`, `Domain = "CrossDomain"`.

```
HeadingBlock("Cross-Domain Insights")
TableBlock("All Cross-Analyzer Findings",
    Headers: ["Severity","Analyzer","Category","Title","Evidence","Recommendation","Confidence","Tags"],
    Rows: from findings sorted by Severity desc)
```

### 4.2 Register in DI / `DefaultReportBuilderFactory`

Add `CrossDomainInsightsSectionBuilder` to the `IReportSectionBuilder` list, with `SortOrder` set last (after all domain sections).

**Files added:** `SectionBuilders/CrossDomainInsightsSectionBuilder.cs`  
**Files changed:** DI registration file (identify the `DefaultReportBuilderFactory` or startup wiring)

---

## Step 5 — `HealthScorecard` Renderer Updates

**Status:** Implemented in markdown and HTML renderers.

### 5.1 Markdown renderer — `CanonicalReportFormatter.cs`

Add a `RenderHealthScorecard(HealthScorecard scorecard)` method called before all sections:

```markdown
## Health Summary

| Domain | Severity | Critical | Warning |
|--------|----------|----------|---------|
| Memory | 🔴 Critical | 2 | 1 |
| GC | 🟡 Warning | 0 | 3 |
...
```

### 5.2 HTML renderer — `HtmlReportRenderer.cs`

The HTML renderer serializes the document to JSON and renders via JavaScript. Add a `healthScorecard` key from the JSON document to the front-end rendering logic.

If the HTML renderer uses a JavaScript template: add a scorecard component at the top of the report body that reads `doc.healthScorecard.domains` and renders traffic-light chips.

### 5.3 JSON renderer — `JsonCanonicalReportFormatter.cs`

No changes needed — `HealthScorecard` on the document will serialize automatically via `ReportJsonContext`. Register the new type:

```csharp
// ReportJsonContext.cs — add:
[JsonSerializable(typeof(HealthScorecard))]
[JsonSerializable(typeof(DomainHealthEntry))]
```

---

## Step 6 — Confidence Band Inline in Section Output

**Goal:** Every `LeadFinding` derived block should show the confidence band inline, not only in the appendix.

### 6.1 Add `ConfidenceBlock` variant — `AnalyzerDetailSection.cs`

```csharp
[JsonDerivedType(typeof(ConfidenceBandBlock), "confidenceBand")]
...
internal sealed record ConfidenceBandBlock(
    string Band,        // "High" | "Medium" | "Low"
    double Score,       // 0.0–1.0
    string Symbol,      // "★★★☆" | "★★☆☆" | "★☆☆☆"
    string[] Caveats) : SectionBlock;
```

### 6.2 Helper method — `SectionBuilderBase.cs` (or shared utility)

```csharp
internal static ConfidenceBandBlock BuildConfidenceBand(double? score, IReadOnlyList<string>? caveats)
{
    double s = score ?? 0.5;
    return new ConfidenceBandBlock(
        Band:    s >= 0.8 ? "High" : s >= 0.5 ? "Medium" : "Low",
        Score:   s,
        Symbol:  s >= 0.8 ? "★★★☆" : s >= 0.5 ? "★★☆☆" : "★☆☆☆",
        Caveats: caveats?.ToArray() ?? []);
}
```

### 6.3 Apply in section builders with known heuristic data

Emit `ConfidenceBandBlock` immediately after the `HeadingBlock` in the following section builders:
- `LeakAnalysisSectionBuilder` — ★★☆☆ (HeuristicOnly always true)
- Dominator section builder — ★★☆☆ (BFS approximation)
- `MemoryLeakFindingGenerator` / retention section — ★★☆☆ when `ReferenceCountingSkipped = true`, else ★★★☆
- `GCRootIntelligenceSectionBuilder` — ★★☆☆ (avg-size retained estimate)
- `AllocationPattern` section — ★☆☆☆ (dump-snapshot heuristics)

For measured data sections (ThreadAnalyzer, LockGraphAnalyzer, StringAnalyzer, CrashAnalyzer): ★★★☆.

---

## Step 7 — `ExecutiveSummaryRecord` — Add Scorecard Integration

### 7.1 Extend `ExecutiveSummaryRecord` — `AnalysisReportDocument.cs`

```csharp
internal partial record ExecutiveSummaryRecord
{
    public HealthScorecard? HealthScorecard { get; init; }    // ADD — reference to top-level scorecard
    public IReadOnlyList<FindingRecord>? CriticalFindings { get; init; } = null;   // ADD — top 5 Critical
    public IReadOnlyList<FindingRecord>? WarningFindings { get; init; } = null;    // ADD — top 5 Warning
}
```

### 7.2 Update `ExecutiveSummarySectionBuilder.cs`

- Emit `CriticalFindings` before `TopRecommendations`.
- Emit `WarningFindings` after Critical.
- Emit the key metrics strip (TotalBytes, LOH, Gen2%, GCPressure, LeakCandidates, BlockedThreads, etc.) as a `TableBlock` with two columns: Metric | Value.

The metrics strip sources (all already in domain results; accessed via the section builder's existing result lookup):
```
TotalBytes          → MemoryDomainResult.TotalBytes
LOH %               → MemoryDomainResult.LohPercent
Gen2 %              → GCGenerationDomainResult.Gen2Pct
GC Pressure         → AllocationPatternDomainResult.GCPressure.ToString()
Leak Candidates     → LeakCandidateDomainResult.TotalCandidates
Hang Score          → HangDomainResult.HealthScore
Blocked Threads     → ThreadDomainResult.BlockedThreadCount
Deadlock Cycles     → LockGraphDomainResult.DeadlockCandidateCount
Active Exceptions   → CrashDomainResult.ActiveExceptions
Finalizer Queue     → FinalizableObjectDomainResult.FinalizerQueueCount
```

**Files changed:** `AnalysisReportDocument.cs`, `SectionBuilders/ExecutiveSummarySectionBuilder.cs`

---

## Step 8 — Type Table Section (C1) as Cross-Analyzer `IReportSectionBuilder`

**Goal:** The full joined type table (C1 in the format) requires data from three analyzers
(`MemoryDomainResult`, `GCGenerationDomainResult`, `ObjectShapeAnalyzerDomainResult`). This cannot
be built by a single `IAnalyzerSectionBuilder`. Build it as an `IReportSectionBuilder`.

### 8.1 New file — `SectionBuilders/TypeTableSectionBuilder.cs`

```csharp
internal sealed class TypeTableSectionBuilder : SectionBuilderBase, IReportSectionBuilder
```

Input via `AnalyzerRunResult` lookup: `MemoryDomainResult`, `GCGenerationDomainResult`, `ObjectShapeAnalyzerDomainResult`.

Algorithm:
1. Build a `Dictionary<string, TypeRow>` keyed by `TypeName`.
2. Populate from `MemoryDomainResult.TopTypesBySize` (Count, TotalBytes, LohBytes, AverageSize, EstimatedRetainedBytes, SampleAddress, ModuleName).
3. Join `GCGenerationDomainResult.PerTypeGenerationProfiles` by TypeName → add Gen0/1/2 counts, derive Gen2%.
4. Join `ObjectShapeAnalyzerDomainResult.TopReferenceHeavyTypes` and `TopValueHeavyTypes` by TypeName → add IsValueType, IsArray, ReferenceFields, RefFieldRatio, BaseTypeChainDepth.
5. Emit `TableBlock` with all columns.

Output: `AnalyzerDetailSection` with `SectionId = "C1"`, `Domain = "TypeSystem"`, `SortOrder = 300`.

Register in DI alongside other `IReportSectionBuilder` registrations.

**Files added:** `SectionBuilders/TypeTableSectionBuilder.cs`

---

## Step 9 — B7 Compound Section (GC Handles + Weak Refs + Dependent Handles)

**Status:** Implemented as adjacent GC-domain sections with shared ordering.

**Goal:** Merge `GCHandleSectionBuilder`, `WeakReferenceSectionBuilder`, and `DependentHandleSectionBuilder` into a single rendered section (B7) without changing the builder classes themselves.

### 9.1 New `IReportSectionBuilder` — `GCHandleCompoundSectionBuilder.cs`

Aggregates output from the three individual builders into one `AnalyzerDetailSection`:
- Uses `GCHandleSectionBuilder.Build()`, `WeakReferenceSectionBuilder.Build()`, `DependentHandleSectionBuilder.Build()` internally to get their block lists.
- Concatenates all blocks under a single `HeadingBlock("GC Handles, Weak References & Dependent Handles")`.
- Sets `SectionId = "B7"`, `Domain = "GC"`.
- Original individual sections are suppressed from final output (filter by SectionId or AnalyzerName in `ReportSerializer`).

Alternatively — if compound building is too complex — set the three individual builders' `SortOrder` to adjacent values and assign them all `Domain = "GC"` so they cluster correctly. This is the simpler option.

**Recommendation:** take the simpler option (adjacent sort orders + same domain) and skip the compound builder. The format spec allows sub-sections within a domain.

**Files changed:** Adjust `SortOrder` and add `Domain` / `SectionId` in `GCHandleSectionBuilder`, `WeakReferenceSectionBuilder`, `DependentHandleSectionBuilder`.

---

## Step 10 — LOH Fragmentation Section Builder (identify correct file)

**Status:** Implemented; LOH large-object tables already exist in the reporting layer.

**Check:** Confirm which section builder handles `LohFragmentationDomainResult`. It may be inside `HeapSegmentDiagnosticsSectionBuilder` or a dedicated `LohFragmentationSectionBuilder`.

```
grep -r "LohFragmentationDomainResult" src/DumpDetective.Reporting/
```

Once identified: apply Step 3.4 (add `TopLargeObjects` table) to that builder.

**Note:** `LohFragmentationDomainResult.TopLargeObjects` is `IReadOnlyList<LargeObjectSnapshot>`. Confirm the fields on `LargeObjectSnapshot` from the model file before building the table headers.

---

## Step 11 — `SectionBlock` Renderer Updates

### 11.1 Markdown renderer — `CanonicalReportFormatter.cs`

Add rendering for:
- `ConfidenceBandBlock` → `> ★★☆☆ Medium confidence — {caveats joined}`

Verify `CollapsibleSectionBeginBlock` / `CollapsibleSectionEndBlock` are rendered (even if as flat headings in markdown — collapsing is HTML-only).

### 11.2 HTML renderer — `HtmlReportRenderer.cs` / JS template

Verify `CollapsibleSectionBeginBlock` / `CollapsibleSectionEndBlock` produce a `<details><summary>` wrapper in the HTML output. If not implemented, add it.

Add rendering for `ConfidenceBandBlock` as an inline badge:
```html
<span class="confidence-band confidence-{band.toLower()}">
  {symbol} {band} confidence
</span>
{caveats as <ul class="caveats">}
```

### 11.3 JSON renderer

No changes — `ConfidenceBandBlock` serializes automatically once registered in `ReportJsonContext`:
```csharp
[JsonDerivedType(typeof(ConfidenceBandBlock), "confidenceBand")]
```

---

## Step 12 — `AnalysisReportDocument` JSON Context Updates

Register all new types in `ReportJsonContext.cs`:

```csharp
[JsonSerializable(typeof(HealthScorecard))]
[JsonSerializable(typeof(DomainHealthEntry))]
[JsonSerializable(typeof(ConfidenceBandBlock))]
```

Ensure `AnalyzerDetailSection` new fields (`SectionId`, `Domain`, `LeadSeverity`) are serialized (they will be automatically since they're on the record).

**Files changed:** `Serialization/ReportJsonContext.cs`

---

## Step 13 — `ProfessionalTierReport.md` Reference Update

**Status:** Implemented.

Once the new format is implemented and validated, update `ProfessionalTierReport.md` to note that:
- The data availability audit remains authoritative for gap tracking.
- The format spec lives in `SingleDumpReportFormat.md`.
- The implementation status column can now track "Surfaced in report" vs "In model only".

---

## Implementation Order (dependency graph)

```
Step 1  (schema + scorecard model)
  └── Step 12 (JSON context)
  └── Step 5  (scorecard renderers)
  └── Step 2  (dynamic ordering) — depends on SectionId/Domain on sections

Step 3  (section builder gaps) — independent, can be done in parallel per sub-item

Step 4  (cross-domain insights) — independent

Step 6  (confidence band block)
  └── Step 11 (renderer for new block type)
  └── Step 12 (JSON context for new block type)

Step 7  (executive summary extension) — depends on Step 1

Step 8  (type table section builder) — independent

Step 9  (B7 compound section) — independent

Step 10 (LOH large objects) — independent (find file first)
```

Recommended batching for a single session:
- **Batch 1:** Steps 1 + 2 + 12 (schema foundation)
- **Batch 2:** Steps 3.1–3.14 (section builder gaps — all additive, low risk)
- **Batch 3:** Steps 6 + 11 (confidence band — new block type + renderers)
- **Batch 4:** Steps 4 + 7 + 8 (cross-domain, executive summary, type table)
- **Batch 5:** Steps 5 + 9 + 10 + 13 (renderers, compound section, LOH fix, docs)

---

## Testing Checklist

For each step, verify:

| Test | Method |
|---|---|
| JSON output contains `healthScorecard` key as first element | deserialize JSON, check key order |
| Domains order: Critical domain before OK domain | compare section order in output |
| `SectionId` values are stable across runs on same dump | golden test comparison |
| All new tables render in markdown (non-empty) | golden report test with small dump |
| All new tables render in HTML (non-empty, collapsed) | visual inspection + HTML golden |
| `ConfidenceBandBlock` renders in all three formats | unit test per renderer |
| `ThreadsWithActiveExceptions` table absent when empty | render with dump with no exceptions |
| `HealthScorecard.OverallSeverity = Critical` when any domain is Critical | unit test |
| `AnalyzerRunStatusRecord` contains `CacheHits`, `CacheMisses`, `FindingGeneratorError` | JSON assertion |
| `CrossDomainInsightsSectionBuilder` renders after all domain sections | section order assertion |
