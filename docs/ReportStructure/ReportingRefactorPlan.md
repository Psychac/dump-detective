# Reporting Architecture Refactor
## Printers → Section Builders + JSON Output Contract

> **Branch**: `optimize`  
> **Status**: ✅ COMPLETE — All Phases A–H Done  

---

## Goals

1. Replace 17 `Printer` classes + `IReportWriter`/`IStructuredReportWriter` hack with `IAnalyzerSectionBuilder` returning structured data directly
2. Replace `ComposedReport` (pre-rendered submodule lists) with `AnalysisReportDocument` (typed per-analyzer section records)
3. Replace `HtmlCanonicalReportFormatter` (~500 lines of C# building HTML from string lists) with an embedded-resource template + client-side JS renderer
4. Make `report.json` a first-class saved output artifact — the single source of truth for all renderers
5. Delete `StructuredCaptureReportWriter`, `OutputWriter`, and all related writer infrastructure

---

## Invariants — What Does NOT Change

| Component | Status |
|-----------|--------|
| `IAnalyzer` interface | ✅ Unchanged |
| All analyzer implementations | ✅ Unchanged |
| `AnalysisContext`, `HeapIndexBuildResult` | ✅ Unchanged |
| `AnalyzerDomainResult` type hierarchy | ✅ Unchanged |
| `IFindingGenerator` + `FindingGenerationPipeline` | ✅ Unchanged |
| `InsightEngine` | ✅ Unchanged |
| CLI pipeline stages (LoadDump → BuildHeapIndex → RunAnalyzers → GenerateFindings) | ✅ Unchanged |
| Golden test baselines | ⚠️ Must be regenerated — they test JSON shape, not HTML strings |

---

## Execution Order

```
Phase A  →  Phase B  →  Phase C  →  Phase D  →  Phase E  →  Phase F  →  Phase G  →  Phase H
Contracts   Builders    Serializer  Formatters  Templates   Wire CLI    Delete      Tests
```

Each phase must produce a clean build before the next phase begins.  
Nothing is deleted until **Phase G** — old and new code coexist through Phase F.

---

## Phase A — Define New Contracts ✅ Complete

> Create all new interfaces and model types. Nothing is deleted. Build must pass after each step.

---

### A1 · Create `IAnalyzerSectionBuilder` ✅

**File**: `src/DumpDetective.Reporting/Abstractions/IAnalyzerSectionBuilder.cs`  
**Action**: Created  
**Note**: Placed in `DumpDetective.Reporting` (not Core) — `Core` has no reference to `Reporting`; moving the interface avoids a circular dependency.  

```csharp
internal interface IAnalyzerSectionBuilder
{
    string AnalyzerName { get; }           // Matches AnalyzerRunResult.AnalyzerName for routing
    string DisplayTitle => AnalyzerName;   // Human-readable section title
    int SortOrder => 100;                  // Controls order in report output

    bool CanHandle(AnalyzerDomainResult result);
    AnalyzerDetailSection Build(AnalyzerDomainResult result);
    // Returns pure structured data — no writer, no text formatting
}
```

---

### A2 · Create `AnalyzerDetailSection` and `SectionBlock` discriminated union ✅

**File**: `src/DumpDetective.Reporting/Models/AnalyzerDetailSection.cs`  
**Action**: Created  
**Replaces**: `DetailedAnalyzerSection` + `DetailedAnalyzerSubmodule` + `DetailedAnalyzerSubmoduleKind` enum

```csharp
internal sealed record AnalyzerDetailSection(
    string AnalyzerName,
    string DisplayTitle,
    int SortOrder,
    IReadOnlyList<SectionBlock> Blocks);   // Ordered content blocks — typed, not an enum

// Discriminated union root — each subtype carries only what it needs
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HeadingBlock),                "heading")]
[JsonDerivedType(typeof(MetricBlock),                 "metric")]
[JsonDerivedType(typeof(PathBlock),                   "path")]
[JsonDerivedType(typeof(TextBlock),                   "text")]
[JsonDerivedType(typeof(ListItemBlock),               "listItem")]
[JsonDerivedType(typeof(DividerBlock),                "divider")]
[JsonDerivedType(typeof(BlankBlock),                  "blank")]
[JsonDerivedType(typeof(TableBlock),                  "table")]
[JsonDerivedType(typeof(CollapsibleSectionBeginBlock),"collapsibleBegin")]
[JsonDerivedType(typeof(CollapsibleSectionEndBlock),  "collapsibleEnd")]
internal abstract record SectionBlock;

internal sealed record HeadingBlock(string Text, int IndentLevel = 0) : SectionBlock;
internal sealed record MetricBlock(string Label, string Value, double? RawValue = null, int IndentLevel = 0) : SectionBlock;
internal sealed record PathBlock(string Label, string Path, int IndentLevel = 0) : SectionBlock;
internal sealed record TextBlock(string Text, int IndentLevel = 0) : SectionBlock;
internal sealed record ListItemBlock(string Text, int IndentLevel = 0) : SectionBlock;
internal sealed record DividerBlock : SectionBlock;
internal sealed record BlankBlock : SectionBlock;

internal sealed record TableBlock(
    string? Caption,
    IReadOnlyList<string> Headers,
    IReadOnlyList<TableRow> Rows) : SectionBlock;

internal sealed record TableRow(IReadOnlyList<TableCell> Cells);
internal sealed record TableCell(string Display, long? RawValue = null);   // RawValue for client-side sort

internal sealed record CollapsibleSectionBeginBlock(string Title) : SectionBlock;
internal sealed record CollapsibleSectionEndBlock : SectionBlock;
```

> **Note**: `[JsonPolymorphic]` + `[JsonDerivedType]` require .NET 7+. Confirmed available in .NET 10.  
> The `type` discriminator field is written by `System.Text.Json` and read by `report.js`.

---

### A3 · Create `AnalysisReportDocument` — the JSON output contract ✅

**File**: `src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs`  
**Action**: Created  
**Replaces**: `ComposedReport` and all nested record types in `ComposedReport.cs`

```csharp
internal sealed record AnalysisReportDocument
{
    public string SchemaVersion { get; init; } = "2.0";
    public string DumpPath { get; init; } = "";
    public DateTime GeneratedAtUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public bool IsTrendReport { get; init; }
    public int TrendDumpCount { get; init; }
    public IReadOnlyList<string>? TrendDumpPaths { get; init; }

    // Cross-cutting outputs
    public IReadOnlyList<FindingRecord> Findings { get; init; } = [];
    public ExecutiveSummaryRecord? ExecutiveSummary { get; init; }        // null unless audience == Executive
    public IReadOnlyList<DeveloperActionRecord> DeveloperActionPlan { get; init; } = [];
    public IReadOnlyList<ConfidenceNote> Confidence { get; init; } = [];
    public DedupRecord DedupDiagnostics { get; init; } = new(0, 0, 0);

    // Per-analyzer structured sections — ordered by SortOrder
    public IReadOnlyList<AnalyzerDetailSection> AnalyzerSections { get; init; } = [];
}

// Serializable projection of InsightFinding — InsightFinding itself is unchanged
internal sealed record FindingRecord(
    string Analyzer,
    string Category,
    string Severity,          // FindingSeverity.ToString()
    string Title,
    string Evidence,
    string Recommendation,
    IReadOnlyList<string> Tags,
    string Fingerprint);

internal sealed record ExecutiveSummaryRecord(
    long TotalManagedBytes,
    int LeakLikelihoodScore,        // 0–100
    int GcPressureScore,            // 0–100
    int ThreadContentionScore,      // 0–100
    IReadOnlyList<FindingRecord> TopRecommendations);   // Top 3 Critical/Warning findings

internal sealed record DeveloperActionRecord(
    string Priority,
    string Title,
    string Action,
    string Impact);

internal sealed record ConfidenceNote(
    string Analyzer,
    bool Capped,
    string Reason);

internal sealed record DedupRecord(
    int MergedSections,
    int DuplicateCandidates,
    int EvidenceBeforeMerge);
```

---

### A4 · Create `ISectionBuilderFactory` ✅

**File**: `src/DumpDetective.Cli/Services/ISectionBuilderFactory.cs`  
**Action**: Created  
**Replaces**: `IAnalyzerReporterFactory`

```csharp
internal interface ISectionBuilderFactory
{
    IReadOnlyList<IAnalyzerSectionBuilder> CreateBuilders();
}
```

---

## Phase B — Implement Section Builders ✅ Complete

> Create one `IAnalyzerSectionBuilder` per existing `Printer`.  
> Each builder returns `AnalyzerDetailSection` directly — no writer, no `IReportWriter` involved.

**Folder**: `src/DumpDetective.Reporting/SectionBuilders/`

### Canonical Shape

Every section builder follows this pattern:

```csharp
internal sealed class MemorySectionBuilder : IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Memory Analysis";
    public string DisplayTitle => "Memory Analysis";
    public int SortOrder => 20;

    public bool CanHandle(AnalyzerDomainResult result) => result is MemoryDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var domain = (MemoryDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(new HeadingBlock("OVERALL SUMMARY"));
        blocks.Add(new MetricBlock("Total Memory", FormatHelper.FormatBytes(domain.TotalBytes), (double)domain.TotalBytes));
        blocks.Add(new MetricBlock("Total Objects", $"{domain.TotalObjects:N0}", domain.TotalObjects));
        // ...
        blocks.Add(new TableBlock(
            Caption: "Top 20 types by memory size",
            Headers: ["Type", "Count", "Total Size"],
            Rows: domain.TopTypesBySize.Take(20)
                .Select(t => new TableRow([
                    new TableCell(t.TypeName),
                    new TableCell($"{t.Count:N0}", t.Count),
                    new TableCell(FormatHelper.FormatBytes(t.TotalBytes), (long)t.TotalBytes)]))
                .ToList()));

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks);
    }
}
```

**Recommended helper base** (`SectionBuilderBase.cs` — optional static helpers):

```csharp
internal abstract class SectionBuilderBase
{
    protected static HeadingBlock H(string text, int indent = 0) => new(text, indent);
    protected static MetricBlock  M(string label, string value, double? raw = null, int indent = 0) => new(label, value, raw, indent);
    protected static TextBlock    T(string text, int indent = 0) => new(text, indent);
    protected static ListItemBlock Li(string text, int indent = 0) => new(text, indent);
    protected static DividerBlock Divider() => new();
    protected static BlankBlock   Blank() => new();
    protected static TableRow     Row(params TableCell[] cells) => new(cells);
    protected static TableCell    Cell(string display, long? raw = null) => new(display, raw);
}
```

---

### Builder Inventory

| ID | File | Replaces | Analyzer Name | Domain Result | Sort |
|----|------|----------|---------------|---------------|------|
| B1 | `MemorySectionBuilder.cs` | `MemoryPrinter.cs` | `Memory Analysis` | `MemoryDomainResult` | 20 |
| B2 | `GCGenerationSectionBuilder.cs` | `GCGenerationPrinter.cs` | `GC Generation Analysis` | `GCGenerationDomainResult` | 30 |
| B3 | `SegmentSectionBuilder.cs` | `SegmentPrinter.cs` | `Segment Analysis` | `SegmentAnalysisDomainResult` | 35 |
| B4 | `ModuleSectionBuilder.cs` | `ModulePrinter.cs` | `Module Analysis` | `ModuleDomainResult` | 40 |
| B5 | `CrashSectionBuilder.cs` | `CrashPrinter.cs` | `Crash Analysis` | `CrashDomainResult` | 10 |
| B6 | `HangSectionBuilder.cs` | `HangPrinter.cs` | `Hang Analysis` | `HangDomainResult` | 15 |
| B7 | `MemoryLeakSectionBuilder.cs` | `MemoryLeakPrinter.cs` | `Memory Leak Analysis` | `MemoryLeakDomainResult` | 25 |
| B8 | `CollectionSectionBuilder.cs` | `CollectionPrinter.cs` | `Collection Analysis` | `CollectionDomainResult` | 50 |
| B9 | `StaticRootSectionBuilder.cs` | `StaticRootPrinter.cs` | `Static Root Leak Detection` | `StaticRootLeakDomainResult` | 27 |
| B10 | `ReferenceChainSectionBuilder.cs` | `ReferenceChainPrinter.cs` | `Reference Chain Analysis` | `ReferenceChainDomainResult` | 60 |
| B11 | `GCHandleSectionBuilder.cs` | `GCHandlePrinter.cs` | `GC Handle Analysis` | `GCHandleDomainResult` | 45 |
| B12 | `DependentHandleSectionBuilder.cs` | `DependentHandlePrinter.cs` | `Dependent Handle Analysis` | `DependentHandleDomainResult` | 46 |
| B13 | `LohFragmentationSectionBuilder.cs` | `LohFragmentationPrinter.cs` | `LOH Fragmentation Analysis` | `LohFragmentationDomainResult` | 55 |
| B14 | `ThreadStackClusterSectionBuilder.cs` | `ThreadStackClusterPrinter.cs` | `Thread Stack Cluster Analysis` | `ThreadStackClusterDomainResult` | 65 |
| B15 | `ThreadSectionBuilder.cs` | `ThreadPrinter.cs` | `Thread Analysis` | `ThreadDomainResult` | 12 |
| B16 | `LockGraphSectionBuilder.cs` | `LockGraphPrinter.cs` | `Lock Graph Analysis` | `LockGraphDomainResult` | 70 |
| B17 | `EventLeakSectionBuilder.cs` | `EventLeakPrinter.cs` | `Event Leak Analysis` | `EventLeakDomainResult` | 80 |

---

### Builder Content Specs

**B1 — `MemorySectionBuilder`**
- `HeadingBlock` "OVERALL SUMMARY"
- `MetricBlock` × 6: TotalBytes, TotalObjects, LohBytes, LohPercent, LohThresholdBytes, UniqueTypes
- `TextBlock` heap composition signal (LOH ≥ 40% warning)
- `TableBlock` top 20 types by memory size (Type / Count / Total Size)
- `TableBlock` top 20 types by count (Type / Count / Total Size)
- `TableBlock` size bucket histogram — added when `SizeBucketHistogram` field added (Priority 3)

**B2 — `GCGenerationSectionBuilder`**
- `MetricBlock` × 4: Gen0/Gen1/Gen2/LOH bytes + counts
- `TableBlock` top LOH types
- `TableBlock` `PerTypeGenerationProfile` — added at Priority 4

**B3 — `SegmentSectionBuilder`**
- `MetricBlock` per heap kind (SOH / LOH / POH / FOH) — bytes + segment count
- `TableBlock` per-segment list (Address / Kind / CommittedBytes / FragmentPercent)

**B4 — `ModuleSectionBuilder`**
- `MetricBlock` × 3: total modules, dynamic modules, anonymous modules
- `TableBlock` top modules by heap footprint
- `CollapsibleSectionBeginBlock` + `TableBlock` + `CollapsibleSectionEndBlock` per version conflict group

**B5 — `CrashSectionBuilder`**
- `MetricBlock` × 2: ActiveExceptions, TotalExceptions
- `TableBlock` top exception types by count
- `CollapsibleSectionBeginBlock` per top exception instance with `PathBlock` stack frames + `CollapsibleSectionEndBlock`

**B6 — `HangSectionBuilder`**
- `MetricBlock` × 4: PendingTasks, FaultedTasks, CanceledTasks; `TextBlock` for TaskScanLimited flag
- `TableBlock` blocking threads (ThreadId / FrameCount / TopFrame)
- `TableBlock` top continuation types

**B7 — `MemoryLeakSectionBuilder`**
- `MetricBlock` × 4: TotalStrings, TotalStringMemoryBytes, UniqueStrings, DuplicateStringWastedBytes
- `TableBlock` top duplicate strings (Value / Count / WastedBytes)
- `MetricBlock` FinalizerQueueCount; `TableBlock` top finalizer types
- `TableBlock` top highly-referenced objects
- `TextBlock` SkippedReferenceAddresses confidence note (when > 0)

**B8 — `CollectionSectionBuilder`**
- `MetricBlock` × 2: total collections, wasteful collection count
- `TableBlock` wasteful collections (Type / Count / AvgFillRate% / WastedBytes)

**B9 — `StaticRootSectionBuilder`**
- `MetricBlock` × 2: TotalRetainedBytes, rooted type count
- `TableBlock` top roots by retained bytes (Field / Type / RetainedBytes)
- `TextBlock` BfsCappedCount note when > 0

**B10 — `ReferenceChainSectionBuilder`**
- `MetricBlock` chains found; `TextBlock` ChainSearchCapped flag
- Per chain: `CollapsibleSectionBeginBlock` → `PathBlock` entries → `CollapsibleSectionEndBlock`

**B11 — `GCHandleSectionBuilder`**
- `TableBlock` handles by kind (Kind / Count)
- `TableBlock` pinned types (Type / Count / PinnedRetainedBytes — added at Priority 5)
- `MetricBlock` WeakLikeHandles count
- `TableBlock` top target types

**B12 — `DependentHandleSectionBuilder`**
- `MetricBlock` DependentHandleCount
- `TableBlock` source type distribution
- `TableBlock` target type distribution

**B13 — `LohFragmentationSectionBuilder`**
- `MetricBlock` × 3: TotalLohBytes, segment count, overall fragmentation %
- `TableBlock` per-segment (Address / Size / FragmentationPercent / LargestFreeBlock)
- `TableBlock` top large objects — added at Priority 6
- `TableBlock` free gap histogram — added at Priority 6

**B14 — `ThreadStackClusterSectionBuilder`**
- `MetricBlock` × 2: cluster count, dominant wait category
- Per cluster: `CollapsibleSectionBeginBlock` → `MetricBlock` thread IDs → `TextBlock` representative stack → `CollapsibleSectionEndBlock`

**B15 — `ThreadSectionBuilder`**
- `MetricBlock` × 5: Total, Alive, Background, ThreadPool, FinalizerManagedThreadId
- `MetricBlock` × 3: FinalizerIsBlocked, FinalizerLockCount; `TextBlock` FinalizerFrames
- `MetricBlock` × 2: AsyncChainThreadCount, MaxAsyncChainDepth
- `TableBlock` wait category distribution
- `TableBlock` top frame hotspots

**B16 — `LockGraphSectionBuilder`**
- `MetricBlock` × 2: contested lock count, total waiting threads
- `TableBlock` top contested types
- Per deadlock candidate: `CollapsibleSectionBeginBlock` → cycle nodes → `CollapsibleSectionEndBlock`
- `TableBlock` `ContestedLockDetails` — added at Priority 7

**B17 — `EventLeakSectionBuilder`**
- `MetricBlock` × 3: StaticLeaks, InstanceLeaks, TotalSubscribers
- Per leak group: `CollapsibleSectionBeginBlock` → `TableBlock` publisher/subscriber breakdown → `CollapsibleSectionEndBlock`
- Subscription graph summary — added at Priority 13

---

## Phase C — Create `ReportSerializer` ✅ Complete

> Replaces `ReportBuilder`. Maps `AnalyzerRunResult[]` → `AnalysisReportDocument`.  
> Pure function — no text formatting, no side effects, no I/O.

---

### C1 · Create `ReportSerializer` ✅

**File**: `src/DumpDetective.Reporting/Services/ReportSerializer.cs`  
**Action**: Created  
**Replaces**: `src/DumpDetective.Reporting/Services/ReportBuilder.cs`

**Public API**:

```csharp
internal sealed class ReportSerializer
{
    public AnalysisReportDocument Serialize(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        IReadOnlyList<IAnalyzerSectionBuilder> builders,
        ReportAudience audience = ReportAudience.All);
}
```

**Responsibilities**:

1. Route each `run.Result` to the first matching builder via `CanHandle` — O(builders × runs)
2. Collect `AnalyzerDetailSection` list, sort by `SortOrder`
3. Map `run.Findings` (`InsightFinding[]`) → `FindingRecord[]`
4. Apply deduplication logic (preserved from `ReportBuilder.DeduplicateSections`) to `FindingRecord` list
5. Build `ExecutiveSummaryRecord` from top-3 Critical/Warning findings when `audience == Executive`
6. Build `DeveloperActionRecord` list when `audience == Developer`
7. Build `ConfidenceNote` list from runs that expose cap/limit signals:
   - `MemoryLeakDomainResult.SkippedReferenceAddresses > 0`
   - `HangDomainResult.TaskScanLimited == true`
   - `StaticRootLeakDomainResult.BfsCappedCount > 0` (added at Priority 1)
8. Add `AnalyzerRunResult` failure/finding-generator-error entries as `FindingRecord` with `Severity = "Warning"`
9. Return `AnalysisReportDocument`

---

### C2 · Create `ReportJsonContext` ✅

**File**: `src/DumpDetective.Reporting/Serialization/ReportJsonContext.cs`  
**Action**: Created

```csharp
[JsonSerializable(typeof(AnalysisReportDocument))]
[JsonSerializable(typeof(List<FindingRecord>))]
[JsonSerializable(typeof(List<AnalyzerDetailSection>))]
[JsonSerializable(typeof(List<SectionBlock>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ReportJsonContext : JsonSerializerContext { }
```

> **Polymorphic serialization**: `SectionBlock` derived types are registered via `[JsonDerivedType]` on `SectionBlock` (see A2). The `type` discriminator field is emitted by `System.Text.Json` and consumed by `report.js`.

---

## Phase D — Refactor Formatters ✅ Complete

> All formatters now accept `AnalysisReportDocument`. The `IReportFormatter` interface signature changes.

---

### D1 · Update `IReportFormatter` ✅

**File**: `src/DumpDetective.Reporting/Formatters/CanonicalReportFormatter.cs`  
**Action**: Updated — `Render(ComposedReport)` → `Render(AnalysisReportDocument)`  

```csharp
// Before
internal interface IReportFormatter
{
    ReportFormat Format { get; }
    string Render(ComposedReport report);
}

// After
internal interface IReportFormatter
{
    ReportFormat Format { get; }
    string Render(AnalysisReportDocument doc);
}
```

---

### D2 · Rewrite `TextCanonicalReportFormatter` ✅

Walk `AnalysisReportDocument` directly using `StringBuilder`. No `List<string> lines` pattern.

| `SectionBlock` subtype | Text output |
|------------------------|-------------|
| `HeadingBlock` | `{indent}{text}\n` |
| `MetricBlock` | `{indent}{label}: {value}\n` |
| `PathBlock` | `{indent}{label}: {path}\n` |
| `TextBlock` | `{indent}{text}\n` |
| `ListItemBlock` | `{indent}- {text}\n` |
| `DividerBlock` | `StringConstants.Separator80\n` |
| `BlankBlock` | `\n` |
| `TableBlock` | tab-aligned columns via `CanonicalTableFormatter` helper |
| `CollapsibleSectionBeginBlock` | `[{title}]\n` |
| `CollapsibleSectionEndBlock` | `\n` |

---

### D3 · Rewrite `MarkdownCanonicalReportFormatter` ✅

| `SectionBlock` subtype | Markdown output |
|------------------------|-----------------|
| `HeadingBlock` | `### {text}` |
| `MetricBlock` | `**{label}**: {value}` |
| `PathBlock` | `**{label}**: \`{path}\`` |
| `TextBlock` | `{text}` |
| `ListItemBlock` | `- {text}` |
| `DividerBlock` | `---` |
| `BlankBlock` | *(empty line)* |
| `TableBlock` | GFM table `\| col \| col \|` |
| `CollapsibleSectionBeginBlock` | `<details><summary>{title}</summary>` |
| `CollapsibleSectionEndBlock` | `</details>` |

---

### D4 · Create `HtmlReportRenderer` ✅

**File**: `src/DumpDetective.Reporting/Formatters/HtmlReportRenderer.cs`  
**Action**: Created — thin shell using `EmbeddedResourceLoader`. Registered in Phase F.  

```csharp
internal sealed class HtmlReportRenderer : IReportFormatter
{
    private static readonly string _template = EmbeddedResourceLoader.LoadText("report.html");
    private static readonly string _css      = EmbeddedResourceLoader.LoadText("report.css");
    private static readonly string _js       = EmbeddedResourceLoader.LoadText("report.js");

    public ReportFormat Format => ReportFormat.Html;

    public string Render(AnalysisReportDocument doc)
    {
        string json = JsonSerializer.Serialize(doc, ReportJsonContext.Default.AnalysisReportDocument);
        return _template
            .Replace("{{CSS}}", _css)
            .Replace("{{REPORT_JSON}}", json)
            .Replace("{{JS}}", _js);
    }
}
```

The entire ~500-line class shrinks to ~20 lines. All HTML, CSS, and JS move to embedded resource files.

---

### D5 · Rewrite `JsonCanonicalReportFormatter` ✅

**File**: `src/DumpDetective.Reporting/Formatters/JsonCanonicalReportFormatter.cs`  
**Action**: Created — new file using `ReportJsonContext` source-generated serialization.  

```csharp
public string Render(AnalysisReportDocument doc) =>
    JsonSerializer.Serialize(doc,
        new JsonSerializerOptions(ReportJsonContext.Default.Options) { WriteIndented = true });
```

---

## Phase E — Create Embedded Resource Templates ✅ Complete

> Extract all HTML/CSS/JS from `HtmlCanonicalReportFormatter` into proper source files.

**Project file entry** (`DumpDetective.Reporting.csproj`):

```xml
<ItemGroup>
  <EmbeddedResource Include="Templates\report.html" />
  <EmbeddedResource Include="Templates\report.css" />
  <EmbeddedResource Include="Templates\report.js" />
</ItemGroup>
```

---

### E1 · `report.html` — Page Skeleton ✅

**File**: `src/DumpDetective.Reporting/Templates/report.html`  
**Action**: Created  

Contains **exactly three** C#-side substitution placeholders:

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>DumpDetective Analysis Report</title>
  <style>{{CSS}}</style>
</head>
<body>
  <a class="skip-link" href="#main">Skip to main content</a>
  <div role="status" aria-live="polite" aria-atomic="true" id="clipboard-status" class="sr-only"></div>
  <main class="container" id="main" tabindex="-1"></main>
  <script>window.__REPORT__ = {{REPORT_JSON}};</script>
  <script>{{JS}}</script>
</body>
</html>
```

All rendering logic is in `report.js`. The C# formatter only stamps in the three placeholders.

---

### E2 · `report.css` — Stylesheet ✅

**File**: `src/DumpDetective.Reporting/Templates/report.css`  
**Action**: Created — CSS extracted from `HtmlCanonicalReportFormatter.AppendCss()`, properly formatted.  

Extracted from the current `HtmlCanonicalReportFormatter` (~100 CSS rules across ~100 `List<string>` entries). Unminified for editability.

| Section | Classes |
|---------|---------|
| Base layout | `body`, `.container` |
| Header card | `.header-card`, `.meta-grid`, `.meta-item`, `.dedup-note` |
| Section cards | `.section-card`, `.section-header`, `.severity-badge`, `.severity-critical/warning/info`, `.category` |
| Analyzer sections | `.analyzer-section`, `.detail-color-0` through `.detail-color-5`, `details/summary` chevron animation |
| Detail block | `.detail-block`, `.detail-subheading`, `.detail-divider`, `.detail-key`, `.detail-value`, `.detail-path` |
| Tables | `table`, `thead th`, `tbody td`, `tbody tr:nth-child(even)`, `.wrap`, `caption` |
| Nested collapsibles | `.detail-nested`, `.detail-nested-content` |
| Navigation ToC | `nav.toc`, `nav.toc ol`, `.toc-badge` |
| Filter bar | `.filter-bar`, `.filter-btn`, `.filter-btn.active`, `.filter-search`, `.filter-count` |
| Sortable columns | `thead th.sortable`, `[aria-sort]` indicators |
| Action bar | `.action-bar`, `.action-btn` |
| Address spans | `.addr`, `.copy-btn` |
| Accessibility | `.skip-link`, `:focus-visible`, `.sr-only` |
| Print | `@media print` — hides filter bar, action bar, copy buttons |

---

### E3 · `report.js` — Client-Side Renderer ✅

**File**: `src/DumpDetective.Reporting/Templates/report.js`  
**Action**: Created — full DOM-building renderer reading `window.__REPORT__`. All user strings use `textContent`. Implements: header, executive summary, developer plan, filter bar, finding cards, analyzer section dispatch (all 10 `SectionBlock` types), filter, table sort, copy-to-clipboard, JSON download, CSV export, print.  

Reads `window.__REPORT__` (`AnalysisReportDocument` JSON) and builds the entire DOM.  
**Security rule**: All user-originated strings (type names, stack frames, evidence text) use `textContent` assignment — never `innerHTML`.

**Responsibilities**:

| Responsibility | Input |
|---------------|-------|
| Render header card | `doc.dumpPath`, `doc.generatedAtUtc`, `doc.elapsedSeconds`, `doc.dedupDiagnostics` |
| Render trend block | `doc.isTrendReport`, `doc.trendDumpCount`, `doc.trendDumpPaths` |
| Render executive summary table | `doc.executiveSummary` (when non-null) |
| Render developer action plan | `doc.developerActionPlan` |
| Render table of contents | `doc.findings` (severity-grouped) + `doc.analyzerSections` |
| Render finding cards | `doc.findings` — severity badge, category pill, evidence/remediation |
| Render analyzer sections | `doc.analyzerSections` — collapsible `<details>` per section |
| Dispatch `SectionBlock` subtypes | `block.type` discriminator → `heading/metric/path/text/listItem/divider/blank/table/collapsibleBegin/collapsibleEnd` |
| Filter bar | Severity buttons (Critical / Warning / Info / All) + free-text search across title + evidence |
| Table sort | Click column header → toggle ascending/descending by `data-value` or text |
| Copy button | Copy hex address to clipboard; aria-live announcement via `#clipboard-status` |
| Download JSON | Blob URL from `window.__REPORT__`, download as `{filename}.json` |
| Export CSV | Flatten `doc.findings` to CSV, download as `{filename}-findings.csv` |
| Print | `window.print()` |

---

### E4 · Create `EmbeddedResourceLoader` ✅

**File**: `src/DumpDetective.Reporting/Formatters/EmbeddedResourceLoader.cs`  
**Action**: Created in Phase D.  

```csharp
internal static class EmbeddedResourceLoader
{
    internal static string LoadText(string resourceName)
    {
        Assembly asm = typeof(EmbeddedResourceLoader).Assembly;
        string fullName = $"DumpDetective.Reporting.Templates.{resourceName}";
        using Stream stream = asm.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {fullName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

Fields on `HtmlReportRenderer` are `static readonly` — loaded once on first access, reused for every subsequent render call.

---

## Phase F — Wire CLI Services ✅ Complete (implemented with Phase D)

---

### F1 · Create `DefaultSectionBuilderFactory` ✅

**File**: `src/DumpDetective.Cli/Services/DefaultSectionBuilderFactory.cs`  
**Action**: Created — all 17 builders registered.  

```csharp
internal sealed class DefaultSectionBuilderFactory : ISectionBuilderFactory
{
    public IReadOnlyList<IAnalyzerSectionBuilder> CreateBuilders() =>
    [
        new MemorySectionBuilder(),
        new GCGenerationSectionBuilder(),
        new SegmentSectionBuilder(),
        new ModuleSectionBuilder(),
        new CrashSectionBuilder(),
        new HangSectionBuilder(),
        new MemoryLeakSectionBuilder(),
        new CollectionSectionBuilder(),
        new StaticRootSectionBuilder(),
        new ReferenceChainSectionBuilder(),
        new GCHandleSectionBuilder(),
        new DependentHandleSectionBuilder(),
        new LohFragmentationSectionBuilder(),
        new ThreadStackClusterSectionBuilder(),
        new ThreadSectionBuilder(),
        new LockGraphSectionBuilder(),
        new EventLeakSectionBuilder()
    ];
}
```

---

### F2 · Rewrite `ReportBuilderFacade` ✅

**File**: `src/DumpDetective.Cli/Services/ReportBuilderFacade.cs`  
**Action**: Rewritten — uses `ReportSerializer` for single-dump path; bridges `ComposedReport` → `AnalysisReportDocument` for trend path until `TrendReportComposer` is migrated.  

- Replace `IAnalyzerReporterFactory` dependency → `ISectionBuilderFactory`
- Replace `ReportBuilder.ComposeCanonicalReport` call → `ReportSerializer.Serialize`
- Remove `_reporters` field; add `_builders` field from `ISectionBuilderFactory.CreateBuilders()`
- `BuildRenderedReport` and `BuildRenderedTrendReport` contracts to `BuildReportStage` are unchanged — they still return `string`

---

### F3 · Update `ServiceRegistration` ✅

**File**: `src/DumpDetective.Cli/Hosting/ServiceRegistration.cs`  
**Action**: Complete — `HtmlCanonicalReportFormatter` replaced by `HtmlReportRenderer` (Phase E templates now available). `IAnalyzerReporterFactory` removed; `ISectionBuilderFactory`, `ReportSerializer`, and `JsonCanonicalReportFormatter` registered.  

| Registration | Before | After |
|---|---|---|
| `IAnalyzerReporterFactory` | `DefaultAnalyzerReporterFactory` (singleton) | **Remove** |
| `IAnalyzerReporter` instances | 17 registrations | **Remove** |
| `ISectionBuilderFactory` | *(new)* | `DefaultSectionBuilderFactory` (singleton) |
| `ReportSerializer` | *(new)* | singleton |
| `IReportFormatter` registrations | `TextCanonicalReportFormatter`, `MarkdownCanonicalReportFormatter`, `HtmlCanonicalReportFormatter`, `JsonCanonicalReportFormatter` | replace `HtmlCanonicalReportFormatter` → `HtmlReportRenderer`; rest unchanged |

---

## Phase G — Delete Obsolete Infrastructure ✅ Complete

> All 26 files deleted after clean build and tests confirmed passing.

### Files to Delete

**Printers** (17 files):

| File | Replaced by |
|------|-------------|
| `Printers/MemoryPrinter.cs` | B1 |
| `Printers/GCGenerationPrinter.cs` | B2 |
| `Printers/SegmentPrinter.cs` | B3 |
| `Printers/ModulePrinter.cs` | B4 |
| `Printers/CrashPrinter.cs` | B5 |
| `Printers/HangPrinter.cs` | B6 |
| `Printers/MemoryLeakPrinter.cs` | B7 |
| `Printers/CollectionPrinter.cs` | B8 |
| `Printers/StaticRootPrinter.cs` | B9 |
| `Printers/ReferenceChainPrinter.cs` | B10 |
| `Printers/GCHandlePrinter.cs` | B11 |
| `Printers/DependentHandlePrinter.cs` | B12 |
| `Printers/LohFragmentationPrinter.cs` | B13 |
| `Printers/ThreadStackClusterPrinter.cs` | B14 |
| `Printers/ThreadPrinter.cs` | B15 |
| `Printers/LockGraphPrinter.cs` | B16 |
| `Printers/EventLeakPrinter.cs` | B17 |

**Writer infrastructure** (3 files):

| File | Reason |
|------|--------|
| `Output/OutputWriter.cs` | `IReportWriter` removed; CLI stages use `Console.WriteLine` directly |
| `Output/StructuredCaptureReportWriter.cs` | Reverse-parse hack eliminated |
| `Output/StructuredReportWriterExtensions.cs` | Extensions on deleted interface |

**Interfaces and factories** (4 files):

| File | Reason |
|------|--------|
| `Core/Abstractions/IReportWriter.cs` | Both `IReportWriter` and `IStructuredReportWriter` removed |
| `Core/Abstractions/IAnalyzerReporter.cs` | Replaced by `IAnalyzerSectionBuilder` |
| `Cli/Services/IAnalyzerReporterFactory.cs` | Replaced by `ISectionBuilderFactory` |
| `Cli/Services/DefaultAnalyzerReporterFactory.cs` | Replaced by `DefaultSectionBuilderFactory` |

**Services and models** (2 files):

| File | Reason |
|------|--------|
| `Reporting/Services/ReportBuilder.cs` | Replaced by `ReportSerializer` |
| `Reporting/Models/ComposedReport.cs` | Replaced by `AnalysisReportDocument` |

### Types Deleted from `ComposedReport.cs`

`ComposedReport`, `ReportSection`, `ReportEvidenceRow`, `ExecutiveSummaryItem`, `DeveloperActionItem`,
`DetailedAnalyzerSection`, `DetailedAnalyzerSubmodule`, `DetailedAnalyzerSubmoduleKind`,
`DetailedAnalyzerTableData`, `DetailedAnalyzerTableRow`, `DetailedAnalyzerTableCell`,
`DedupDiagnostics`, `ReportContractVersions`

---

## Phase H — Update Tests ✅ Complete

---

### H1 · Regenerate HTML Golden Baselines ✅

**Action**: `GoldenFileTests.cs` updated — format code 2 now uses `JsonCanonicalReportFormatter`; golden folder renamed `Json`; old `.html.golden` files deleted. 15/15 golden cases pass after `UPDATE_GOLDENS=1` run.  

**Old strategy**: Compare full rendered HTML string against `.html.golden` file.  
**New strategy**: Extract the JSON payload from `<script>window.__REPORT__ = ...;</script>` in the rendered output, compare against a `.json.golden` file.

This makes HTML golden tests **stable across all CSS and JS changes** — only data changes cause failures.

**Files to delete and replace**:
- `Tests/Golden/Baselines/Html/BaselineSmall.html.golden` → `BaselineSmall.json.golden`
- `Tests/Golden/Baselines/Html/DuplicateHeavy.html.golden` → `DuplicateHeavy.json.golden`
- `Tests/Golden/Baselines/Html/LongNames.html.golden` → `LongNames.json.golden`
- `Tests/Golden/Baselines/Html/MixedSeverity.html.golden` → `MixedSeverity.json.golden`
- `Tests/Golden/Baselines/Html/RichEvidence.html.golden` → `RichEvidence.json.golden`

---

### H2 · Add `AnalysisReportDocument` Schema Tests ✅

**File**: `tests/DumpDetective.Tests/ReportDocumentSchemaTests.cs`  
**Action**: Created — 5 tests covering round-trip serialization, polymorphic `SectionBlock` deserialization, trend fields, null executive summary, and camelCase JSON shape. All pass.  

Deserialize a known `AnalysisReportDocument` JSON and assert field values.  
These become the primary regression tests for the reporting layer — format-independent.

---

### H3 · Add `SectionBuilder` Unit Tests ✅

**File**: `tests/DumpDetective.Tests/SectionBuilderTests.cs`  
**Action**: Created — 7 tests covering `MemorySectionBuilder`, `CrashSectionBuilder`, `GCHandleSectionBuilder`, `CollectionSectionBuilder`, `LohFragmentationSectionBuilder` (block structure, `CanHandle` routing, table row content). All pass.  

Per-builder pattern:
1. Construct a known `AnalyzerDomainResult` with controlled field values
2. Call `builder.Build(result)`
3. Assert returned `AnalyzerDetailSection.Blocks` contains expected `SectionBlock` subtypes in order
4. Assert key `MetricBlock` and `TableBlock` values match expected strings

No writer involved, no text parsing.

---

### H4 · Re-baseline Text and Markdown Golden Files ✅

**Action**: Re-baselined via `UPDATE_GOLDENS=1` after Phase G deletions. All 10 text/markdown golden cases pass.  

Text and Markdown golden files remain functionally equivalent but are re-baselined.  
The rendering now walks `AnalyzerDetailSection.Blocks` instead of `DetailedAnalyzerSubmodule` lists.  
Content should be identical modulo minor whitespace normalization.

---

## Adding a New Analyzer (Post-Refactor)

When a new analyzer is added (e.g. `StringAnalyzer` from Priority 1):

1. Analyzer produces `StringDomainResult` in `DumpDetective.Analysis` *(unchanged pattern)*
2. `IFindingGenerator` implementation added in `Reporting/FindingGenerators/` *(unchanged pattern)*
3. `IAnalyzerSectionBuilder` implementation added in `Reporting/SectionBuilders/StringSectionBuilder.cs` *(new pattern)*
4. `DefaultSectionBuilderFactory.CreateBuilders()` adds `new StringSectionBuilder()`
5. `report.js` adds a rendering handler for the new `type` discriminator — **no C# changes needed for HTML**
6. `ReportSerializer.BuildConfidenceNotes()` updated if the new analyzer emits cap/limit signals

**No changes required in**: `IAnalyzer`, `AnalysisContext`, `ReportSerializer` core logic, `HtmlReportRenderer`, `IReportFormatter`, `TextCanonicalReportFormatter`, `MarkdownCanonicalReportFormatter`

---

## Summary

| Metric | Value |
|--------|-------|
| Files created | 28 |
| Files modified | 8 |
| Files deleted | 25 |
| Net line change estimate | −1 800 lines |
| Analysis pipeline touched | **No** |
| Performance impact on dump scanning | **None** |
