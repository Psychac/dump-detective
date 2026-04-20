using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using System.Text;

namespace DumpDetective.Reporting.Output;

internal sealed class OutputWriter(TextWriter? writer, bool writeToConsoleWhenNoWriter = true) : TextWriter, IReportWriter
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
}


