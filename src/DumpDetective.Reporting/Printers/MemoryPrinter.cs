using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class MemoryPrinter : IAnalyzerReporter
    {
        private const int TopItemsToShow = 20;

        public string AnalyzerName => "Memory Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is MemoryDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not MemoryDomainResult domain)
                return;

            writer.WriteHeader("MEMORY ANALYSIS:");
            writer.WriteSubHeading("OVERALL SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Memory", FormatHelper.FormatBytes(domain.TotalBytes));
            writer.WriteMetric("Total Objects", $"{domain.TotalObjects:N0}");
            writer.WriteMetric("LOH Memory", $"{FormatHelper.FormatBytes(domain.LohBytes)} ({domain.LohPercent:F1}%)");
            writer.WriteMetric("LOH Objects", $"{domain.LohObjects:N0} ({domain.LohPercent:F1}% of total memory)");
            writer.WriteMetric("LOH Threshold", $"{domain.LohThresholdBytes:N0} bytes");
            writer.WriteMetric("Unique Types", $"{domain.UniqueTypes:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("HEAP COMPOSITION SIGNALS:");
            writer.WriteSeparator();
            if (domain.LohPercent >= 40)
                writer.WriteDetailText("⚠️  LOH share is elevated; review large-object allocation and retention patterns.");
            else
                writer.WriteDetailText("✅ LOH share appears within expected range for this snapshot.");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP 20 OBJECT TYPES BY MEMORY SIZE:");
            writer.WriteSeparator();
            writer.WriteDetailText($"{"Type",-80} {"Count",12} {"Total Size",12}");
            foreach (var type in domain.TopTypesBySize.Take(TopItemsToShow))
            {
                var wrappedTypeLines = WrapText(type.TypeName, 80).ToList();
                if (wrappedTypeLines.Count == 0)
                    wrappedTypeLines.Add(string.Empty);

                writer.WriteDetailText($"{wrappedTypeLines[0],-80} {type.Count,12:N0} {FormatHelper.FormatBytes(type.TotalBytes),12}");
                for (int i = 1; i < wrappedTypeLines.Count; i++)
                    writer.WriteDetailText($"{wrappedTypeLines[i],-80} {string.Empty,12} {string.Empty,12}");
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP 20 OBJECT TYPES BY COUNT:");
            writer.WriteSeparator();
            writer.WriteDetailText($"{"Type",-80} {"Count",12} {"Total Size",12}");
            foreach (var type in domain.TopTypesByCount.Take(TopItemsToShow))
            {
                var wrappedTypeLines = WrapText(type.TypeName, 80).ToList();
                if (wrappedTypeLines.Count == 0)
                    wrappedTypeLines.Add(string.Empty);

                writer.WriteDetailText($"{wrappedTypeLines[0],-80} {type.Count,12:N0} {FormatHelper.FormatBytes(type.TotalBytes),12}");
                for (int i = 1; i < wrappedTypeLines.Count; i++)
                    writer.WriteDetailText($"{wrappedTypeLines[i],-80} {string.Empty,12} {string.Empty,12}");
            }

            writer.WriteDetailDivider();
        }

        private static IEnumerable<string> WrapText(string? value, int width)
        {
            if (string.IsNullOrWhiteSpace(value) || width <= 0)
            {
                yield return string.Empty;
                yield break;
            }

            var text = value.Trim();
            int index = 0;

            while (index < text.Length)
            {
                int remaining = text.Length - index;
                if (remaining <= width)
                {
                    yield return text[index..];
                    yield break;
                }

                int lastSpace = text.LastIndexOf(' ', index + width, width);
                if (lastSpace <= index)
                {
                    yield return text.Substring(index, width);
                    index += width;
                }
                else
                {
                    yield return text.Substring(index, lastSpace - index).TrimEnd();
                    index = lastSpace + 1;
                }

                while (index < text.Length && text[index] == ' ')
                    index++;
            }
        }
    }
}



