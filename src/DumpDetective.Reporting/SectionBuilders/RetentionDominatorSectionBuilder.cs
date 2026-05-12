using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class RetentionDominatorSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.retention-dominators";
    public string DisplayTitle => "Retention & Dominators";
    public int SortOrder => 1150;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<RetentionDomainResult>() is not null
        || results.Get<GCRootDomainResult>() is not null
        || results.Get<StaticRootDomainResult>() is not null
        || results.Get<EventLeakDomainResult>() is not null
        || results.Get<FinalizableObjectDomainResult>() is not null
        || results.Get<LeakCandidateDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        RetentionDomainResult? retention = results.Get<RetentionDomainResult>();
        GCRootDomainResult? gcRoot = results.Get<GCRootDomainResult>();
        StaticRootDomainResult? staticRoots = results.Get<StaticRootDomainResult>();
        EventLeakDomainResult? eventLeaks = results.Get<EventLeakDomainResult>();
        FinalizableObjectDomainResult? finalizers = results.Get<FinalizableObjectDomainResult>();
        LeakCandidateDomainResult? leakCandidates = results.Get<LeakCandidateDomainResult>();
        DominatorDomainResult? dominators = results.Get<DominatorDomainResult>();

        var blocks = new List<SectionBlock>
        {
            H("RETENTION INTELLIGENCE SUMMARY"),
            M("Highly referenced objects", retention?.HighlyReferencedObjectCount.ToString("N0") ?? "N/A", retention?.HighlyReferencedObjectCount),
            M("Finalizer queue objects", finalizers?.FinalizerQueueCount.ToString("N0") ?? "N/A", finalizers?.FinalizerQueueCount),
            M("GC roots", gcRoot?.TotalRoots.ToString("N0") ?? "N/A", gcRoot?.TotalRoots),
            M("Static roots", staticRoots?.RootCount.ToString("N0") ?? "N/A", staticRoots?.RootCount),
            M("Event leak groups", eventLeaks?.TopLeakGroups?.Count.ToString("N0") ?? "N/A", eventLeaks?.TopLeakGroups?.Count),
            M("Leak candidates", leakCandidates?.TopCandidates.Count.ToString("N0") ?? "N/A", leakCandidates?.TopCandidates.Count),
            M("Dominator suspects", dominators?.TopDominatorTypes.Count.ToString("N0") ?? "N/A", dominators?.TopDominatorTypes.Count),
        };

        if (retention is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("RETENTION HOTSPOTS"));
            blocks.Add(T(retention.ReferenceCountingSkipped
                ? "Reference counting was skipped; retention counts are unavailable for this dump."
                : retention.ObjectScanCapped
                    ? "Incoming-reference counting was capped; retention counts may be partial."
                    : "Retention counts are available for highly-referenced objects."));

            if (retention.TopRetentionTypes is { Count: > 0 })
            {
                var proxyRows = new List<RetentionTypeSnapshot>(retention.TopRetentionTypes);
                proxyRows.Sort((a, b) => CompareRetentionRatio(b, a));

                blocks.Add(new TableBlock(
                    Caption: "Top retention types",
                    Headers: ["Type", "Objects", "Footprint", "Incoming Refs", "Retained", "Ratio"],
                    Rows: retention.TopRetentionTypes.Take(10).Select(type => Row(
                        Cell(type.TypeName),
                        Cell(type.ObjectCount.ToString("N0"), type.ObjectCount),
                        Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                        Cell(type.TotalIncomingReferences.ToString("N0"), type.TotalIncomingReferences),
                        Cell(type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—", (long)Math.Min(type.EstimatedRetainedBytes, long.MaxValue)),
                        Cell(FormatRatio(type.EstimatedRetainedBytes, type.TotalBytes), (long)Math.Round(RatioValue(type.EstimatedRetainedBytes, type.TotalBytes) * 1000)))).ToList()));

                blocks.Add(Blank());
                blocks.Add(H("TOP 20 BY RETENTION RATIO"));
                blocks.Add(T("Retention ratio is estimated from bounded BFS retained bytes on the top highly referenced objects. Results remain capped by breadth and depth."));
                blocks.Add(new TableBlock(
                    Caption: "Top retention types by ratio",
                    Headers: ["Type", "Objects", "Footprint", "Incoming Refs", "Retained", "Ratio"],
                    Rows: proxyRows.Take(20).Select(type => Row(
                        Cell(type.TypeName),
                        Cell(type.ObjectCount.ToString("N0"), type.ObjectCount),
                        Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                        Cell(type.TotalIncomingReferences.ToString("N0"), type.TotalIncomingReferences),
                        Cell(type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—", (long)Math.Min(type.EstimatedRetainedBytes, long.MaxValue)),
                        Cell(FormatRatio(type.EstimatedRetainedBytes, type.TotalBytes), (long)Math.Round(RatioValue(type.EstimatedRetainedBytes, type.TotalBytes) * 1000)))).ToList()));
            }
        }

        if (gcRoot is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("GC ROOT DISTRIBUTION"));
            if (gcRoot.ByKind.Count > 0)
            {
                blocks.Add(new TableBlock(
                    Caption: "GC root kinds",
                    Headers: ["Root Kind", "Count", "Est. Retained", "% of Heap"],
                    Rows: gcRoot.ByKind.Take(10).Select(kind => Row(
                        Cell(kind.Kind),
                        Cell(kind.Count.ToString("N0"), kind.Count),
                        Cell(FormatBytes(kind.EstimatedRetainedBytes), (long)Math.Min(kind.EstimatedRetainedBytes, long.MaxValue)),
                        Cell(kind.PctOfManagedHeap.ToString("F1") + "%", (long)Math.Round(kind.PctOfManagedHeap * 10)))).ToList()));
            }

            if (gcRoot.TopRootsBySeverity.Count > 0)
            {
                blocks.Add(Blank());
                blocks.Add(H("TOP ROOT SEVERITY"));
                blocks.Add(new TableBlock(
                    Caption: "Top GC roots by severity",
                    Headers: ["Root Kind", "Target Type", "Est. Retained", "Severity", "Root Addr"],
                    Rows: gcRoot.TopRootsBySeverity.Take(10).Select(root => Row(
                        Cell(root.RootKind),
                        Cell(root.TargetTypeName),
                        Cell(FormatBytes(root.EstimatedRetainedBytes), (long)Math.Min(root.EstimatedRetainedBytes, long.MaxValue)),
                        Cell(root.SeverityScore.ToString("N0"), root.SeverityScore),
                        Cell($"0x{root.RootAddress:X}"))).ToList()));
            }

            if (gcRoot.RootPaths.Count > 0)
            {
                blocks.Add(Blank());
                blocks.Add(H("ROOT PATHS"));
                blocks.Add(T("Forward BFS root paths show how objects remain reachable from the GC root target object."));
                blocks.Add(new TableBlock(
                    Caption: "Root retention paths",
                    Headers: ["Root Kind", "Target Type", "Path Length", "Capped", "First Types Seen"],
                    Rows: gcRoot.RootPaths.Take(10).Select(path => Row(
                        Cell(path.RootKind),
                        Cell(path.TargetTypeName),
                        Cell(path.PathLength.ToString("N0"), path.PathLength),
                        Cell(path.WasCapped ? "yes" : "no"),
                        Cell(string.Join(" → ", path.PathTypeNames.Take(8).Select(TrimTypeName))))).ToList()));
            }
        }

        if (staticRoots is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("STATIC ROOT HOTSPOTS"));
            blocks.Add(M("Root count", staticRoots.RootCount.ToString("N0"), staticRoots.RootCount));
            blocks.Add(M("Total retained bytes", FormatBytes(staticRoots.TotalRetainedBytes), (double)Math.Min(staticRoots.TotalRetainedBytes, long.MaxValue)));
            if (staticRoots.TopRootsByRetainedBytes is { Count: > 0 })
            {
                blocks.Add(new TableBlock(
                    Caption: "Static roots by retained bytes",
                    Headers: ["Field / Type", "Retained Bytes"],
                    Rows: staticRoots.TopRootsByRetainedBytes.Take(8).Select(root => Row(
                        Cell(root.Name),
                        Cell(FormatBytes(root.Bytes), (long)Math.Min(root.Bytes, long.MaxValue)))).ToList()));
            }
        }

        if (eventLeaks is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("EVENT RETENTION"));
            blocks.Add(M("Static event leaks", eventLeaks.StaticEventLeakCount.ToString("N0"), eventLeaks.StaticEventLeakCount));
            blocks.Add(M("Instance event leaks", eventLeaks.InstanceEventLeakCount.ToString("N0"), eventLeaks.InstanceEventLeakCount));
            blocks.Add(M("Total subscribers", eventLeaks.TotalSubscribers.ToString("N0"), eventLeaks.TotalSubscribers));
        }

        if (finalizers is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("FINALIZER RETENTION"));
            blocks.Add(M("Finalizer queue count", finalizers.FinalizerQueueCount.ToString("N0"), finalizers.FinalizerQueueCount));
            blocks.Add(M("Queue retained bytes", FormatBytes(finalizers.FinalizerQueueRetainedBytes), (double)Math.Min(finalizers.FinalizerQueueRetainedBytes, long.MaxValue)));
            blocks.Add(M("Potential resurrection", finalizers.PotentialResurrectionDetected ? "Yes" : "No", finalizers.PotentialResurrectionDetected ? 1.0 : 0.0));
        }

        if (leakCandidates is not null)
        {
            blocks.Add(Blank());
            blocks.Add(H("LEAK CANDIDATE NARRATIVE"));
            if (leakCandidates.TopCandidates.Count > 0)
            {
                LeakCandidateRecord top = leakCandidates.TopCandidates[0];
                blocks.Add(T($"Top suspect {top.TypeName} scores {top.SuspicionScore:N0} because it is {top.Classification} with {FormatBytes(top.TotalSize)} shallow size and {top.Gen2Pct:F1}% Gen2 occupancy."));
                blocks.Add(T($"Root hint: {(string.IsNullOrWhiteSpace(top.RootKind) ? "unknown" : top.RootKind)}. Review GC root paths, static ownership, and queue retention for this type."));
            }
        }

        if (dominators is not null && dominators.TopDominatorTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("DOMINATOR SUSPECTS"));
            blocks.Add(T(dominators.HeuristicOnly
                ? $"Retained bytes are estimated with a bounded BFS over {dominators.AnalyzedCount:N0} suspects (breadth cap {dominators.MaxBreadth:N0}, depth cap {dominators.MaxDepth:N0})."
                : "Retained bytes are available for the listed suspects."));
            blocks.Add(new TableBlock(
                Caption: "Top dominator suspects",
                Headers: ["Type", "Objects", "Shallow", "Retained", "Ratio"],
                Rows: dominators.TopDominatorTypes.Take(10).Select(type => Row(
                    Cell(type.TypeName),
                    Cell(type.Count.ToString("N0"), type.Count),
                    Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                    Cell(type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—", (long)Math.Min(type.EstimatedRetainedBytes, long.MaxValue)),
                    Cell(FormatRatio(type.EstimatedRetainedBytes, type.TotalBytes), (long)Math.Round(RatioValue(type.EstimatedRetainedBytes, type.TotalBytes) * 1000)))).ToList()));
        }

        return new AnalyzerDetailSection(
            AnalyzerName: "Retention Intelligence",
            DisplayTitle: DisplayTitle,
            SortOrder: SortOrder,
            Blocks: blocks);
    }

    private static string TrimTypeName(string typeName)
    {
        int dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }

    private static string FormatRatio(ulong retainedBytes, ulong shallowBytes)
    {
        if (shallowBytes == 0)
            return "—";

        return $"{(double)retainedBytes / shallowBytes:F2}x";
    }

    private static double RatioValue(ulong retainedBytes, ulong shallowBytes)
        => shallowBytes == 0 ? 0.0 : (double)retainedBytes / shallowBytes;

    private static int CompareRetentionRatio(RetentionTypeSnapshot left, RetentionTypeSnapshot right)
    {
        double leftRatio = RatioValue(left.EstimatedRetainedBytes, left.TotalBytes);
        double rightRatio = RatioValue(right.EstimatedRetainedBytes, right.TotalBytes);

        int cmp = rightRatio.CompareTo(leftRatio);
        return cmp != 0 ? cmp : right.TotalBytes.CompareTo(left.TotalBytes);
    }
}