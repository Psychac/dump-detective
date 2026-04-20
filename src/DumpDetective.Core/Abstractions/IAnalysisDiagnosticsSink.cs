using DumpDetective.Core.Models;

namespace DumpDetective.Core.Abstractions;

internal interface IAnalysisDiagnosticsSink
{
    void Publish(AnalysisDiagnosticsEvent diagnosticsEvent);
}

internal sealed class NullAnalysisDiagnosticsSink : IAnalysisDiagnosticsSink
{
    public static NullAnalysisDiagnosticsSink Instance { get; } = new();

    public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent) { }
}

internal sealed class InMemoryAnalysisDiagnosticsSink : IAnalysisDiagnosticsSink
{
    private readonly List<AnalysisDiagnosticsEvent> _events = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<AnalysisDiagnosticsEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        lock (_gate)
        {
            _events.Add(diagnosticsEvent);
        }
    }
}
