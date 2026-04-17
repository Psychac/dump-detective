using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

internal interface IAnalyzer
{
string Name { get; }
AnalyzerExecutionResult Execute(AnalysisContext context);
}

internal sealed record AnalyzerExecutionResult(
IReadOnlyList<InsightFinding> Findings,
AnalyzerDomainResult? DomainResult = null)
{
public static AnalyzerExecutionResult Empty { get; } = new([]);
}

internal class AnalysisContext
{
public required ClrRuntime Runtime { get; init; }
public required ClrHeap Heap { get; init; }
// TEMP-REFRACTOR-BRIDGE: Remove after Spec 03 when AnalysisContext is owned/enriched by final async contracts.
public dynamic? Cache { get; init; }
}
