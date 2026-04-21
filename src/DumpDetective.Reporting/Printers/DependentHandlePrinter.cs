using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class DependentHandlePrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Dependent Handle Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is DependentHandleDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not DependentHandleDomainResult domain)
                return;

            writer.WriteHeader("DEPENDENT HANDLE ANALYSIS:");
            writer.WriteSubHeading("DEPENDENT HANDLE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Dependent handles", $"{domain.DependentHandleCount:N0}");
            writer.WriteMetric("Resolved edges", $"{domain.ResolvedEdgeCount:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP SOURCE TYPES:");
            writer.WriteSeparator();
            var sources = domain.TopSourceTypes ?? [];
            if (sources.Count == 0)
            {
                writer.WriteDetailText("No source-type distribution available.");
            }
            else
            {
                foreach (var entry in sources.Take(8))
                    writer.WriteMetric(FormatHelper.TruncateString(entry.Name, 70), $"{entry.Count:N0}", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP TARGET TYPES:");
            writer.WriteSeparator();
            var targets = domain.TopTargetTypes ?? [];
            if (targets.Count == 0)
            {
                writer.WriteDetailText("No target-type distribution available.");
            }
            else
            {
                foreach (var entry in targets.Take(8))
                    writer.WriteMetric(FormatHelper.TruncateString(entry.Name, 70), $"{entry.Count:N0}", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP SOURCE -> TARGET EDGES:");
            writer.WriteSeparator();
            var edges = domain.TopSourceTargetEdges ?? [];
            if (edges.Count == 0)
            {
                writer.WriteDetailText("No source-target edge breakdown available.");
            }
            else
            {
                foreach (var entry in edges.Take(8))
                    writer.WriteMetric(FormatHelper.TruncateString(entry.Name, 90), $"{entry.Count:N0}", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("RESOLUTION QUALITY SIGNAL:");
            writer.WriteSeparator();
            writer.WriteMetric("Unresolved targets", $"{domain.UnresolvedTargetCount:N0} ({domain.UnresolvedPercent:F1}%)");
            writer.WriteDetailText(domain.UnresolvedPercent >= 50
                ? "⚠️  High unresolved-target ratio; DAC/runtime visibility may be limited."
                : "✅ Unresolved-target ratio is within expected bounds.");
            writer.WriteDetailDivider();
        }
    }
}



