using DumpDetective.Core.Abstractions;

namespace DumpDetective.Reporting.Output;

internal static class StructuredReportWriterExtensions
{
    public static void WriteSubHeading(this IReportWriter writer, string title, int indentLevel = 0)
    {
        if (writer is IStructuredReportWriter structured)
        {
            structured.WriteDetailHeading(title, indentLevel);
            return;
        }

        writer.WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{title}");
    }

    public static void WriteMetric(this IReportWriter writer, string label, string value, int indentLevel = 0)
    {
        if (writer is IStructuredReportWriter structured)
        {
            structured.WriteDetailMetric(label, value, indentLevel);
            return;
        }

        writer.WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{label}: {value}");
    }

    public static void WritePathMetric(this IReportWriter writer, string label, string value, int indentLevel = 0)
    {
        if (writer is IStructuredReportWriter structured)
        {
            structured.WriteDetailPath(label, value, indentLevel);
            return;
        }

        writer.WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{label}: {value}");
    }

    public static void WriteDetailText(this IReportWriter writer, string text, int indentLevel = 0)
    {
        if (writer is IStructuredReportWriter structured)
        {
            structured.WriteDetailText(text, indentLevel);
            return;
        }

        writer.WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{text}");
    }

    public static void WriteDetailBullet(this IReportWriter writer, string text, int indentLevel = 0)
    {
        if (writer is IStructuredReportWriter structured)
        {
            structured.WriteDetailListItem(text, indentLevel);
            return;
        }

        writer.WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}- {text}");
    }

    public static void WriteDetailDivider(this IReportWriter writer)
    {
        if (writer is IStructuredReportWriter structured)
        {
            structured.WriteDetailDivider();
            return;
        }

        writer.WriteSeparator();
    }

    public static void WriteDetailBlank(this IReportWriter writer)
    {
        if (writer is IStructuredReportWriter structured)
        {
            structured.WriteDetailBlank();
            return;
        }

        writer.WriteLine(string.Empty);
    }

    public static void WriteDetailTable(this IReportWriter writer, DumpDetective.Reporting.Models.DetailedAnalyzerTableData tableData)
    {
        if (writer is StructuredCaptureReportWriter capture)
        {
            capture.WriteDetailTable(tableData);
            return;
        }
        if (writer is OutputWriter output)
        {
            output.WriteDetailTable(tableData);
            return;
        }
        // Generic text fallback
        if (tableData.Caption is not null)
            writer.WriteLine(tableData.Caption);
        if (tableData.Headers.Count > 0)
            writer.WriteLine(string.Join("  ", tableData.Headers.Select((h, i) => i == 0 ? $"{h,-60}" : $"{h,14}")));
        foreach (DumpDetective.Reporting.Models.DetailedAnalyzerTableRow row in tableData.Rows)
            writer.WriteLine(string.Join("  ", row.Cells.Select((c, i) => i == 0 ? $"{c.Display,-60}" : $"{c.Display,14}")));
    }
}
