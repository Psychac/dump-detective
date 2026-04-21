namespace DumpDetective.Core.Abstractions;

internal interface IReportWriter
{
    void WriteLine(string? value);
    void WriteHeader(string title);
    void WriteSeparator();
}

internal interface IStructuredReportWriter : IReportWriter
{
    void WriteDetailHeading(string title, int indentLevel = 0);
    void WriteDetailMetric(string label, string value, int indentLevel = 0);
    void WriteDetailPath(string label, string path, int indentLevel = 0);
    void WriteDetailText(string text, int indentLevel = 0);
    void WriteDetailListItem(string text, int indentLevel = 0);
    void WriteDetailDivider();
    void WriteDetailBlank();
}
