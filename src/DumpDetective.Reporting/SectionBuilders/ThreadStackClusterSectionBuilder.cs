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
        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Alive Threads",       d.AliveThreadCount.ToString("N0"),  d.AliveThreadCount),
            KM("Unique Signatures",   d.UniqueClusters.ToString("N0"),    d.UniqueClusters),
            KM("Singleton Signatures",d.SingletonSignatures.ToString("N0"),d.SingletonSignatures),
            KM("Signature Diversity", $"{d.DiversityPercent:F1}%",        d.DiversityPercent),
        };
        var blocks = new List<SectionBlock>();

        if (d.TopClusterSignatures.Count > 0)
        {
            int sigLimit = Math.Min(d.TopClusterSignatures.Count, TopSignaturesToShow);
            for (int i = 0; i < sigLimit; i++)
                blocks.Add(Li(FormatHelper.TruncateString(d.TopClusterSignatures[i], 120)));
        }

        var clusters = d.TopClusters ?? [];
        if (clusters.Count > 0)
        {
            for (int i = 0; i < clusters.Count; i++)
            {
                var cluster = clusters[i];
                string osIds = cluster.SampleOsThreadIds.Count == 0
                    ? "none"
                    : string.Join(", ", BuildOsIdList(cluster.SampleOsThreadIds));

                blocks.Add(CollapseBegin($"[{cluster.Count} threads] OSThreadIds: {osIds}"));
                blocks.Add(M("Thread Count", $"{cluster.Count:N0}", cluster.Count, 1));
                blocks.Add(T(FormatHelper.TruncateString(cluster.Signature, 220), 1));
                blocks.Add(CollapseEnd());
            }
        }

        blocks.Add(d.DiversityPercent < 20
            ? T("Low signature diversity; large clusters may indicate coordinated blocking/contention.")
            : T("Signature diversity suggests varied active work."));

        // Exported artifacts note (if any)
        if (d.Artifacts is { Count: > 0 })
        {
            blocks.Add(T("This analyzer produced on-disk exports for deeper offline inspection."));
            foreach (var a in d.Artifacts)
            {
                if (a.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(Li($"{a.FileName} — Pretty JSON; open in VS Code or any JSON viewer."));
                else if (a.FileName.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(Li($"{a.FileName} — NDJSON + gzip (streamable). To inspect: 'gzip -cd {a.FileName} | jq -C \'.' or open in 7-Zip/VS Code after extraction."));
                else
                    blocks.Add(Li(a.FileName));
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: AnalyzerName,
            DisplayTitle: AnalyzerName,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics);
    }

    private static IEnumerable<string> BuildOsIdList(IReadOnlyList<uint> ids)
    {
        for (int i = 0; i < ids.Count; i++)
            yield return $"0x{ids[i]:X}";
    }
}
