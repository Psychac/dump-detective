using DumpDetective.Core.Abstractions;
using DumpDetective.Reporting.Printers;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultAnalyzerReporterFactory : IAnalyzerReporterFactory
{
    public IReadOnlyList<IAnalyzerReporter> CreateReporters()
    {
        return
        [
            new MemoryPrinter(),
            new GCGenerationPrinter(),
            new ModulePrinter(),
            new CrashPrinter(),
            new HangPrinter(),
            new MemoryLeakPrinter(),
            new CollectionPrinter(),
            new StaticRootPrinter(),
            new ReferenceChainPrinter(),
            new GCHandlePrinter(),
            new DependentHandlePrinter(),
            new LohFragmentationPrinter(),
            new ThreadStackClusterPrinter(),
            new ThreadPrinter(),
            new LockGraphPrinter(),
            new EventLeakPrinter()
        ];
    }
}
