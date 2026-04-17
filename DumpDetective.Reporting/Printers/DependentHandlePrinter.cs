using System.IO;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class DependentHandlePrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Dependent Handle Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is DependentHandleDomainResult;

        public void Render(AnalyzerDomainResult result, TextWriter writer)
        {
            if (result is not DependentHandleDomainResult domain)
                return;

            writer.WriteHeader("DEPENDENT HANDLE ANALYSIS:");
            writer.WriteLine("DEPENDENT HANDLE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Dependent handles: {domain.DependentHandleCount:N0}");
            writer.WriteLine($"Resolved edges: {domain.ResolvedEdgeCount:N0}");

            writer.WriteLine("\nTOP SOURCE TYPES:");
            writer.WriteSeparator();
            var sources = domain.TopSourceTypes ?? [];
            if (sources.Count == 0)
            {
                writer.WriteLine("No source-type distribution available.");
            }
            else
            {
                foreach (var entry in sources.Take(8))
                    writer.WriteLine($"  â€¢ {FormatHelper.TruncateString(entry.Name, 70)}: {entry.Count:N0}");
            }

            writer.WriteLine("\nTOP TARGET TYPES:");
            writer.WriteSeparator();
            var targets = domain.TopTargetTypes ?? [];
            if (targets.Count == 0)
            {
                writer.WriteLine("No target-type distribution available.");
            }
            else
            {
                foreach (var entry in targets.Take(8))
                    writer.WriteLine($"  â€¢ {FormatHelper.TruncateString(entry.Name, 70)}: {entry.Count:N0}");
            }

            writer.WriteLine("\nTOP SOURCE -> TARGET EDGES:");
            writer.WriteSeparator();
            var edges = domain.TopSourceTargetEdges ?? [];
            if (edges.Count == 0)
            {
                writer.WriteLine("No source-target edge breakdown available.");
            }
            else
            {
                foreach (var entry in edges.Take(8))
                    writer.WriteLine($"  â€¢ {FormatHelper.TruncateString(entry.Name, 90)}: {entry.Count:N0}");
            }

            writer.WriteLine("\nRESOLUTION QUALITY SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Unresolved targets: {domain.UnresolvedTargetCount:N0} ({domain.UnresolvedPercent:F1}%)");
            writer.WriteLine(domain.UnresolvedPercent >= 50
                ? "âš ï¸  High unresolved-target ratio; DAC/runtime visibility may be limited."
                : "âœ… Unresolved-target ratio is within expected bounds.");
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}



