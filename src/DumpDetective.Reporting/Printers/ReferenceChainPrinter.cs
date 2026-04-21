using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

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
            writer.WriteSubHeading("REFERENCE CHAIN ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteMetric("Analyzed samples", $"{domain.AnalyzedSamples:N0}");
            writer.WriteMetric("Retained samples", $"{domain.RetainedSamples:N0}");
            writer.WriteMetric("Retained percentage", $"{domain.RetainedPercent:F1}%");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP TYPE SAMPLE TRACE RESULTS:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.AnalyzedSamples == 0
                ? "No valid sample instance found."
                : $"{domain.RetainedSamples:N0} sampled top-type instance(s) had at least one GC-root path.");

            var traces = domain.TopTypeSampleTraces ?? [];
            if (traces.Count > 0)
            {
                writer.WriteDetailBlank();
                int index = 1;
                foreach (var trace in traces.Take(10))
                {
                    writer.WriteDetailText($"[{index++}] Type: {trace.TypeName}");
                    writer.WriteMetric("Count", $"{trace.Count:N0}", indentLevel: 2);
                    writer.WriteMetric("Total Size", FormatHelper.FormatBytes(trace.TotalSizeBytes), indentLevel: 2);

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
                writer.WriteSubHeading("Top retained sampled types:");
                foreach (var entry in topRetainedTypes.Take(8))
                    writer.WriteMetric(FormatHelper.TruncateString(entry.Name, 80), $"{entry.Count:N0} retained sample(s)", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("REFERENCE CHAINS (showing up to 5):");
            writer.WriteSeparator();
            var chains = domain.SampleReferenceChains ?? [];
            if (chains.Count == 0)
            {
                writer.WriteLine("No sampled GC-root chain paths were captured.");
            }
            else
            {
                foreach (string chain in chains.Take(5))
                    writer.WriteDetailBullet(chain, indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("GC-ROOT COVERAGE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.AnalyzedSamples == 0
                ? "ℹ️  No sample instances were available for root-path tracing."
                : domain.RetainedPercent >= 70
                    ? "⚠️  High retention coverage across sampled top types."
                    : "✅ Retention coverage is not elevated across sampled top types.");
            writer.WriteDetailDivider();
        }
    }
}



