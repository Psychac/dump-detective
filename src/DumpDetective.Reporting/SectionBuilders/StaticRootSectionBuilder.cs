using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class StaticRootSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopRootsToShow = 8;

    public string AnalyzerName => "Static Root Leak Detection";
    public string DisplayTitle => "Static Roots";
    public int SortOrder => 600;

    public bool CanHandle(AnalyzerDomainResult result) => result is StaticRootDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (StaticRootDomainResult)result;
        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["concerning_static_roots"] = new NumericMetricValue(d.RootCount, MetricUnit.Count),
            ["total_retained_bytes"] = new NumericMetricValue((double)d.TotalRetainedBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalRetainedBytes)),
        };

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var roots = d.TopRootsByRetainedBytes ?? [];
        if (roots.Count > 0)
        {
            int limit = Math.Min(roots.Count, TopRootsToShow);
            var rootRows = new List<CompactRow>(limit);
            for (int i = 0; i < limit; i++)
            {
                var r = roots[i];
                string bytesDisplay = FormatHelper.FormatBytes(r.TotalMemoryImpact);
                if (r.ScanWasCapped)
                    bytesDisplay += " (estimate — scan capped)";

                rootRows.Add(R(
                    FormatHelper.TruncateString(r.RootDescription, 90),
                    r.TypeName,
                    bytesDisplay,
                    (double)r.ObjectsKeptAlive));
            }
            compactTables.Add(STCompact("Top roots by retained bytes", new[] { CH("Root"), CH("Type"), CH("Retained Bytes","bytes"), CH("Objects Kept Alive","number") }, rootRows));

            var collectionRoots = roots.Where(r => r.ContainsCollections).ToList();
            if (collectionRoots.Count > 0)
            {
                blocks.Add(T($"⚠️ {collectionRoots.Count} root(s) retain collection objects — likely cache-pattern retention."));
            }

            var eventHandlerRoots = roots.Where(r => r.ContainsEventHandlers).ToList();
            if (eventHandlerRoots.Count > 0)
            {
                blocks.Add(T($"⚠️ {eventHandlerRoots.Count} root(s) retain event handler objects — check for unsubscription leaks."));
            }

            var alcRoots = roots.Where(r => !string.IsNullOrEmpty(r.AssemblyLoadContextInfo)).ToList();
            if (alcRoots.Count > 0)
            {
                blocks.Add(T($"⚠️ {alcRoots.Count} root(s) belong to non-default AppDomains — indicates potential plugin unload failure."));
            }

            for (int i = 0; i < limit; i++)
            {
                var r = roots[i];
                var topTypes = r.TopRetainedTypes;
                if (topTypes != null && topTypes.Count > 0)
                {
                    var typeRows = new List<CompactRow>();
                    foreach (var typeInfo in topTypes)
                    {
                        typeRows.Add(R(
                            typeInfo.TypeName,
                            (double)typeInfo.Count,
                            (ulong)typeInfo.TotalSize));
                    }
                    compactTables.Add(STCompact($"Top retained types in '{FormatHelper.TruncateString(r.RootDescription, 60)}'",
                        new[] { CH("Type"), CH("Count","number"), CH("Total Size","bytes") },
                        typeRows));
                }
            }
        }
        else
        {
            blocks.Add(T("No root-level retained-byte breakdown available."));
        }

        blocks.Add(d.RootCount >= 10
            ? T("High static-root pressure detected; review long-lived static ownership.")
            : T("Static-root pressure appears moderate in this dump."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
