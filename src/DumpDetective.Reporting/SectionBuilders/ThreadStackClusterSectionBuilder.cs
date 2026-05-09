using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ThreadStackClusterSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopSignaturesToShow = 5;

    public string AnalyzerName => "Thread Stack Cluster Analysis";
    public int SortOrder => 65;

    public bool CanHandle(AnalyzerDomainResult result) => result is ThreadStackClusterDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ThreadStackClusterDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("CLUSTER SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Alive Threads", $"{d.AliveThreadCount:N0}", d.AliveThreadCount));
        blocks.Add(M("Unique Signatures", $"{d.UniqueClusters:N0}", d.UniqueClusters));
        blocks.Add(M("Singleton Signatures", $"{d.SingletonSignatures:N0}", d.SingletonSignatures));
        blocks.Add(M("Signature Diversity", $"{d.DiversityPercent:F1}%", d.DiversityPercent));

        if (d.TopClusterSignatures.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP SIGNATURES"));
            blocks.Add(Divider());

            int sigLimit = Math.Min(d.TopClusterSignatures.Count, TopSignaturesToShow);
            for (int i = 0; i < sigLimit; i++)
                blocks.Add(Li(FormatHelper.TruncateString(d.TopClusterSignatures[i], 120)));
        }

        var clusters = d.TopClusters ?? [];
        if (clusters.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP THREAD CLUSTERS"));
            blocks.Add(Divider());

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

        blocks.Add(Blank());
        blocks.Add(H("DIVERSITY SIGNAL"));
        blocks.Add(Divider());
        blocks.Add(d.DiversityPercent < 20
            ? T("Low signature diversity; large clusters may indicate coordinated blocking/contention.")
            : T("Signature diversity suggests varied active work."));

        // Exported artifacts note (if any)
        if (d.RawExports is { Count: > 0 })
        {
            blocks.Add(Blank());
            blocks.Add(H("EXPORTS"));
            blocks.Add(Divider());
            blocks.Add(T("This analyzer produced on-disk exports for deeper offline inspection."));

            foreach (var a in d.RawExports)
            {
                // Friendly guidance: JSON for quick viewing; NDJSON+gzip for tooling/streaming
                if (a.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(Li($"{a.FileName} — Pretty JSON; open in VS Code or any JSON viewer."));
                else if (a.FileName.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(Li($"{a.FileName} — NDJSON + gzip (streamable). To inspect: 'gzip -cd {a.FileName} | jq -C \'.' or open in 7-Zip/VS Code after extraction."));
                else
                    blocks.Add(Li(a.FileName));
            }
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }

    private static IEnumerable<string> BuildOsIdList(IReadOnlyList<uint> ids)
    {
        for (int i = 0; i < ids.Count; i++)
            yield return $"0x{ids[i]:X}";
    }
}
