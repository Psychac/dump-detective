using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Services;

internal sealed class ConsoleDiagnosticsSink(bool enabled) : IAnalysisDiagnosticsSink
{
    private readonly bool _enabled = enabled;

    public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        if (!_enabled)
        {
            return;
        }

        string analyzerSegment = string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName)
            ? string.Empty
            : $" {diagnosticsEvent.AnalyzerName}";

        string durationSegment = diagnosticsEvent.DurationMs.HasValue
            ? $", duration={diagnosticsEvent.DurationMs.Value:F0}ms"
            : string.Empty;

        string exceptionSegment = !string.IsNullOrWhiteSpace(diagnosticsEvent.ExceptionType)
            ? $", ex={diagnosticsEvent.ExceptionType}: {diagnosticsEvent.ExceptionMessage}"
            : string.Empty;

        System.Console.WriteLine(
            $"[DIAG] {diagnosticsEvent.EventType}{analyzerSegment} | category={diagnosticsEvent.Category}{durationSegment}, scans={diagnosticsEvent.ObjectScanCount}, cacheHits={diagnosticsEvent.CacheHits}, cacheMisses={diagnosticsEvent.CacheMisses}{exceptionSegment} | {diagnosticsEvent.Message}");
    }
}
