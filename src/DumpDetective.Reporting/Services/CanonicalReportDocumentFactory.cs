using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

internal sealed class CanonicalReportDocumentFactory(ReportSerializer serializer)
{
    private readonly ReportSerializer _serializer = serializer;

    public AnalysisReportDocument BuildDocument(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        TimeSpan elapsed,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerBuilders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        ReportAudience audience = ReportAudience.All,
        AnalysisIncidentContext? incidentContext = null,
        IReadOnlyList<InsightFinding>? additionalFindings = null)
        => _serializer.Serialize(dumpPath, runs, elapsed, analyzerBuilders, reportBuilders, audience, incidentContext, additionalFindings);

    public AnalysisReportDocument BuildSnapshotDocument(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerBuilders,
        ReportAudience audience = ReportAudience.All,
        AnalysisIncidentContext? incidentContext = null)
        => _serializer.Serialize(dumpPath, runs, TimeSpan.Zero, analyzerBuilders, [], audience, incidentContext);

    public IReadOnlyList<AnalyzerDetailSection> BuildSnapshotSections(
        string dumpPath,
        IReadOnlyList<AnalyzerRunResult> runs,
        IReadOnlyList<IAnalyzerSectionBuilder> analyzerBuilders,
        IReadOnlyList<IReportSectionBuilder> reportBuilders,
        ReportAudience audience = ReportAudience.All,
        AnalysisIncidentContext? incidentContext = null)
        => _serializer.SerializeSectionsOnly(runs, analyzerBuilders, reportBuilders, incidentContext);
}