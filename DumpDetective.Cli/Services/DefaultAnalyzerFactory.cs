using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Configuration;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultAnalyzerFactory : IAnalyzerFactory
{
    public IReadOnlyList<IAnalyzer> CreateAnalyzers(ResolvedExecutionOptions resolved)
    {
        AnalysisConfiguration analysisConfiguration = new()
        {
            HighReferenceThreshold = resolved.MemoryLeak.HighReferenceThreshold,
            MaxDuplicateStringLength = resolved.MemoryLeak.MaxDuplicateStringLength,
            MinDuplicateStringCount = resolved.MemoryLeak.MinDuplicateStringCount,
            MaxReferenceAddressesToTrack = resolved.MemoryLeak.MaxReferenceAddresses,
            ReferenceChainTopCount = resolved.ReferenceChain.TopCount,
            ReferenceChainMaxPathSearchObjects = resolved.ReferenceChain.MaxPathSearchObjects,
            EventLeakMinSubscribers = resolved.EventLeak.MinSubscribers
        };

        return
        [
            new MemoryAnalyzer(),
            new GCGenerationAnalyzer(),
            new ModuleAnalyzer(),
            new CrashAnalyzer(),
            new HangAnalyzer(),
            new MemoryLeakAnalyzer(analysisConfiguration),
            new CollectionAnalyzer(),
            new StaticRootLeakDetector(),
            new ReferenceChainAnalyzer(analysisConfiguration),
            new GCHandleAnalyzer(),
            new DependentHandleAnalyzer(),
            new LohFragmentationAnalyzer(),
            new ThreadStackClusterAnalyzer(),
            new ThreadAnalyzer(),
            new LockGraphAnalyzer(),
            new EventLeakAnalyzer(analysisConfiguration)
        ];
    }
}
