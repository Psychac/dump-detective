using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal abstract class SectionBuilderBase
{
    protected static HeadingBlock H(string text, int indent = 0) => new(text, indent);
    protected static MetricBlock M(string label, string value, double? raw = null, int indent = 0) => new(label, value, raw, indent);
    protected static TextBlock T(string text, int indent = 0) => new(text, indent);
    protected static ListItemBlock Li(string text, int indent = 0) => new(text, indent);
    protected static DividerBlock Divider() => new();
    protected static BlankBlock Blank() => new();
    protected static ChartBlock Chart(string title, string kind, string payloadJson, int indent = 0) => new(title, kind, payloadJson, indent);
    protected static CollapsibleSectionBeginBlock CollapseBegin(string title) => new(title);
    protected static CollapsibleSectionEndBlock CollapseEnd() => new();
    protected static TableRow Row(params TableCell[] cells) => new(cells);
    protected static TableCell Cell(string display, long? raw = null) => new(display, raw);
}
