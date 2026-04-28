using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class CrashSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopExceptionTypes = 10;

    public string AnalyzerName => "Crash Analysis";
    public int SortOrder => 10;

    public bool CanHandle(AnalyzerDomainResult result) => result is CrashDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (CrashDomainResult)result;
        var blocks = new List<SectionBlock>();

        blocks.Add(H("EXCEPTION SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Exception Objects",      $"{d.TotalExceptions:N0}",             d.TotalExceptions));
        blocks.Add(M("Active Exceptions (on threads)", $"{d.ActiveExceptions:N0}",          d.ActiveExceptions));
        blocks.Add(M("Unique Exception Types",       $"{d.ExceptionTypeCounts.Count:N0}",   d.ExceptionTypeCounts.Count));

        if (d.ActiveExceptions > 0)
        {
            blocks.Add(Blank());
            blocks.Add(T($"CRASH DETECTED: {d.ActiveExceptions:N0} active exception(s) found!"));
        }
        else if (d.TotalExceptions == 0)
        {
            blocks.Add(Blank());
            blocks.Add(T("No exceptions detected in dump (likely not a crash dump)."));
        }

        blocks.Add(Blank());
        blocks.Add(H("TOP EXCEPTION TYPES"));
        blocks.Add(Divider());

        // Sort by count descending, take top N — build table
        var sortedTypes = new List<KeyValuePair<string, int>>(d.ExceptionTypeCounts);
        sortedTypes.Sort((a, b) => b.Value.CompareTo(a.Value));

        var excRows = new List<TableRow>(Math.Min(sortedTypes.Count, TopExceptionTypes));
        int excLimit = Math.Min(sortedTypes.Count, TopExceptionTypes);
        for (int i = 0; i < excLimit; i++)
        {
            var kvp = sortedTypes[i];
            d.ActiveExceptionTypeCounts.TryGetValue(kvp.Key, out int activeCount);
            excRows.Add(new TableRow([
                Cell(kvp.Key),
                Cell($"{kvp.Value:N0}", kvp.Value),
                Cell(activeCount > 0 ? $"{activeCount:N0}" : "-", activeCount)]));
        }
        blocks.Add(new TableBlock("Top exception types", ["Exception Type", "Count", "Active"], excRows));

        var candidates = d.TopCrashThreadCandidates ?? [];
        if (candidates.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("LIKELY CRASH THREADS"));
            blocks.Add(Divider());

            for (int rank = 0; rank < candidates.Count; rank++)
            {
                var c = candidates[rank];
                blocks.Add(CollapseBegin($"[{rank + 1}] Thread {c.ThreadId} (OS: {c.OSThreadId}) — {c.ActiveExceptionCount} active exception(s)"));
                blocks.Add(M("Primary exception type", c.PrimaryExceptionType, indent: 1));
                for (int f = 0; f < c.TopFrames.Count; f++)
                    blocks.Add(new PathBlock("Frame", c.TopFrames[f], 2));
                blocks.Add(CollapseEnd());
            }
        }

        var instances = d.TopExceptionInstances ?? [];
        if (instances.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("DETAILED EXCEPTION INFORMATION"));
            blocks.Add(Divider());

            for (int idx = 0; idx < instances.Count; idx++)
            {
                var ex = instances[idx];
                blocks.Add(CollapseBegin($"[{idx + 1}] {ex.Type} @ 0x{ex.Address:X}"));
                if (!string.IsNullOrWhiteSpace(ex.Message))
                    blocks.Add(M("Message", ex.Message, indent: 1));
                if (ex.HResult.HasValue)
                    blocks.Add(M("HRESULT", $"0x{ex.HResult.Value:X8}", indent: 1));
                if (!string.IsNullOrWhiteSpace(ex.InnerExceptionType))
                    blocks.Add(M("Inner Exception", ex.InnerExceptionType, indent: 1));
                blocks.Add(M("Status", ex.IsActive ? $"ACTIVE on Thread {ex.ThreadId} (OS: {ex.OSThreadId})" : "Inactive", indent: 1));

                if (ex.CurrentThreadFrames is { Count: > 0 })
                {
                    blocks.Add(H("Current Thread Frames:", 1));
                    for (int f = 0; f < ex.CurrentThreadFrames.Count; f++)
                        blocks.Add(new PathBlock("Frame", ex.CurrentThreadFrames[f], 2));
                }

                if (ex.OriginalStackTrace is { Count: > 0 })
                {
                    blocks.Add(H("Original Stack Trace (where thrown):", 1));
                    for (int f = 0; f < ex.OriginalStackTrace.Count; f++)
                        blocks.Add(new PathBlock("Frame", ex.OriginalStackTrace[f], 2));
                }

                blocks.Add(CollapseEnd());
            }
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
