using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Serialization;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Splits a single-dump report into a small core and lazy segments (ReportSystemVision § 14.1; docs/ReportStructure/m4-payload-plan.md D6, D9, D10):
/// every section's typed slots and tables become one section segment, a table over <see cref="LazyTableRowThreshold"/> rows ships its rows as a
/// <see cref="ColumnTable"/> segment of its own, and the entity table and dossier data move out. The core keeps what the page shows before anything is opened:
/// section id, title, severity, lead finding, key metrics and narrative blocks. Run after <c>EntityCellPass</c> and <c>ReportStringPool</c>, so cells are already
/// pool indexes and entity ids. Trend reports are not segmented yet (plan D4) and come back unchanged.
/// </summary>
internal static class ReportSegmenter
{
    // The viewer already hydrates a table lazily above this many rows (report.renderers.sections.js).
    public const int LazyTableRowThreshold = 180;

    public const string EntitiesSegmentId = "e-entities";
    public const string EntityDossierSegmentId = "e-dossier";

    public static SegmentedReport Segment(AnalysisReportDocument document)
    {
        var segments = new List<ReportSegment>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<ReportDomainSection>? domains = document.Domains is null ? null : SegmentDomains(document.Domains, string.Empty, segments, usedIds);
        AnalysisReportDocument core = document with { Domains = domains };

        // A trend report's own sections (the metric timeline above all) hold their data in table blocks and are segmented the same way. Its per-dump
        // documents are embedded separately and are not segmented yet.
        if (core is TrendReportDocument trend && trend.TrendAnalyzerSections.Count > 0)
        {
            var trendSections = new List<AnalyzerDetailSection>(trend.TrendAnalyzerSections.Count);
            foreach (AnalyzerDetailSection section in trend.TrendAnalyzerSections)
                trendSections.Add(SegmentSection(section, string.Empty, segments, usedIds));

            core = trend with { TrendAnalyzerSections = trendSections };
        }

        // Each per-dump document is a full single-dump report, so its sections are segmented the same way, with the dump's index in every id
        // ("s-d1-A5") so the dumps' sections, which share section ids, do not collide. Entity data and the string pool are not segmented here:
        // a per-dump document is embedded without them.
        if (core is TrendReportDocument withDumps && withDumps.PerDumpDocuments.Count > 0)
        {
            var dumpDocuments = new List<AnalysisReportDocument>(withDumps.PerDumpDocuments.Count);
            for (int dump = 0; dump < withDumps.PerDumpDocuments.Count; dump++)
            {
                AnalysisReportDocument dumpDocument = withDumps.PerDumpDocuments[dump];
                dumpDocuments.Add(dumpDocument.Domains is null
                    ? dumpDocument
                    : dumpDocument with { Domains = SegmentDomains(dumpDocument.Domains, $"d{dump}-", segments, usedIds) });
            }

            core = withDumps with { PerDumpDocuments = dumpDocuments };
        }

        if (document.Entities is not null)
        {
            segments.Add(new ReportSegment(EntitiesSegmentId, ReportSegmentKind.Entities, Serialize(document.Entities)));
            core = core with { Entities = null };
        }

        if (document.EntitySummary is not null || document.Relations is not null)
        {
            segments.Add(new ReportSegment(
                EntityDossierSegmentId, ReportSegmentKind.EntityDossier, Serialize(new EntityDossierPayload(document.EntitySummary, document.Relations))));
            core = core with { EntitySummary = null, Relations = null };
        }

        return new SegmentedReport(core, segments);
    }

    private static IReadOnlyList<ReportDomainSection> SegmentDomains(
        IReadOnlyList<ReportDomainSection> domains, string idScope, List<ReportSegment> segments, HashSet<string> usedIds)
    {
        var result = new List<ReportDomainSection>(domains.Count);
        foreach (ReportDomainSection domain in domains)
        {
            var sections = new List<AnalyzerDetailSection>(domain.Sections.Count);
            foreach (AnalyzerDetailSection section in domain.Sections)
                sections.Add(SegmentSection(section, idScope, segments, usedIds));

            result.Add(domain with { Sections = sections });
        }

        return result;
    }

    private static AnalyzerDetailSection SegmentSection(AnalyzerDetailSection section, string idScope, List<ReportSegment> segments, HashSet<string> usedIds)
    {
        if (!HasLazyContent(section))
            return section;

        string sectionPart = UniqueId(idScope + (string.IsNullOrEmpty(section.SectionId) ? "section" : section.SectionId), usedIds);
        string segmentId = "s-" + sectionPart;
        List<BlockTableRows>? blockTableRows = null;
        IReadOnlyList<SectionBlock> coreBlocks = section.Blocks;
        for (int i = 0; i < section.Blocks.Count; i++)
        {
            if (section.Blocks[i] is not TableBlock { Rows.Count: > 0 } tableBlock)
                continue;

            if (blockTableRows is null)
            {
                blockTableRows = [];
                coreBlocks = section.Blocks.ToList();
            }

            blockTableRows.Add(new BlockTableRows(i, tableBlock.Rows));
            ((List<SectionBlock>)coreBlocks)[i] = tableBlock with { Rows = [] };
        }

        var payload = new SectionSegmentPayload(
            blockTableRows,
            section.CompactTables is null ? null : SegmentTables(section.CompactTables, sectionPart, segments, usedIds),
            section.StackTraces,
            section.RootOwnedSubgraphGroups,
            section.TypeTraces,
            section.LeakCandidateCards,
            section.EventLeakGroupCards,
            section.EventLeakInstanceCards,
            section.SubscriberDetailPool,
            section.StackClusters,
            section.Artifacts,
            section.TreeWidgets);

        segments.Add(new ReportSegment(segmentId, ReportSegmentKind.Section, Serialize(payload)));
        return section with
        {
            Blocks = coreBlocks,
            CompactTables = null,
            StackTraces = null,
            RootOwnedSubgraphGroups = null,
            TypeTraces = null,
            LeakCandidateCards = null,
            EventLeakGroupCards = null,
            EventLeakInstanceCards = null,
            SubscriberDetailPool = null,
            StackClusters = null,
            Artifacts = null,
            TreeWidgets = null,
            SegmentId = segmentId,
        };
    }

    private static IReadOnlyList<CompactTable> SegmentTables(
        IReadOnlyList<CompactTable> tables, string sectionPart, List<ReportSegment> segments, HashSet<string> usedIds)
    {
        var result = new List<CompactTable>(tables.Count);
        for (int index = 0; index < tables.Count; index++)
        {
            CompactTable table = tables[index];
            if (table.Rows.Count <= LazyTableRowThreshold)
            {
                result.Add(table);
                continue;
            }

            string segmentId = "t-" + UniqueId(sectionPart + "-" + index, usedIds);
            segments.Add(new ReportSegment(segmentId, ReportSegmentKind.Table, Serialize(ColumnTableConverter.ToColumns(table))));
            result.Add(table with { Rows = [], RowCount = table.Rows.Count, SegmentId = segmentId });
        }

        return result;
    }

    private static bool HasLazyContent(AnalyzerDetailSection section) =>
        section.Blocks.Any(block => block is TableBlock { Rows.Count: > 0 })
        || section.CompactTables is { Count: > 0 }
        || section.StackTraces is { Count: > 0 }
        || section.RootOwnedSubgraphGroups is { Count: > 0 }
        || section.TypeTraces is { Count: > 0 }
        || section.LeakCandidateCards is { Count: > 0 }
        || section.EventLeakGroupCards is { Count: > 0 }
        || section.EventLeakInstanceCards is { Count: > 0 }
        || section.SubscriberDetailPool is { Count: > 0 }
        || section.StackClusters is { Count: > 0 }
        || section.Artifacts is { Count: > 0 }
        || section.TreeWidgets is { Count: > 0 };

    // Ids come from section ids and positions so the same report always yields the same ids; a repeat (two sections sharing an id) gets a numeric suffix.
    private static string UniqueId(string candidate, HashSet<string> usedIds)
    {
        string id = candidate;
        for (int suffix = 2; !usedIds.Add(id); suffix++)
            id = candidate + "~" + suffix;

        return id;
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)ReportJsonContext.Default.Options.GetTypeInfo(typeof(T)));
}
