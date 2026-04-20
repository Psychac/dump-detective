using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Services;

internal sealed class FileDiagnosticsSink(string filePath) : IAnalysisDiagnosticsSink
{
    private readonly string _filePath = filePath;
    private readonly Lock _gate = new();

    public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        string line = System.Text.Json.JsonSerializer.Serialize(diagnosticsEvent);

        try
        {
            lock (_gate)
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
