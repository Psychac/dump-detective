using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using DumpDetective.Core.Enums;

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
    MetricValue Value);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NumericMetricValue), "number")]
[JsonDerivedType(typeof(TextMetricValue), "text")]
[JsonDerivedType(typeof(EnumMetricValue), "enum")]
internal abstract record MetricValue;

internal sealed record NumericMetricValue(
    double Value,
    MetricUnit Unit,
    string? Formatted = null) : MetricValue;

internal sealed record TextMetricValue(
    string Value) : MetricValue;

internal sealed record EnumMetricValue(
    string Value,
    string? EnumType = null) : MetricValue;

/// <summary>A titled data table extracted from the section's block stream.</summary>
/// <summary>Compact table header metadata for the compact table representation.</summary>
internal sealed record CompactHeader(
    string Name,
    string? Type = "string",
    string? Format = null,
    bool Sortable = true);

/// <summary>Compact row as a dense array of primitive values (strings, numbers, nulls).
/// Values are interpreted according to the corresponding header metadata.</summary>
internal sealed record CompactRow(object?[] Values);

/// <summary>Compact table: headers carry typing/formatting metadata and rows are arrays-of-values.
/// This representation is much smaller on the wire than per-cell objects.</summary>
internal sealed record CompactTable(
    string Title,
    IReadOnlyList<CompactHeader> Headers,
    IReadOnlyList<CompactRow> Rows,
    int RowLimit = 20);

internal static class CompactTableExtensions
{
    // Legacy translators removed; producers now emit CompactTable directly.
}

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
    IReadOnlyDictionary<string, MetricValue>? KeyMetrics = null, // Always-visible metric strip (map: snake_case -> value)
    // Legacy typed tables removed: producers should populate `CompactTables` only.
    SectionProvenance? Provenance = null,  // Run provenance — collapsed footer
    IReadOnlyList<CompactTable>? CompactTables = null); // Compact table representation (preferred)

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
