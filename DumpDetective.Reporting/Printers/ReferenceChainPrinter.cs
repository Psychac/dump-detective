using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class ReferenceChainPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Reference Chain Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is ReferenceChainDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not ReferenceChainDomainResult domain)
                return;

            writer.WriteHeader("REFERENCE CHAIN ANALYSIS:");
            writer.WriteLine("REFERENCE CHAIN ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteLine($"Analyzed samples: {domain.AnalyzedSamples:N0}");
            writer.WriteLine($"Retained samples: {domain.RetainedSamples:N0}");
            writer.WriteLine($"Retained percentage: {domain.RetainedPercent:F1}%");

            writer.WriteLine("\nTOP TYPE SAMPLE TRACE RESULTS:");
            writer.WriteSeparator();
            writer.WriteLine(domain.AnalyzedSamples == 0
                ? "No valid sample instance found."
                : $"{domain.RetainedSamples:N0} sampled top-type instance(s) had at least one GC-root path.");

            var traces = domain.TopTypeSampleTraces ?? [];
            if (traces.Count > 0)
            {
                writer.WriteLine(string.Empty);
                int index = 1;
                foreach (var trace in traces.Take(10))
                {
                    writer.WriteLine($"[{index++}] Type: {trace.TypeName}");
                    writer.WriteLine($"    Count: {trace.Count:N0}");
                    writer.WriteLine($"    Total Size: {FormatHelper.FormatBytes(trace.TotalSizeBytes)}");

                    if (trace.SampleAddress.HasValue)
                    {
                        writer.WriteLine($"    Sample Instance: 0x{trace.SampleAddress.Value:X}");
                        writer.WriteLine($"    Type: {trace.SampleObjectType ?? StringConstants.UnknownType}");
                        writer.WriteLine($"    Size: {FormatHelper.FormatBytes(trace.SampleObjectSize)}");
                        writer.WriteLine(trace.HasGcRoot
                            ? "    Status: GC root path found"
                            : trace.TraversalLimited
                                ? "    Status: No GC root found (search limit reached; result inconclusive)"
                                : "    Status: No GC root found (may be eligible for collection)");

                        if (trace.HasGcRoot && !string.IsNullOrWhiteSpace(trace.RootPath))
                            writer.WriteLine($"    Root Path: {trace.RootPath}");
                    }
                    else
                    {
                        writer.WriteLine("    Sample Instance: not available");
                        writer.WriteLine("    Status: Sample instance unavailable for tracing");
                    }

                    writer.WriteLine(string.Empty);
                }
            }

            var topRetainedTypes = domain.TopRetainedTypes ?? [];
            if (topRetainedTypes.Count > 0)
            {
                writer.WriteLine("Top retained sampled types:");
                foreach (var entry in topRetainedTypes.Take(8))
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 80)}: {entry.Count:N0} retained sample(s)");
            }

            writer.WriteLine("\nREFERENCE CHAINS (showing up to 5):");
            writer.WriteSeparator();
            var chains = domain.SampleReferenceChains ?? [];
            if (chains.Count == 0)
            {
                writer.WriteLine("No sampled GC-root chain paths were captured.");
            }
            else
            {
                foreach (string chain in chains.Take(5))
                    writer.WriteLine($"  • {chain}");
            }

            writer.WriteLine("\nGC-ROOT COVERAGE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.AnalyzedSamples == 0
                ? "ℹ️  No sample instances were available for root-path tracing."
                : domain.RetainedPercent >= 70
                    ? "⚠️  High retention coverage across sampled top types."
                    : "✅ Retention coverage is not elevated across sampled top types.");
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}



