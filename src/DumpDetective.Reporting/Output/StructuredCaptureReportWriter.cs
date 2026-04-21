using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;
using System.Text;

namespace DumpDetective.Reporting.Output;

internal sealed class StructuredCaptureReportWriter : IStructuredReportWriter
{
    private readonly StringBuilder _buffer = new(capacity: 4 * 1024);
    private readonly List<DetailedAnalyzerSubmodule> _submodules = [];

    private void AppendRawLine(string line)
    {
        _buffer.AppendLine(line);
    }

    public void WriteLine(string? value)
    {
        string line = value ?? string.Empty;
        AppendRawLine(line);

        string trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Empty, null, null, null));
            return;
        }

        if (string.Equals(trimmed, StringConstants.Equals80, StringComparison.Ordinal) ||
            string.Equals(trimmed, StringConstants.Separator80, StringComparison.Ordinal))
        {
            _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Divider, null, null, null));
            return;
        }

        int indentLevel = line.TakeWhile(char.IsWhiteSpace).Count() / 2;
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Text, null, null, line.TrimStart(), indentLevel));
    }

    public void WriteHeader(string title)
    {
        AppendRawLine(string.Empty);
        AppendRawLine(title);
        AppendRawLine(StringConstants.Equals80);
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Heading, null, null, title));
    }

    public void WriteSeparator()
    {
        AppendRawLine(StringConstants.Separator80);
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Divider, null, null, null));
    }

    public void WriteDetailHeading(string title, int indentLevel = 0)
    {
        AppendRawLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{title}");
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Heading, null, null, title, indentLevel));
    }

    public void WriteDetailMetric(string label, string value, int indentLevel = 0)
    {
        AppendRawLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{label}: {value}");
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Metric, label, value, null, indentLevel));
    }

    public void WriteDetailPath(string label, string path, int indentLevel = 0)
    {
        AppendRawLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{label}: {path}");
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Path, label, path, null, indentLevel));
    }

    public void WriteDetailText(string text, int indentLevel = 0)
    {
        AppendRawLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}{text}");
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Text, null, null, text, indentLevel));
    }

    public void WriteDetailListItem(string text, int indentLevel = 0)
    {
        AppendRawLine($"{new string(' ', Math.Max(0, indentLevel) * 2)}- {text}");
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.ListItem, null, null, text, indentLevel));
    }

    public void WriteDetailDivider()
    {
        WriteSeparator();
    }

    public void WriteDetailBlank()
    {
        AppendRawLine(string.Empty);
        _submodules.Add(new DetailedAnalyzerSubmodule(DetailedAnalyzerSubmoduleKind.Empty, null, null, null));
    }

    public string GetContent() => _buffer.ToString().Trim();

    public IReadOnlyList<DetailedAnalyzerSubmodule> GetSubmodules() => _submodules;
}
