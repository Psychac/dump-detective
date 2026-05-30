using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Query;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class RuntimeAnalysisContext : DumpDetective.Core.Abstractions.AnalysisContext
{
    /// <summary>
    /// Facade wrapping <see cref="AnalysisContext.Runtime"/> and <see cref="AnalysisContext.Heap"/>
    /// with a shared per-session <c>MethodTable → ClrType</c> cache.
    /// Populated by the pipeline before any analyzer runs.
    /// May be <c>null</c> in unit tests that supply a lightweight <see cref="IHeapAnalysisCache"/> stub
    /// without a real <see cref="Microsoft.Diagnostics.Runtime.ClrRuntime"/>.
    /// </summary>
    public RuntimeFacade? RuntimeFacade { get; init; }

    /// <summary>
    /// Convenience accessor that exposes the build-time interface for the heap cache.
    /// Returns null when <see cref="AnalysisContext.Cache"/> does not implement <see cref="IHeapIndexBuilder"/>
    /// (e.g., in unit tests that supply a lightweight <see cref="IHeapAnalysisCache"/> stub).
    /// </summary>
    public IHeapIndexBuilder? HeapIndexBuilder => Cache as IHeapIndexBuilder;

    /// <summary>
    /// Query engine that operates on the pre-built index — never on the raw heap.
    /// Lazily instantiated on first access; <c>null</c> in unit tests that do not supply a real runtime.
    /// </summary>
    public IQueryEngine? Query => _query ??= new QueryEngine(Cache, Heap);
    private IQueryEngine? _query;
}
