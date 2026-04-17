using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalysisContext : DumpDetective.Core.Abstractions.AnalysisContext
{
    public HeapAnalysisCache HeapCache => (HeapAnalysisCache)Cache;
    public MemoryLeakOptions MemoryLeakOptions { get; init; } = new();
    public ReferenceChainOptions ReferenceChainOptions { get; init; } = new();
    public EventLeakOptions EventLeakOptions { get; init; } = new();
    public DiagnosticsOptions DiagnosticsOptions { get; init; } = new();
}


