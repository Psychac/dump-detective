using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class RuntimeAnalysisContext : DumpDetective.Core.Abstractions.AnalysisContext
{
    /// <summary>Convenience accessor that down-casts <see cref="AnalysisContext.Cache"/> to the concrete type.</summary>
    public HeapAnalysisCache HeapCache => (HeapAnalysisCache)Cache;
}
