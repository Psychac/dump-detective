using System.IO;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class GCGenerationPrinter : IAnalyzerReporter
    {
        private const int TopLohTypesToShow = 15;

        public string AnalyzerName => "GC Generation Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is GCGenerationDomainResult;

        public void Render(AnalyzerDomainResult result, TextWriter writer)
        {
            if (result is not GCGenerationDomainResult domain)
                return;

            writer.WriteHeader("GC GENERATIONS BREAKDOWN:");
            writer.WriteLine("HEAP SUMMARY:");
            writer.WriteSeparator();

            writer.WriteLine($"Gen0 objects: {domain.Gen0Objects:N0}, {FormatHelper.FormatBytes(domain.Gen0Bytes)}");
            writer.WriteLine($"Gen1 objects: {domain.Gen1Objects:N0}, {FormatHelper.FormatBytes(domain.Gen1Bytes)}");
            writer.WriteLine($"Gen2 objects: {domain.Gen2Objects:N0}, {FormatHelper.FormatBytes(domain.Gen2Bytes)}");
            writer.WriteLine($"LOH objects: {domain.LohObjects:N0}, {FormatHelper.FormatBytes(domain.LohBytes)}");
            writer.WriteLine($"Total objects: {domain.TotalObjects:N0}");
            writer.WriteLine($"LOH percentage: {domain.LohPercent:F1}%");

            writer.WriteLine("\nGENERATION SPLIT:");
            writer.WriteSeparator();
            writer.WriteLine($"Gen0 bytes: {FormatHelper.FormatBytes(domain.Gen0Bytes)}");
            writer.WriteLine($"Gen1 bytes: {FormatHelper.FormatBytes(domain.Gen1Bytes)}");
            writer.WriteLine($"Gen2 bytes: {FormatHelper.FormatBytes(domain.Gen2Bytes)}");
            writer.WriteLine($"Large object heap bytes: {FormatHelper.FormatBytes(domain.LohBytes)}");

            writer.WriteLine("\nLARGE OBJECT HEAP (LOH) USAGE:");
            writer.WriteSeparator();
            writer.WriteLine($"Total LOH Objects: {domain.LohObjects:N0}");
            writer.WriteLine($"Total LOH Size: {FormatHelper.FormatBytes(domain.LohBytes)}");

            var topLohTypes = domain.TopLohTypes ?? [];
            if (topLohTypes.Count > 0)
            {
                writer.WriteLine("\nTop LOH Object Types:");
                writer.WriteLine($"{"Type",-68} {"Count",10} {"Total Size",14}");
                foreach (var type in topLohTypes.Take(TopLohTypesToShow))
                {
                    var wrappedTypeLines = WrapText(type.TypeName, 68).ToList();
                    if (wrappedTypeLines.Count == 0)
                        wrappedTypeLines.Add(string.Empty);

                    writer.WriteLine($"{wrappedTypeLines[0],-68} {type.Count,10:N0} {FormatHelper.FormatBytes(type.TotalBytes),14}");
                    for (int i = 1; i < wrappedTypeLines.Count; i++)
                        writer.WriteLine($"{wrappedTypeLines[i],-68} {string.Empty,10} {string.Empty,14}");
                }
            }

            writer.WriteLine("\nLOH RISK SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.LohPercent >= 35
                ? "âš ï¸  LOH footprint is elevated for this dump."
                : "âœ… LOH footprint is not elevated.");

            writer.WriteLine(StringConstants.Equals80);
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



