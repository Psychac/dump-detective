using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace BenchmarkSuite1
{
    /// <summary>
    /// Type-level config applied to AnalyzerBenchmarkBase via [Config].
    /// BenchmarkDotNet merges configs in global → assembly → type order, so a type-level
    /// mutator is applied last and reliably wins over the assembly-level mutator injected by
    /// BenchmarkProfilerAgentConfig (Microsoft.VisualStudio.DiagnosticsHub.BenchmarkDotNetDiagnosers),
    /// which would otherwise force WarmupCount=1 and IterationCount=5 for every profiler session.
    /// </summary>
    internal sealed class AnalyzerBenchmarkIterationConfig : ManualConfig
    {
        internal const int WarmupCount = 0;
        internal const int IterationCount = 3;

        public AnalyzerBenchmarkIterationConfig()
        {
            AddJob(Job.Default
                .WithWarmupCount(WarmupCount)
                .WithIterationCount(IterationCount)
                .AsMutator());
        }
    }
}
