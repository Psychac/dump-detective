using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Pipeline;

internal class AnalysisContext
{
    public required ClrRuntime Runtime { get; init; }
    public required ClrHeap Heap { get; init; }
    public required HeapAnalysisCache Cache { get; init; }
}


