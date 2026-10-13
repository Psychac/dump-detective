namespace DumpDetective.Reporting.Models;

internal enum ReportSegmentKind
{
    Section,
    Table,
    Entities,
    EntityDossier,
}

/// <summary>One lazily-loaded piece of the report, as uncompressed JSON; the encoder (not the segmenter) decides how it is embedded.</summary>
internal sealed record ReportSegment(string Id, ReportSegmentKind Kind, string Json);

/// <summary>A report split into a small <paramref name="Core"/> the viewer loads first and the <paramref name="Segments"/> it loads on demand.</summary>
internal sealed record SegmentedReport(AnalysisReportDocument Core, IReadOnlyList<ReportSegment> Segments);

/// <summary>
/// Everything in a section that the viewer builds only when the section is opened: the typed slots and the tables. A table over the
/// lazy-row threshold appears here as a stub (<see cref="CompactTable.RowCount"/> and <see cref="CompactTable.SegmentId"/>, no rows).
/// </summary>
internal sealed record SectionSegmentPayload(
    IReadOnlyList<CompactTable>? CompactTables,
    IReadOnlyList<NamedStackTrace>? StackTraces,
    IReadOnlyList<RootOwnedSubgraphGroup>? RootOwnedSubgraphGroups,
    IReadOnlyList<TypeSampleTrace>? TypeTraces,
    IReadOnlyList<LeakCandidateCard>? LeakCandidateCards,
    IReadOnlyList<EventLeakGroupCard>? EventLeakGroupCards,
    IReadOnlyList<EventLeakInstanceCard>? EventLeakInstanceCards,
    IReadOnlyList<SubscriberDetailEntry>? SubscriberDetailPool,
    IReadOnlyList<StackCluster>? StackClusters,
    IReadOnlyList<AnalyzerArtifact>? Artifacts,
    IReadOnlyList<TreeWidget>? TreeWidgets);

/// <summary>The dossier panel's data, loaded together when a dossier first opens.</summary>
internal sealed record EntityDossierPayload(EntitySummary? EntitySummary, EntityRelations? Relations);
