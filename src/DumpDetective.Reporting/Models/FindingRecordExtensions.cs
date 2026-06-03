using System.Linq;

namespace DumpDetective.Reporting.Models;

internal static class FindingRecordExtensions
{
    public static string GetSummaryText(this FindingRecord f)
    {
        if (f.Details is { Count: > 0 })
            return f.Details[0];

        return string.Empty;
    }

    public static string GetDetailsJoined(this FindingRecord f)
    {
        if (f.Details is { Count: > 0 })
            return string.Join(" ", f.Details);

        return string.Empty;
    }
}
