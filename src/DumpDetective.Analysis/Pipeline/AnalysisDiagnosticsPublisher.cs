using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalysisDiagnosticsPublisher
{
    public void Publish(IAnalysisDiagnosticsSink diagnosticsSink, AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        try
        {
            diagnosticsSink.Publish(diagnosticsEvent);
        }
        catch
        {
            // Diagnostics are best-effort and should never stop analysis execution.
        }
    }
}