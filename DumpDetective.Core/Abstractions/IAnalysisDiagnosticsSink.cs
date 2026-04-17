namespace DumpDetective.Core.Abstractions;

internal interface IAnalysisDiagnosticsSink
{
    void AnalyzerStarted(string analyzerName, string category);
    void AnalyzerCompleted(string analyzerName, string category, TimeSpan duration, IReadOnlyDictionary<string, object?>? metrics = null);
    void AnalyzerFailed(string analyzerName, string category, TimeSpan duration, string errorType, string errorMessage);
    void AnalyzerCanceled(string analyzerName, string category, TimeSpan duration);
}

internal sealed class NullAnalysisDiagnosticsSink : IAnalysisDiagnosticsSink
{
    public static NullAnalysisDiagnosticsSink Instance { get; } = new();

    public void AnalyzerStarted(string analyzerName, string category) { }

    public void AnalyzerCompleted(string analyzerName, string category, TimeSpan duration, IReadOnlyDictionary<string, object?>? metrics = null) { }

    public void AnalyzerFailed(string analyzerName, string category, TimeSpan duration, string errorType, string errorMessage) { }

    public void AnalyzerCanceled(string analyzerName, string category, TimeSpan duration) { }
}
