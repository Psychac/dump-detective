using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class ReferenceChainPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Reference Chain Analysis";
        public string DisplayTitle => "Reference Chain Analysis";
        public int SortOrder => 60;

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
                        writer.WriteMetric("Sample Instance", $"0x{trace.SampleAddress.Value:X}", indentLevel: 2);
                        writer.WriteMetric("Type", trace.SampleObjectType ?? StringConstants.UnknownType, indentLevel: 2);
                        writer.WriteMetric("Size", FormatHelper.FormatBytes(trace.SampleObjectSize), indentLevel: 2);
                        writer.WriteMetric("Status", trace.HasGcRoot
                            ? "GC root path found"
                            : trace.TraversalLimited
                                ? "No GC root found (search limit reached; result inconclusive)"
                                : "No GC root found (may be eligible for collection)", indentLevel: 2);

                        if (trace.HasGcRoot && !string.IsNullOrWhiteSpace(trace.RootPath))
                            writer.WritePathMetric("Root Path", trace.RootPath, indentLevel: 2);
                    }
                    else
                    {
                        writer.WriteMetric("Sample Instance", "not available", indentLevel: 2);
                        writer.WriteMetric("Status", "Sample instance unavailable for tracing", indentLevel: 2);
                    }

                    writer.WriteDetailBlank();
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
                writer.WriteDetailText("No sampled GC-root chain paths were captured.");
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



