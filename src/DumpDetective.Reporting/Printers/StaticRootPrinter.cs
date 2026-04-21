using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class StaticRootPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Static Root Leak Detection";

        public bool CanHandle(AnalyzerDomainResult result) => result is StaticRootDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not StaticRootDomainResult domain)
                return;

            writer.WriteHeader("STATIC ROOT LEAK DETECTION:");
            writer.WriteSubHeading("STATIC FIELD REFERENCES:");
            writer.WriteSeparator();
            writer.WriteMetric("Concerning static roots", $"{domain.RootCount:N0}");
            writer.WriteMetric("Total retained bytes", FormatHelper.FormatBytes(domain.TotalRetainedBytes));

            writer.WriteDetailBlank();
            writer.WriteSubHeading("ROOTED OBJECTS ANALYSIS:");
            writer.WriteSeparator();
            var roots = domain.TopRootsByRetainedBytes ?? [];
            if (roots.Count == 0)
            {
                writer.WriteLine("No root-level retained-byte breakdown available.");
            }
            else
            {
                foreach (var root in roots.Take(8))
                    writer.WriteMetric(FormatHelper.TruncateString(root.Name, 90), $"{FormatHelper.FormatBytes(root.Bytes)} retained", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("RETENTION PRESSURE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.RootCount >= 10
                ? "⚠️  High static-root pressure detected; review long-lived static ownership."
                : "ℹ️  Static-root pressure appears moderate in this dump.");
            writer.WriteDetailDivider();
        }
    }
}



