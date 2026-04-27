using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class RuntimeAnalysisContext : DumpDetective.Core.Abstractions.AnalysisContext
{
    /// <summary>
    /// Convenience accessor that exposes the build-time interface for the heap cache.
    /// Returns null when <see cref="AnalysisContext.Cache"/> does not implement <see cref="IHeapIndexBuilder"/>
    /// (e.g., in unit tests that supply a lightweight <see cref="IHeapAnalysisCache"/> stub).
    /// </summary>
    public IHeapIndexBuilder? HeapIndexBuilder => Cache as IHeapIndexBuilder;
}
