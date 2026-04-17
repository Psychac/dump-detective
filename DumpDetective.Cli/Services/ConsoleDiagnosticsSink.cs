using DumpDetective.Core.Abstractions;

namespace DumpDetective.Cli.Services;

internal sealed class ConsoleDiagnosticsSink(bool enabled) : IAnalysisDiagnosticsSink
{
    private readonly bool _enabled = enabled;

    public void AnalyzerStarted(string analyzerName, string category)
    {
        if (_enabled)
        {
            System.Console.WriteLine($"[DIAG] Started: {analyzerName} ({category})");
        }
    }

    public void AnalyzerCompleted(string analyzerName, string category, TimeSpan duration, IReadOnlyDictionary<string, object?>? metrics = null)
    {
        if (_enabled)
        {
            System.Console.WriteLine($"[DIAG] Completed: {analyzerName} ({category}) in {duration.TotalMilliseconds:F0} ms");
        }
    }

    public void AnalyzerFailed(string analyzerName, string category, TimeSpan duration, string errorType, string errorMessage)
    {
        if (_enabled)
        {
            System.Console.WriteLine($"[DIAG] Failed: {analyzerName} ({category}) in {duration.TotalMilliseconds:F0} ms - {errorType}: {errorMessage}");
        }
    }

    public void AnalyzerCanceled(string analyzerName, string category, TimeSpan duration)
    {
        if (_enabled)
        {
            System.Console.WriteLine($"[DIAG] Canceled: {analyzerName} ({category}) after {duration.TotalMilliseconds:F0} ms");
        }
    }
}
