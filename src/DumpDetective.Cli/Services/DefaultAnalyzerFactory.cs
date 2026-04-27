using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultAnalyzerFactory : IAnalyzerFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DefaultAnalyzerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

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
            new CollectionAnalyzer(_loggerFactory.CreateLogger<CollectionAnalyzer>()),
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
