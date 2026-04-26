using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class GCHandlePrinter : IAnalyzerReporter
    {
        private const int KindColumnWidth = 50;
        private const int TypeColumnWidth = 70;

        public string AnalyzerName => "GC Handle Analysis";
        public string DisplayTitle => "GC Handle Analysis";
        public int SortOrder => 90;

        public bool CanHandle(AnalyzerDomainResult result) => result is GCHandleDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not GCHandleDomainResult domain)
                return;

            writer.WriteHeader("GC HANDLE ANALYSIS:");
            writer.WriteSubHeading("HANDLE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Total handles", $"{domain.TotalHandles:N0}");
            double strongPct = domain.TotalHandles == 0 ? 0 : domain.StrongLikeHandles * 100.0 / domain.TotalHandles;
            double weakPct = domain.TotalHandles == 0 ? 0 : domain.WeakLikeHandles * 100.0 / domain.TotalHandles;
            double pinnedPct = domain.TotalHandles == 0 ? 0 : domain.PinnedHandleTargets * 100.0 / domain.TotalHandles;
            writer.WriteMetric("Strong-like handles", $"{domain.StrongLikeHandles:N0} ({strongPct:F1}%)");
            writer.WriteMetric("Weak-like handles", $"{domain.WeakLikeHandles:N0} ({weakPct:F1}%)");
            writer.WriteMetric("Pinned-handle targets", $"{domain.PinnedHandleTargets:N0} ({pinnedPct:F1}%)");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("HANDLES BY KIND:");
            writer.WriteSeparator();
            var byKind = domain.HandlesByKind ?? [];
            if (byKind.Count == 0)
            {
                writer.WriteDetailText("No handle-kind distribution available.");
            }
            else
            {
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "Handles by kind",
                    Headers: ["Kind", "Count", "% Total"],
                    Rows: byKind.Select(entry =>
                    {
                        double pct = domain.TotalHandles == 0 ? 0 : entry.Count * 100.0 / domain.TotalHandles;
                        return new DetailedAnalyzerTableRow([
                            new DetailedAnalyzerTableCell(entry.Name),
                            new DetailedAnalyzerTableCell($"{entry.Count:N0}", entry.Count),
                            new DetailedAnalyzerTableCell($"{pct:F1}%")]);
                    }).ToList()));
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP TYPES REFERENCED BY HANDLES:");
            writer.WriteSeparator();
            var topTargets = domain.TopTargetTypes ?? [];
            if (topTargets.Count == 0)
            {
                writer.WriteDetailText("No resolved handle target types available.");
            }
            else
            {
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "Top types referenced by handles",
                    Headers: ["Type", "Count"],
                    Rows: topTargets.Select(entry => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(entry.Name),
                        new DetailedAnalyzerTableCell($"{entry.Count:N0}", entry.Count)]))
                    .ToList()));
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP TYPES REFERENCED BY PINNED HANDLES:");
            writer.WriteSeparator();
            var topPinned = domain.TopPinnedTargetTypes ?? [];
            if (topPinned.Count == 0)
            {
                writer.WriteDetailText("No pinned-handle target type details available.");
            }
            else
            {
                writer.WriteDetailTable(new DetailedAnalyzerTableData(
                    Caption: "Top types referenced by pinned handles",
                    Headers: ["Type", "Count"],
                    Rows: topPinned.Select(entry => new DetailedAnalyzerTableRow([
                        new DetailedAnalyzerTableCell(entry.Name),
                        new DetailedAnalyzerTableCell($"{entry.Count:N0}", entry.Count)]))
                    .ToList()));
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("HANDLE PRESSURE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteMetric("Pinned handle targets", $"{domain.PinnedHandleTargets:N0}");

            writer.WriteDetailText(domain.TotalHandles >= 10_000 || domain.PinnedHandleTargets >= 1_000
                ? "⚠️  Elevated handle pressure detected."
                : "✅ Handle pressure appears within expected range.");
            writer.WriteDetailDivider();
        }

            }
        }



