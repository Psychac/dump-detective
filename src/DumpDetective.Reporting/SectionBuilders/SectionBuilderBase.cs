using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Utilities;

namespace DumpDetective.Reporting.SectionBuilders;

internal abstract class SectionBuilderBase
{
    // ── Narrative block helpers ───────────────────────────────────────────────
    protected static HeadingBlock H(string text, int indent = 0) => new(text, indent);
    /// <summary>MetricBlock — use only inside CollapseBegin/End for per-item detail.
    /// Top-level section KPIs should use <see cref="KM"/> instead.</summary>
    protected static MetricBlock M(string label, string value, double? raw = null, int indent = 0) => new(label, value, raw, indent);
    protected static TextBlock T(string text, int indent = 0) => new(text, indent);
    protected static StackFrameBlock SF(string frame, int indent = 0, bool isFramework = false) => new(frame, indent, isFramework);
    protected static ConfidenceBandBlock BuildConfidenceBand(double? score, IReadOnlyList<string>? caveats)
    {
        double resolvedScore = score ?? 0.5;
        string band   = resolvedScore >= 0.85 ? "High" : resolvedScore >= 0.65 ? "Med-High" : resolvedScore >= 0.45 ? "Medium" : "Low";
        string symbol = resolvedScore >= 0.85 ? "●●●●" : resolvedScore >= 0.65 ? "●●●○" : resolvedScore >= 0.45 ? "●●○○" : "●○○○";
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
        => raw.HasValue
            ? new(label, new NumericMetricValue(raw.Value, MetricUtils.InferUnitFromLabel(label, value), string.IsNullOrWhiteSpace(value) ? null : value))
            : new(label, new TextMetricValue(value));

    /// <summary>Creates a numeric key metric with a formatted display string.</summary>
    protected static SectionKeyMetric KM(string label, double value, MetricUnit unit, string? formatted = null)
        => new(label, new NumericMetricValue(value, unit, formatted));

    /// <summary>Creates a numeric key metric when the value may be absent.</summary>
    protected static SectionKeyMetric KM(string label, double? value, MetricUnit unit, string? formatted = null, string fallbackText = "N/A")
        => value.HasValue
            ? new(label, new NumericMetricValue(value.Value, unit, formatted))
            : new(label, new TextMetricValue(fallbackText));

    /// <summary>Creates a categorical/enum-like key metric.</summary>
    protected static SectionKeyMetric KMEnum(string label, string value, string? enumType = null)
        => new(label, new EnumMetricValue(value, enumType));

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

    // Convert a human label like "Total Bytes" to a stable snake_case key: "total_bytes"
    protected static string ToSnakeCase(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        var sb = new System.Text.StringBuilder();
        bool lastWasUnderscore = false;
        foreach (char ch in label)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }
        var res = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(res) ? label : res;
    }

    // NOTE: `AsKeyMap` removed — builders now create `KeyMetrics` dictionaries directly.
}
