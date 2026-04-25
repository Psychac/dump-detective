using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class ThreadStackClusterPrinter : IAnalyzerReporter
    {
        private const int TopSignaturesToShow = 5;

        public string AnalyzerName => "Thread Stack Signature Clustering";
        public string DisplayTitle => "Thread Stack Cluster Analysis";
        public int SortOrder => 150;

        public bool CanHandle(AnalyzerDomainResult result) => result is ThreadStackClusterDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not ThreadStackClusterDomainResult domain)
                return;

            writer.WriteHeader("THREAD STACK SIGNATURE CLUSTERING:");
            writer.WriteSubHeading("CLUSTER SUMMARY:");
            writer.WriteSeparator();
            writer.WriteMetric("Alive threads", $"{domain.AliveThreadCount:N0}");
            writer.WriteMetric("Unique stack signatures", $"{domain.UniqueClusters:N0}");
            writer.WriteMetric("Singleton signatures", $"{domain.SingletonSignatures:N0}");
            writer.WriteMetric("Signature diversity", $"{domain.DiversityPercent:F1}%");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP SIGNATURES:");
            writer.WriteSeparator();
            int shown = 0;
            foreach (var signature in domain.TopClusterSignatures)
            {
                if (shown >= TopSignaturesToShow)
                    break;

                writer.WriteDetailBullet(FormatHelper.TruncateString(signature, 120), indentLevel: 1);
                shown++;
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP THREAD CLUSTERS:");
            writer.WriteSeparator();
            var clusters = domain.TopClusters ?? [];
            if (clusters.Count == 0)
            {
                writer.WriteDetailText("No cluster detail entries available.");
            }
            else
            {
                foreach (var cluster in clusters)
                {
                    string osIds = cluster.SampleOsThreadIds.Count == 0
                        ? "none"
                        : string.Join(", ", cluster.SampleOsThreadIds.Select(id => $"0x{id:X}"));
                    writer.WriteDetailText($"[{cluster.Count,4} threads] Sample OSThreadIds: {osIds}");
                    writer.WriteMetric("Signature", FormatHelper.TruncateString(cluster.Signature, 220));
                    writer.WriteDetailBlank();
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("DIVERSITY SIGNAL:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.DiversityPercent < 20
                ? "⚠️  Low signature diversity; large clusters may indicate coordinated blocking/contention."
                : "✅ Signature diversity suggests varied active work.");

            writer.WriteDetailDivider();
        }
    }
}



