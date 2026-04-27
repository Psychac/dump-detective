using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace BenchmarkSuite1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // Job iteration counts are enforced via [Config(AnalyzerBenchmarkIterationConfig)] on
            // AnalyzerBenchmarkBase (type-level config wins over the assembly-level mutator injected
            // by BenchmarkProfilerAgentConfig). No mutator override needed here.
            var config = DefaultConfig.Instance
                .AddExporter(JsonExporter.Full);

            _ = BenchmarkSwitcher
                .FromAssembly(typeof(Program).Assembly)
                .Run(args, config);
        }
    }
}
