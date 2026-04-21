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
}
