using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using System.Text;

namespace DumpDetective.Reporting.Output;

internal sealed class OutputWriter(TextWriter? writer, bool writeToConsoleWhenNoWriter = true) : TextWriter, IStructuredReportWriter
{
    private readonly TextWriter? _writer = writer;
    private readonly bool _writeToConsoleWhenNoWriter = writeToConsoleWhenNoWriter;

    public override Encoding Encoding => _writer?.Encoding ?? Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        _writer?.WriteLine(value);

        if (_writer == null && _writeToConsoleWhenNoWriter)
        {
            Console.WriteLine(value);
        }
    }

    public void WriteHeader(string title)
    {
        WriteLine($"\n{title}");
        WriteLine(StringConstants.Equals80);
    }

    public void WriteSeparator()
    {
        WriteLine(StringConstants.Separator80);
    }

    public void WriteDetailHeading(string title, int indentLevel = 0)
    {
        WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{title}");
    }

    public void WriteDetailMetric(string label, string value, int indentLevel = 0)
    {
        WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{label}: {value}");
    }

    public void WriteDetailPath(string label, string path, int indentLevel = 0)
    {
        WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{label}: {path}");
    }

    public void WriteDetailText(string text, int indentLevel = 0)
    {
        WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{text}");
    }

    public void WriteDetailListItem(string text, int indentLevel = 0)
    {
        WriteLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}- {text}");
    }

    public void WriteDetailDivider()
    {
        WriteSeparator();
    }

    public void WriteDetailBlank()
    {
        WriteLine(string.Empty);
    }
}


