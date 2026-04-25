using DumpDetective.Core.Models;

namespace DumpDetective.Core.Abstractions;

/// <summary>
/// Generates <see cref="InsightFinding"/> objects from a pure-data <see cref="AnalyzerDomainResult"/>.
/// Separates threshold-driven interpretation logic from the data-extraction concerns of analyzers.
/// </summary>
internal interface IFindingGenerator
{
    /// <summary>Matches the <see cref="IAnalyzer.Name"/> of the analyzer whose result this generator interprets.</summary>
    string AnalyzerName { get; }

    bool CanGenerate(AnalyzerDomainResult result);

    IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result);
}
