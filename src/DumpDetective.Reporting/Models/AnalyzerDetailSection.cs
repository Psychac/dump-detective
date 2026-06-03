using System.Text.Json.Serialization;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Models;

// ── Contract-slot types (per-section structured data) ────────────────────────

/// <summary>The top finding for this section — always rendered above the collapsible detail.</summary>
internal sealed record SectionLeadFinding(
    string Severity,
    string Title,
    string Summary,
    string Recommendation,
    string ConfidenceSymbol,
    double ConfidenceScore,
    IReadOnlyList<string> Caveats);

/// <summary>A single key metric shown in the always-visible metrics strip.</summary>
internal sealed record SectionKeyMetric(
    string Label,
    string Value,
    double? RawValue = null);

/// <summary>A titled data table extracted from the section's block stream.</summary>
internal sealed record SectionTable(
    string Title,
    IReadOnlyList<string> Headers,
    IReadOnlyList<TableRow> Rows,
    int RowLimit = 20);

/// <summary>Run provenance — duration, scan count, cache stats — shown collapsed at the bottom of the section.</summary>
internal sealed record SectionProvenance(
    string Analyzer,
    string Status,
    double DurationMs,
    long ObjectScanCount,
    long CacheHits,
    long CacheMisses,
    IReadOnlyList<string>? CappingNotes = null);

// ─────────────────────────────────────────────────────────────────────────────

internal sealed record AnalyzerDetailSection(
    string AnalyzerName,
    string DisplayTitle,
    int SortOrder,
    IReadOnlyList<SectionBlock> Blocks,      // Narrative blocks after metrics+tables are extracted to typed slots
    string SectionId = "",                   // Stable anchor e.g. "A1", "B4" — set by ReportSerializer via SectionIdDomainMap
    string Domain = "",                      // "Memory" | "GC" | "Leaks" | "Threads" | "Async" | "Exceptions" | "Runtime" | "TypeSystem"
    FindingSeverity? LeadSeverity = null,    // Severity of the lead finding (null = informational only)
    SectionLeadFinding? LeadFinding = null,  // Always-visible top finding — null when section has no findings
    IReadOnlyList<SectionKeyMetric>? KeyMetrics = null, // Always-visible metric strip
    IReadOnlyList<SectionTable>? Tables = null,         // Data tables, each collapsible
    SectionProvenance? Provenance = null);  // Run provenance — collapsed footer

// Discriminated union root — each subtype carries only what it needs
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HeadingBlock), "heading")]
[JsonDerivedType(typeof(MetricBlock), "metric")]
[JsonDerivedType(typeof(PathBlock), "path")]
[JsonDerivedType(typeof(StackFrameBlock), "stackframe")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ListItemBlock), "listItem")]
[JsonDerivedType(typeof(DividerBlock), "divider")]
[JsonDerivedType(typeof(BlankBlock), "blank")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(ChartBlock), "chart")]
[JsonDerivedType(typeof(ConfidenceBandBlock), "confidenceBand")]
[JsonDerivedType(typeof(CollapsibleSectionBeginBlock), "collapsibleBegin")]
[JsonDerivedType(typeof(CollapsibleSectionEndBlock), "collapsibleEnd")]
[JsonDerivedType(typeof(SparklineBlock), "sparkline")]
internal abstract record SectionBlock;

internal sealed record HeadingBlock(string Text, int IndentLevel = 0) : SectionBlock;
internal sealed record MetricBlock(string Label, string Value, double? RawValue = null, int IndentLevel = 0) : SectionBlock;
internal sealed record PathBlock(string Label, string Path, int IndentLevel = 0) : SectionBlock;
internal sealed record StackFrameBlock(string Frame, int IndentLevel = 0, bool IsFrameworkFrame = false) : SectionBlock;
internal sealed record TextBlock(string Text, int IndentLevel = 0) : SectionBlock;
internal sealed record ListItemBlock(string Text, int IndentLevel = 0) : SectionBlock;
internal sealed record DividerBlock : SectionBlock;
internal sealed record BlankBlock : SectionBlock;

internal sealed record TableBlock(
    string? Caption,
    IReadOnlyList<string> Headers,
    IReadOnlyList<TableRow> Rows) : SectionBlock;

internal sealed record ChartBlock(
    string Title,
    string Kind,
    string PayloadJson,
    int IndentLevel = 0) : SectionBlock;

internal sealed record ConfidenceBandBlock(
    string Band,
    double Score,
    string Symbol,
    string[] Caveats) : SectionBlock;

internal sealed record TableRow(IReadOnlyList<TableCell> Cells);
internal sealed record TableCell(string Display, long? RawValue = null, string? LinkTarget = null);   // RawValue for client-side sort; LinkTarget for anchored links

internal sealed record CollapsibleSectionBeginBlock(string Title) : SectionBlock;
internal sealed record CollapsibleSectionEndBlock : SectionBlock;

internal sealed record SparklineBlock(
    string MetricKey,
    string Unit,
    IReadOnlyList<double> Values,
    string Direction) : SectionBlock;  // Direction: "HigherIsWorse" | "LowerIsWorse" | "Neutral"
