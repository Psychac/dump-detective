namespace DumpDetective.Core.Utilities;
internal static class FormatHelper
{
public static string FormatBytes(ulong bytes)
{
    string[] sizes = { "B", "KB", "MB", "GB", "TB" };
    double len = bytes;
    int order = 0;
    while (len >= 1024 && order < sizes.Length - 1)
    {
        order++;
        len = len / 1024;
    }
    return $"{len:0.##} {sizes[order]}";
}

public static string TruncateString(string str, int maxLength)
{
    if (string.IsNullOrEmpty(str) || str.Length <= maxLength)
        return str;
    return str.Substring(0, maxLength - 3) + "...";
}

public static string FormatMetricValue(double value, string unit)
{
    if (double.IsNaN(value)) return "N/A";
    if (unit == "bytes") return FormatBytes((ulong)Math.Max(0, value));
    if (unit == "%") return $"{value:F1}%";
    return $"{value:F0} {unit}";
}

public static string FormatDeltaValue(double delta, string unit)
{
    string sign = delta >= 0 ? "+" : "-";
    double abs = Math.Abs(delta);
    if (unit == "bytes") return $"{sign}{FormatBytes((ulong)abs)}";
    if (unit == "%") return $"{(delta >= 0 ? "+" : string.Empty)}{delta:F1}%";
    return $"{(delta >= 0 ? "+" : string.Empty)}{delta:F0} {unit}";
}
}
