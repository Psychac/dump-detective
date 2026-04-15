using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class ReferenceChainPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Reference Chain Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is ReferenceChainDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not ReferenceChainDomainResult domain)
                return;

            writer.WriteHeader("REFERENCE CHAIN ANALYSIS:");
            writer.WriteLine("REFERENCE RETENTION SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Analyzed samples: {domain.AnalyzedSamples:N0}");
            writer.WriteLine($"Retained samples: {domain.RetainedSamples:N0}");
            writer.WriteLine($"Retained percentage: {domain.RetainedPercent:F1}%");

            writer.WriteLine("\nTOP TYPE SAMPLE TRACE RESULTS:");
            writer.WriteSeparator();
            writer.WriteLine(domain.AnalyzedSamples == 0
                ? "No valid sample instance found."
                : $"{domain.RetainedSamples:N0} sampled top-type instance(s) had at least one GC-root path.");
            var topRetainedTypes = domain.TopRetainedTypes ?? [];
            if (topRetainedTypes.Count > 0)
            {
                writer.WriteLine("Top retained sampled types:");
                foreach (var entry in topRetainedTypes.Take(8))
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 80)}: {entry.Count:N0} retained sample(s)");
            }

            writer.WriteLine("\nREFERENCE CHAINS (showing up to 5):");
            writer.WriteSeparator();
            writer.WriteLine("Path graph details are condensed in this mode; prioritize types with repeated retained samples first.");

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
