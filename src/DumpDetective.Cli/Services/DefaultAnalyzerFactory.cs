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
            new AllocationPatternAnalyzer(),
            new ObjectShapeAnalyzer(),
            new GCRootAnalyzer(),
            new SegmentAnalyzer(),
            new ModuleAnalyzer(),
            new CrashAnalyzer(),
            new HangAnalyzer(),
            new AsyncTaskAnalyzer(),
            new RetentionAnalyzer(),
            new LeakCandidateAnalyzer(),
            new DominatorAnalyzer(),
            new StringAnalyzer(),
            new CollectionAnalyzer(_loggerFactory.CreateLogger<CollectionAnalyzer>()),
            new StaticRootLeakDetector(),
            new ReferenceChainAnalyzer(),
            new GCHandleAnalyzer(),
            new DependentHandleAnalyzer(),
            new LohFragmentationAnalyzer(),
            new ThreadStackClusterAnalyzer(),
            new ThreadAnalyzer(),
            new LockGraphAnalyzer(),
            new EventLeakAnalyzer(),
            new FinalizableObjectAnalyzer(),
            new AsyncStateMachineAnalyzer(),
            new ArrayAnalyzer(),
            new AppDomainAnalyzer(),
            new SegmentReservationAnalyzer(),
            new WeakReferenceAnalyzer(),
            new BoxingAnalyzer(),
            new JitAnalyzer()
        ];
    }
}
