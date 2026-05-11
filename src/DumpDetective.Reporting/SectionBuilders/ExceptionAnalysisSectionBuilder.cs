using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ExceptionAnalysisSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    public string SectionId => "prof.exception-analysis";
    public string DisplayTitle => "Exception Analysis";
    public int SortOrder => 1600;

    public bool CanBuild(AnalyzerResultSet results) => results.Get<CrashDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        CrashDomainResult? crash = results.Get<CrashDomainResult>();
        ThreadDomainResult? threads = results.Get<ThreadDomainResult>();
        ModuleDomainResult? modules = results.Get<ModuleDomainResult>();

        var blocks = new List<SectionBlock>
        {
            H("EXCEPTION FREQUENCY"),
            T("Crash analysis is summarized here with active-vs-total counts, likely hotspots, and inferred trace provenance."),
        };

        if (crash is null)
        {
            blocks.Add(T("No crash analysis result was available."));
            return new AnalyzerDetailSection("Exception Analysis", DisplayTitle, SortOrder, blocks);
        }

        blocks.Add(new TableBlock(
            Caption: "Exception counts",
            Headers: ["Signal", "Count", "Notes"],
            Rows:
            [
                Row(Cell("Total exceptions"), Cell(crash.TotalExceptions.ToString("N0"), crash.TotalExceptions), Cell("All exception objects")),
                Row(Cell("Active exceptions"), Cell(crash.ActiveExceptions.ToString("N0"), crash.ActiveExceptions), Cell("Exceptions currently on threads")),
                Row(Cell("Unique types"), Cell(crash.ExceptionTypeCounts.Count.ToString("N0"), crash.ExceptionTypeCounts.Count), Cell("Distinct exception types")),
                Row(Cell("Inferred traces"), Cell(crash.InferredTraceCount.ToString("N0"), crash.InferredTraceCount), Cell("Heuristic original stack traces")),
            ]));

        blocks.Add(Blank());
        blocks.Add(H("TOP EXCEPTION TYPES"));
        var typeRows = new List<TableRow>(crash.ExceptionTypeCounts.Count);
        foreach (KeyValuePair<string, int> kvp in crash.ExceptionTypeCounts.OrderByDescending(kvp => kvp.Value).Take(15))
        {
            crash.ActiveExceptionTypeCounts.TryGetValue(kvp.Key, out int activeCount);
            typeRows.Add(Row(
                Cell(kvp.Key),
                Cell(kvp.Value.ToString("N0"), kvp.Value),
                Cell(activeCount > 0 ? activeCount.ToString("N0") : "-", activeCount > 0 ? activeCount : null)));
        }

        blocks.Add(new TableBlock("Top exception types", ["Exception Type", "Count", "Active"], typeRows));

        if (crash.TopCrashThreadCandidates is { Count: > 0 })
        {
            blocks.Add(Blank());
            blocks.Add(H("FAILURE HOTSPOTS"));
            var hotspotRows = new List<TableRow>(crash.TopCrashThreadCandidates.Count);
            for (int i = 0; i < crash.TopCrashThreadCandidates.Count; i++)
            {
                CrashThreadCandidateSnapshot candidate = crash.TopCrashThreadCandidates[i];
                hotspotRows.Add(Row(
                    Cell(candidate.ThreadId.ToString("N0"), candidate.ThreadId),
                    Cell(candidate.OSThreadId.ToString("N0"), candidate.OSThreadId),
                    Cell(candidate.ActiveExceptionCount.ToString("N0"), candidate.ActiveExceptionCount),
                    Cell(candidate.PrimaryExceptionType),
                    Cell(candidate.OriginalStackTraceConfidence.ToString())));
            }

            blocks.Add(new TableBlock("Crash thread candidates", ["Managed Thread", "OS Thread", "Active Exceptions", "Primary Exception", "Trace Confidence"], hotspotRows));

            if (threads is not null && threads.TopBlockedThreads is { Count: > 0 })
                blocks.Add(T("Thread hotspots can be cross-checked against the blocked-thread tables in the thread/concurrency section."));
        }

        if (crash.TopExceptionInstances is { Count: > 0 })
        {
            blocks.Add(Blank());
            blocks.Add(H("EXCEPTION INSTANCES"));
            var rows = new List<TableRow>(crash.TopExceptionInstances.Count);
            for (int i = 0; i < crash.TopExceptionInstances.Count; i++)
            {
                ExceptionInstanceSnapshot ex = crash.TopExceptionInstances[i];
                rows.Add(Row(
                    Cell(ex.Type),
                    Cell($"0x{ex.Address:X}"),
                    Cell(ex.Message ?? "—"),
                    Cell(ex.HResult.HasValue ? $"0x{ex.HResult.Value:X8}" : "—"),
                    Cell(ex.InnerExceptionType ?? "—"),
                    Cell(ex.IsActive ? "ACTIVE" : "Inactive")));
            }

            blocks.Add(new TableBlock("Exception instances", ["Type", "Address", "Message", "HRESULT", "Inner Type", "Status"], rows));
        }

        blocks.Add(Blank());
        blocks.Add(H("FRAME ORIGIN NOTES"));
        blocks.Add(T(modules is null
            ? "Frame origin classification is approximate because module data was unavailable."
            : "Frames can be classified as FrameworkCode, ThirdParty, or UserCode by module prefix and module inventory."));

        return new AnalyzerDetailSection("Exception Analysis", DisplayTitle, SortOrder, blocks);
    }
}