using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class ThreadStackClusterPrinter : IAnalyzerReporter
    {
        private const int TopSignaturesToShow = 5;

        public string AnalyzerName => "Thread Stack Signature Clustering";

        public bool CanHandle(AnalyzerDomainResult result) => result is ThreadStackClusterDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not ThreadStackClusterDomainResult domain)
                return;

            writer.WriteHeader("THREAD STACK SIGNATURE CLUSTERING:");
            writer.WriteLine("CLUSTER SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Alive threads: {domain.AliveThreadCount:N0}");
            writer.WriteLine($"Unique stack signatures: {domain.UniqueClusters:N0}");
            writer.WriteLine($"Singleton signatures: {domain.SingletonSignatures:N0}");
            writer.WriteLine($"Signature diversity: {domain.DiversityPercent:F1}%");

            writer.WriteLine("\nTOP SIGNATURES:");
            writer.WriteSeparator();
            int shown = 0;
            foreach (var signature in domain.TopClusterSignatures)
            {
                if (shown >= TopSignaturesToShow)
                    break;

                writer.WriteLine($"  • {FormatHelper.TruncateString(signature, 120)}");
                shown++;
            }

            writer.WriteLine("\nTOP THREAD CLUSTERS:");
            writer.WriteSeparator();
            var clusters = domain.TopClusters ?? [];
            if (clusters.Count == 0)
            {
                writer.WriteLine("No cluster detail entries available.");
            }
            else
            {
                foreach (var cluster in clusters)
                {
                    string osIds = cluster.SampleOsThreadIds.Count == 0
                        ? "none"
                        : string.Join(", ", cluster.SampleOsThreadIds.Select(id => $"0x{id:X}"));
                    writer.WriteLine($"[{cluster.Count,4} threads] Sample OSThreadIds: {osIds}");
                    writer.WriteLine($"Signature: {FormatHelper.TruncateString(cluster.Signature, 220)}");
                    writer.WriteLine(string.Empty);
                }
            }

            writer.WriteLine("\nDIVERSITY SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.DiversityPercent < 20
                ? "⚠️  Low signature diversity; large clusters may indicate coordinated blocking/contention."
                : "✅ Signature diversity suggests varied active work.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}



