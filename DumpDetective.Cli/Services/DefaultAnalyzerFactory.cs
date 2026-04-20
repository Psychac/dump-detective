using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultAnalyzerFactory : IAnalyzerFactory
{
    public IReadOnlyList<IAnalyzer> CreateAnalyzers()
    {
        return
        [
            new MemoryAnalyzer(),
            new GCGenerationAnalyzer(),
            new ModuleAnalyzer(),
            new CrashAnalyzer(),
            new HangAnalyzer(),
            new MemoryLeakAnalyzer(),
            new CollectionAnalyzer(),
            new StaticRootLeakDetector(),
            new ReferenceChainAnalyzer(),
            new GCHandleAnalyzer(),
            new DependentHandleAnalyzer(),
            new LohFragmentationAnalyzer(),
            new ThreadStackClusterAnalyzer(),
            new ThreadAnalyzer(),
            new LockGraphAnalyzer(),
            new EventLeakAnalyzer()
        ];
    }
}
