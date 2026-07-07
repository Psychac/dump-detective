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
/// Numeric columns should be emitted as raw numbers; formatting belongs in the client layer.</summary>
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

// ── Typed structured-data slots ───────────────────────────────────────────────

/// <summary>A single frame in a named stack trace.</summary>
internal sealed record StackFrameEntry(
    int Index,
    string Text,
    bool IsFramework);

/// <summary>A named thread/finalizer stack with metadata key-value pairs and frames.</summary>
internal sealed record NamedStackTrace(
    string Label,       // e.g. "Finalizer Thread — BLOCKED", "Thread 42 (OS 1234) — Blocked | 2 locks"
    string Category,    // "Finalizer" | "Sampled" | "Captured" | "Blocked"
    IReadOnlyDictionary<string, string> Meta,  // {"State":"Waiting","GC Mode":"Preemptive","Locks":"2",…}
    IReadOnlyList<StackFrameEntry> Frames,
    bool Truncated);

/// <summary>A single GC root path from root through intermediate types to a target object.</summary>
internal sealed record RootPath(
    string RootKind,
    string TargetAddress,   // hex string e.g. "0x1A2B3C"
    int PathLength,
    bool WasCapped,
    IReadOnlyList<string> Hops);  // type names, root-first; final entry is target type

/// <summary>All root paths reaching a particular target type, grouped for display.</summary>
internal sealed record RootPathGroup(
    string TargetType,         // fully qualified
    string TargetTypeShort,    // simple (last segment) name
    int TotalPathCount,        // total paths in group before any take() limit
    bool AnyCapped,
    IReadOnlyList<RootPath> Paths);  // top paths (limited to 3)

/// <summary>A single scored leak candidate with pre-computed explanation and impact text.</summary>
internal sealed record LeakCandidateCard(
    string TypeName,
    string Severity,
    string Classification,
    double SuspicionScore,
    long InstanceCount,
    ulong TotalSize,
    double Gen2Pct,
    string? RootKind,
    bool IsFinalizable,
    bool IsContainer,
    double ReferenceFieldRatio,
    string ExplanationText,
    string ImpactBand,
    string GcImpactNote,
    string LohImpactNote);

/// <summary>Subscriber type/method detail row embedded in an EventLeakInstanceCard.</summary>
internal sealed record SubscriberDetailEntry(
    string Type,
    string? MethodName,
    int Count,
    ulong Size);

/// <summary>Per-publisher event group summary with embedded subscriber type breakdown.</summary>
internal sealed record EventLeakGroupCard(
    string PublisherType,
    string EventFieldName,
    bool IsStatic,
    int SeverityScore,
    int InstanceCount,
    int TotalSubscribers,
    double AverageSubscribers,
    int MinSubscribers,
    int MaxSubscribers,
    double Gen2PublisherPercent,
    ulong EstimatedRetainedBytes,
    bool HasDuplicateSubscriptions,
    bool HasLifetimeMismatch,
    int OrphanedSubscriberInstances,
    IReadOnlyList<SubscriberDetailEntry> TopSubscriberTypes);

/// <summary>Per-publisher instance drill-down with optional per-subscriber details.</summary>
internal sealed record EventLeakInstanceCard(
    string PublisherType,
    string EventFieldName,
    bool IsStatic,
    string PublisherAddress,
    int SeverityScore,
    int SubscriberCount,
    string? RootHint,
    int PublisherGeneration,
    int DuplicateSubscriptionCount,
    int OrphanedSubscriberCount,
    bool HasLifetimeMismatch,
    IReadOnlyList<SubscriberDetailEntry>? SubscriberDetails);

/// <summary>A cluster of threads sharing the same stack signature.</summary>
internal sealed record StackCluster(
    int ThreadCount,
    IReadOnlyList<string> OsThreadIds,
    string Signature,
    bool Truncated);

/// <summary>An on-disk artifact produced by an analyzer for offline inspection.</summary>
internal sealed record AnalyzerArtifact(
    string FileName,
    string Instructions);

/// <summary>A per-type sample trace entry with optional parsed GC root hop chain.</summary>
internal sealed record TypeSampleTrace(
    string TypeName,
    int Count,
    ulong TotalSizeBytes,
    string? SampleAddress,       // hex string; null when sample unavailable
    ulong SampleObjectSize,
    bool HasGcRoot,
    IReadOnlyList<string>? RootHops,  // parsed hop list root-first; null when no root found
    bool TraversalLimited,
    string StatusLabel);  // "GC root found" | "No root (search limit)" | "No root" | "Sample unavailable"

// ─────────────────────────────────────────────────────────────────────────────

internal sealed record AnalyzerDetailSection(
    string AnalyzerName,
    string DisplayTitle,
    int SortOrder,
    IReadOnlyList<SectionBlock> Blocks,      // Narrative prose: TextBlock, ConfidenceBand, H headings only
    string SectionId = "",                   // Stable anchor e.g. "A1", "B4" — set by ReportSerializer via SectionIdDomainMap
    string Domain = "",                      // "Memory" | "GC" | "Leaks" | "Threads" | "Async" | "Exceptions" | "Runtime" | "TypeSystem"
    FindingSeverity? LeadSeverity = null,    // Severity of the lead finding (null = informational only)
    SectionLeadFinding? LeadFinding = null,  // Always-visible top finding — null when section has no findings
    IReadOnlyDictionary<string, MetricValue>? KeyMetrics = null, // Always-visible metric strip (map: snake_case -> value)
    // Legacy typed tables removed: producers should populate `CompactTables` only.
    SectionProvenance? Provenance = null,    // Run provenance — collapsed footer
    IReadOnlyList<CompactTable>? CompactTables = null,           // Compact table representation (preferred)
    IReadOnlyList<NamedStackTrace>? StackTraces = null,          // Named thread/stack traces (replaces H+SF[] blocks)
    IReadOnlyList<RootPathGroup>? RootPathGroups = null,         // GC root paths grouped by target type (replaces nested collapses)
    IReadOnlyList<TypeSampleTrace>? TypeTraces = null,           // Per-type sample traces with root chains (replaces collapse+M[] blocks)
    IReadOnlyList<LeakCandidateCard>? LeakCandidateCards = null, // Scored leak candidates with explanation+impact text
    IReadOnlyList<EventLeakGroupCard>? EventLeakGroupCards = null,       // Event leak per-group drill-down
    IReadOnlyList<EventLeakInstanceCard>? EventLeakInstanceCards = null, // Event leak per-instance drill-down
    IReadOnlyList<StackCluster>? StackClusters = null,           // Thread stack signature clusters
    IReadOnlyList<AnalyzerArtifact>? Artifacts = null);          // On-disk artifacts produced by the analyzer

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
internal sealed record TableCell(string Display, double? RawValue = null, string? LinkTarget = null);   // RawValue for client-side sort; LinkTarget for anchored links

internal sealed record CollapsibleSectionBeginBlock(string Title) : SectionBlock;
internal sealed record CollapsibleSectionEndBlock : SectionBlock;

internal sealed record SparklineBlock(
    string MetricKey,
    string Unit,
    IReadOnlyList<double> Values,
    string Direction) : SectionBlock;  // Direction: "HigherIsWorse" | "LowerIsWorse" | "Neutral"
