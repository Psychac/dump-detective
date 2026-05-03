using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
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
        if (d.InferredTraceCount > 0)
            blocks.Add(M("Inferred Original Traces", $"{d.InferredTraceCount:N0} (from heuristic inference)"));

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
        var instances = d.TopExceptionInstances ?? [];

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
                    blocks.Add(new StackFrameBlock(c.TopFrames[f], 2, CrashAnalyzer.IsFrameworkFrame(c.TopFrames[f])));
                if (c.OriginalStackTrace is { Count: > 0 })
                {
                    string confidenceLabel = c.OriginalStackTraceConfidence switch
                    {
                        InferenceConfidence.Exact        => "",
                        InferenceConfidence.ThreadId     => " [confidence: ThreadId]",
                        InferenceConfidence.MessageHResult => " [confidence: Message+HResult]",
                        InferenceConfidence.TypeInnerType  => " [confidence: Type+InnerType — low]",
                        _                                => "",
                    };
                    if (c.OriginalStackTraceInferred)
                    {
                        if (!string.IsNullOrWhiteSpace(c.OriginalStackTraceInferredFrom))
                            blocks.Add(M($"Original Stack Trace (inferred from {c.OriginalStackTraceInferredFrom}){confidenceLabel}", "", indent: 1));
                        else
                            blocks.Add(M($"Original Stack Trace (inferred from another instance){confidenceLabel}", "", indent: 1));

                        // Link to the detailed exception entry when we can match the address
                        if (!string.IsNullOrWhiteSpace(c.OriginalStackTraceInferredFrom))
                        {
                            try
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(c.OriginalStackTraceInferredFrom, @"0x([0-9A-Fa-f]+)");
                                if (m.Success && ulong.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var addr))
                                {
                                    for (int i = 0; i < instances.Count; i++)
                                    {
                                        var inst = instances[i];
                                        if (inst.Address == addr)
                                        {
                                            blocks.Add(new PathBlock("See detailed exception", $"[{i + 1}] {inst.Type} @ 0x{inst.Address:X}", 2));
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { /* ignore parse/link errors */ }
                        }
                    }
                    else
                    {
                        blocks.Add(H("Original Stack Trace (where thrown):", 1));
                    }

                    for (int f = 0; f < c.OriginalStackTrace.Count; f++)
                        blocks.Add(new StackFrameBlock(c.OriginalStackTrace[f], 2, CrashAnalyzer.IsFrameworkFrame(c.OriginalStackTrace[f])));
                }
                blocks.Add(CollapseEnd());
            }
        }

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
                        blocks.Add(new StackFrameBlock(ex.CurrentThreadFrames[f], 2, CrashAnalyzer.IsFrameworkFrame(ex.CurrentThreadFrames[f])));
                }

                if (ex.OriginalStackTrace is { Count: > 0 })
                {
                    blocks.Add(H("Original Stack Trace (where thrown):", 1));
                    for (int f = 0; f < ex.OriginalStackTrace.Count; f++)
                        blocks.Add(new StackFrameBlock(ex.OriginalStackTrace[f], 2, CrashAnalyzer.IsFrameworkFrame(ex.OriginalStackTrace[f])));
                }

                blocks.Add(CollapseEnd());
            }
        }

        // ── Inference provenance index ────────────────────────────────────────
        var inferred = (d.TopCrashThreadCandidates ?? [])
            .Where(c => c.OriginalStackTraceInferred && !string.IsNullOrWhiteSpace(c.OriginalStackTraceInferredFrom))
            .ToList();
        if (inferred.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("INFERENCE PROVENANCE INDEX"));
            blocks.Add(Divider());
            blocks.Add(T("The following crash-thread original traces were inferred (not directly present on the thread):"));
            blocks.Add(Blank());
            foreach (var ic in inferred)
            {
                string confLabel = ic.OriginalStackTraceConfidence switch
                {
                    InferenceConfidence.ThreadId       => "ThreadId match",
                    InferenceConfidence.MessageHResult => "Message+HResult match",
                    InferenceConfidence.TypeInnerType  => "Type+InnerType match (low confidence)",
                    _                                  => "unknown",
                };
                blocks.Add(new ListItemBlock($"Thread {ic.ThreadId} → {ic.OriginalStackTraceInferredFrom} [{confLabel}]", 1));
            }
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
