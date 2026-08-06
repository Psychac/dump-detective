using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ThreadStackClusterSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopSignaturesToShow = 5;

    public string AnalyzerName => "Thread Stack Signature Clustering";
    public int SortOrder => 110;

    public bool CanHandle(AnalyzerDomainResult result) => result is ThreadStackClusterDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ThreadStackClusterDomainResult)result;
        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["alive_threads"] = new NumericMetricValue(d.AliveThreadCount, MetricUnit.Count),
            ["unique_signatures"] = new NumericMetricValue(d.UniqueClusters, MetricUnit.Count),
            ["singleton_signatures"] = new NumericMetricValue(d.SingletonSignatures, MetricUnit.Count),
            ["signature_diversity_pct"] = new NumericMetricValue(d.DiversityPercent, MetricUnit.Percent, $"{d.DiversityPercent:F1}%"),
        };
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        if (d.TopClusterSignatures.Count > 0)
        {
            int sigLimit = Math.Min(d.TopClusterSignatures.Count, TopSignaturesToShow);
            for (int i = 0; i < sigLimit; i++)
                blocks.Add(T(FormatHelper.TruncateString(d.TopClusterSignatures[i], 120)));
        }

        blocks.Add(d.DiversityPercent <= 25
            ? T("Low signature diversity; large clusters may indicate coordinated blocking/contention.")
            : T("Signature diversity suggests varied active work."));

        // Typed StackClusters slot
        var stackClusters = new List<StackCluster>();
        var clusters = d.TopClusters ?? [];
        for (int i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var osIds = new List<string>(cluster.SampleOsThreadIds.Count);
            for (int j = 0; j < cluster.SampleOsThreadIds.Count; j++)
                osIds.Add($"0x{cluster.SampleOsThreadIds[j]:X}");
            stackClusters.Add(new StackCluster(
                ThreadCount: cluster.Count,
                OsThreadIds: osIds,
                Signature:   cluster.Signature,
                Truncated:   false));
        }

        // Typed Artifacts slot
        var artifacts = new List<AnalyzerArtifact>();
        foreach (var a in d.Artifacts ?? [])
        {
            string instructions = a.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? "Pretty JSON — open in VS Code or any JSON viewer."
                : a.FileName.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase)
                    ? $"NDJSON + gzip (streamable). Inspect with: gzip -cd {a.FileName} | jq -C '.' or open in 7-Zip/VS Code after extraction."
                    : "Analyzer export file.";
            artifacts.Add(new AnalyzerArtifact(a.FileName, instructions));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: AnalyzerName,
            DisplayTitle: AnalyzerName,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            StackClusters: stackClusters.Count > 0 ? stackClusters : null,
            Artifacts:     artifacts.Count > 0 ? artifacts : null);
    }
}
