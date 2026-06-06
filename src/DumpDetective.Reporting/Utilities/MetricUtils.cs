using DumpDetective.Core.Enums;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Utilities;

internal static class MetricUtils
{

    public static double ParseNumericFallback(string? textual)
    {
        if (string.IsNullOrWhiteSpace(textual)) return 0.0;
        var cleaned = new System.Text.StringBuilder();
        foreach (char c in textual)
            if (char.IsDigit(c) || c == '.' || c == '-') cleaned.Append(c);

        if (double.TryParse(cleaned.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
            return d;
        return 0.0;
    }

    public static MetricUnit InferUnitFromLabel(string label, string value)
    {
        string l = (label ?? string.Empty).ToLowerInvariant();
        string v = (value ?? string.Empty).Trim();
        if (l.Contains("byte") || v.EndsWith("b", System.StringComparison.OrdinalIgnoreCase) || v.EndsWith("kb", System.StringComparison.OrdinalIgnoreCase) || v.EndsWith("mb", System.StringComparison.OrdinalIgnoreCase) || v.EndsWith("gb", System.StringComparison.OrdinalIgnoreCase))
            return MetricUnit.Bytes;
        if (v.EndsWith("ms", System.StringComparison.OrdinalIgnoreCase) || l.Contains("duration") || l.Contains("latency") || l.Contains("elapsed"))
            return MetricUnit.Milliseconds;
        if (v.EndsWith("%") || l.Contains("percent") || l.Contains("pct"))
            return MetricUnit.Percent;
        return MetricUnit.Count;
    }
}

