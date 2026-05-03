using System.Text.Json.Serialization;

namespace DumpDetective.Reporting.Models;

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
[JsonDerivedType(typeof(StackFrameBlock),             "stackframe")]
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
internal sealed record StackFrameBlock(string Frame, int IndentLevel = 0, bool IsFrameworkFrame = false) : SectionBlock;
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
