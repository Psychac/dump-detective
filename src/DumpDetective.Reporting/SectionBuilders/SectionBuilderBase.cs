using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal abstract class SectionBuilderBase
{
    // ── Narrative block helpers ───────────────────────────────────────────────
    protected static HeadingBlock H(string text, int indent = 0) => new(text, indent);
    /// <summary>MetricBlock — use only inside CollapseBegin/End for per-item detail.
    /// Top-level section KPIs should use <see cref="KM"/> instead.</summary>
    protected static MetricBlock M(string label, string value, double? raw = null, int indent = 0) => new(label, value, raw, indent);
    protected static TextBlock T(string text, int indent = 0) => new(text, indent);
    protected static ConfidenceBandBlock BuildConfidenceBand(double? score, IReadOnlyList<string>? caveats)
    {
        double resolvedScore = score ?? 0.5;
        string band = resolvedScore >= 0.8 ? "High" : resolvedScore >= 0.5 ? "Medium" : "Low";
        string symbol = resolvedScore >= 0.8 ? "★★★☆" : resolvedScore >= 0.5 ? "★★☆☆" : "★☆☆☆";
        return new ConfidenceBandBlock(band, resolvedScore, symbol, caveats?.ToArray() ?? []);
    }
    protected static ListItemBlock Li(string text, int indent = 0) => new(text, indent);
    protected static DividerBlock Divider() => new();
    protected static BlankBlock Blank() => new();
    protected static ChartBlock Chart(string title, string kind, string payloadJson, int indent = 0) => new(title, kind, payloadJson, indent);
    protected static CollapsibleSectionBeginBlock CollapseBegin(string title) => new(title);
    protected static CollapsibleSectionEndBlock CollapseEnd() => new();
    protected static TableRow Row(params TableCell[] cells) => new(cells);
    protected static TableCell Cell(string display, long? raw = null) => new(display, raw);

    // ── Typed contract-slot helpers ───────────────────────────────────────────

    /// <summary>Creates a key metric for the always-visible KPI strip.</summary>
    protected static SectionKeyMetric KM(string label, string value, double? raw = null)
        => new(label, value, raw);

    /// <summary>Creates a typed section table (collapsed by default in HTML).</summary>
    protected static SectionTable ST(
        string title,
        IReadOnlyList<string> headers,
        IReadOnlyList<TableRow> rows,
        int rowLimit = 20)
        => new(title, headers, rows, rowLimit);

    // ── Formatting helpers ────────────────────────────────────────────────────
    protected static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F2} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }

    protected static string FormatBytes(long bytes) => FormatBytes((ulong)Math.Max(0, bytes));

    protected static string FormatRatio(ulong part, ulong total)
        => total == 0 ? "0.0%" : $"{part * 100.0 / total:F1}%";

    protected static double RatioValue(ulong part, ulong total)
        => total == 0 ? 0.0 : part * 100.0 / total;
}
