using DumpDetective.Core.Models;

namespace DumpDetective.Core.Abstractions;

public interface IAnalysisDiagnosticsSink
{
    void Publish(AnalysisDiagnosticsEvent diagnosticsEvent);
}

public sealed class NullAnalysisDiagnosticsSink : IAnalysisDiagnosticsSink
{
    public static NullAnalysisDiagnosticsSink Instance { get; } = new();

    public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent) { }
}

public sealed class InMemoryAnalysisDiagnosticsSink : IAnalysisDiagnosticsSink
{
    private readonly List<AnalysisDiagnosticsEvent> _events = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<AnalysisDiagnosticsEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
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
