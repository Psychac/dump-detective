using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class GCHandlePrinter : IAnalyzerReporter
    {
        private const int KindColumnWidth = 50;
        private const int TypeColumnWidth = 70;

        public string AnalyzerName => "GC Handle Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is GCHandleDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not GCHandleDomainResult domain)
                return;

            writer.WriteHeader("GC HANDLE ANALYSIS:");
            writer.WriteLine("HANDLE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Total handles: {domain.TotalHandles:N0}");
            double strongPct = domain.TotalHandles == 0 ? 0 : domain.StrongLikeHandles * 100.0 / domain.TotalHandles;
            double weakPct = domain.TotalHandles == 0 ? 0 : domain.WeakLikeHandles * 100.0 / domain.TotalHandles;
            double pinnedPct = domain.TotalHandles == 0 ? 0 : domain.PinnedHandleTargets * 100.0 / domain.TotalHandles;
            writer.WriteLine($"Strong-like handles: {domain.StrongLikeHandles:N0} ({strongPct:F1}%)");
            writer.WriteLine($"Weak-like handles: {domain.WeakLikeHandles:N0} ({weakPct:F1}%)");
            writer.WriteLine($"Pinned-handle targets: {domain.PinnedHandleTargets:N0} ({pinnedPct:F1}%)");

            writer.WriteLine("\nHANDLES BY KIND:");
            writer.WriteSeparator();
            var byKind = domain.HandlesByKind ?? [];
            if (byKind.Count == 0)
            {
                writer.WriteLine("No handle-kind distribution available.");
            }
            else
            {
                writer.WriteLine($"{"Kind",-50} {"Count",12} {"% Total",8}");
                foreach (var entry in byKind)
                {
                    double pct = domain.TotalHandles == 0 ? 0 : entry.Count * 100.0 / domain.TotalHandles;
                    var wrappedLines = WrapText(entry.Name, KindColumnWidth).ToList();
                    if (wrappedLines.Count == 0)
                        wrappedLines.Add(string.Empty);

                    writer.WriteLine($"{wrappedLines[0],-50} {entry.Count,12:N0} {pct,7:F1}%");
                    for (int i = 1; i < wrappedLines.Count; i++)
                        writer.WriteLine($"{wrappedLines[i],-50} {string.Empty,12} {string.Empty,8}");
                }
            }

            writer.WriteLine("\nTOP TYPES REFERENCED BY HANDLES:");
            writer.WriteSeparator();
            var topTargets = domain.TopTargetTypes ?? [];
            if (topTargets.Count == 0)
            {
                writer.WriteLine("No resolved handle target types available.");
            }
            else
            {
                writer.WriteLine($"{"Type",-70} {"Count",12}");
                foreach (var entry in topTargets)
                {
                    var wrappedLines = WrapText(entry.Name, TypeColumnWidth).ToList();
                    if (wrappedLines.Count == 0)
                        wrappedLines.Add(string.Empty);

                    writer.WriteLine($"{wrappedLines[0],-70} {entry.Count,12:N0}");
                    for (int i = 1; i < wrappedLines.Count; i++)
                        writer.WriteLine($"{wrappedLines[i],-70} {string.Empty,12}");
                }
            }

            writer.WriteLine("\nTOP TYPES REFERENCED BY PINNED HANDLES:");
            writer.WriteSeparator();
            var topPinned = domain.TopPinnedTargetTypes ?? [];
            if (topPinned.Count == 0)
            {
                writer.WriteLine("No pinned-handle target type details available.");
            }
            else
            {
                writer.WriteLine($"{"Type",-70} {"Count",12}");
                foreach (var entry in topPinned)
                {
                    var wrappedLines = WrapText(entry.Name, TypeColumnWidth).ToList();
                    if (wrappedLines.Count == 0)
                        wrappedLines.Add(string.Empty);

                    writer.WriteLine($"{wrappedLines[0],-70} {entry.Count,12:N0}");
                    for (int i = 1; i < wrappedLines.Count; i++)
                        writer.WriteLine($"{wrappedLines[i],-70} {string.Empty,12}");
                }
            }

            writer.WriteLine("\nHANDLE PRESSURE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Pinned handle targets: {domain.PinnedHandleTargets:N0}");

            writer.WriteLine(domain.TotalHandles >= 10_000 || domain.PinnedHandleTargets >= 1_000
                ? "⚠️  Elevated handle pressure detected."
                : "✅ Handle pressure appears within expected range.");
            writer.WriteLine(StringConstants.Equals80);
        }

        private static IEnumerable<string> WrapText(string? value, int width)
        {
            if (string.IsNullOrWhiteSpace(value) || width <= 0)
            {
                yield return string.Empty;
                yield break;
            }

            string text = value.Trim();
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



