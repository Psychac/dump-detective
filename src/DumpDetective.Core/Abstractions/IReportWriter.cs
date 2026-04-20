namespace DumpDetective.Core.Abstractions;

internal interface IReportWriter
{
    void WriteLine(string? value);
    void WriteHeader(string title);
    void WriteSeparator();
}
