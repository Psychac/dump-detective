namespace DumpDetective.Reporting.Services;

internal interface IAnalyzerFactory
{
    IReadOnlyList<Sdk.Analysis.IAnalyzer> CreateAnalyzers();
}
