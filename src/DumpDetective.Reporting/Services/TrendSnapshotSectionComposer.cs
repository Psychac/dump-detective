using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

internal static class TrendSnapshotSectionComposer
{
    public static AnalyzerDetailSection Build(
        string dumpPath,
        DateTime generatedAtUtc,
        IReadOnlyList<FindingRecord> findings,
        DumpDetective.Core.Models.AnalysisIncidentContext? incidentContext,
        IReadOnlyList<AnalyzerDetailSection> sections,
        int dumpIndex,
        int totalDumps,
        AnalysisSnapshot? snapshot = null,
        AnalysisSnapshot? baseline = null)
    {
        string title = $"Dump {dumpIndex + 1} of {totalDumps}: {Path.GetFileName(dumpPath)}";
        var blocks = new List<SectionBlock>();

        blocks.Add(new HeadingBlock("DUMP SUMMARY"));
        blocks.Add(new DividerBlock());
        blocks.Add(new PathBlock("Path", dumpPath));
        blocks.Add(new MetricBlock("Generated (UTC)", generatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")));
        blocks.Add(new MetricBlock("Findings", findings.Count.ToString()));

        if (incidentContext is { } ctx)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("INCIDENT CONTEXT"));
            blocks.Add(new DividerBlock());
            blocks.Add(new MetricBlock("Mode", ctx.Mode));
            blocks.Add(new MetricBlock("Report", $"{ctx.ReportFormat}"));
            blocks.Add(new MetricBlock("Runtime", $"{ctx.RuntimeFlavor ?? "n/a"}{(string.IsNullOrWhiteSpace(ctx.RuntimeVersion) ? string.Empty : " " + ctx.RuntimeVersion)}"));
            blocks.Add(new MetricBlock("GC Mode", ctx.GcMode ?? "n/a"));
            blocks.Add(new MetricBlock("Heap Count", ctx.HeapCount.HasValue ? ctx.HeapCount.Value.ToString() : "n/a"));
            blocks.Add(new MetricBlock("Heap Walkable", ctx.HeapCanWalk ? "yes" : "no"));
            blocks.Add(new MetricBlock("Config", (ctx.UsedConfigFile ? "config file" : "command line") + (string.IsNullOrWhiteSpace(ctx.ConfigPath) ? string.Empty : $" ({ctx.ConfigPath})")));
            blocks.Add(new MetricBlock("Index Prebuild", ctx.IndexPrebuildMode));
            blocks.Add(new MetricBlock("Active Analyzers", ctx.ActiveAnalyzerCount.ToString()));
            blocks.Add(new MetricBlock("Elapsed", $"{ctx.AnalysisElapsedSeconds:F1}s"));
        }

        if (findings.Count > 0)
        {
            // Key metrics for this snapshot (with Δ vs baseline)
            if (snapshot != null)
                AddKeyMetricsBlock(blocks, snapshot, baseline);

            blocks.Add(new BlankBlock());
            blocks.Add(new HeadingBlock("FINDINGS"));
            blocks.Add(new DividerBlock());
            foreach (FindingRecord finding in findings)
            {
                blocks.Add(new HeadingBlock($"[{finding.Severity}] {finding.Title}", 1));
                blocks.Add(new TextBlock(finding.GetSummaryText(), 2));
            }
        }
        else if (snapshot != null)
        {
            AddKeyMetricsBlock(blocks, snapshot, baseline);
        }

        foreach (AnalyzerDetailSection section in sections)
        {
            blocks.Add(new BlankBlock());
            blocks.Add(new CollapsibleSectionBeginBlock(section.DisplayTitle));
            AddEmbeddedAnalyzerSectionBlocks(blocks, section);
            blocks.Add(new CollapsibleSectionEndBlock());
        }

        return new AnalyzerDetailSection(
            AnalyzerName:  title,
            DisplayTitle:  title,
            SortOrder:     dumpIndex * 10 + 200,
            Blocks:        blocks,
            SectionId:     $"detail-{dumpIndex}",
            Domain:        "SnapshotDetail");
    }

    private static void AddEmbeddedAnalyzerSectionBlocks(List<SectionBlock> blocks, AnalyzerDetailSection section)
    {
        if (section.LeadFinding is { } lead)
        {
            blocks.Add(new HeadingBlock($"Lead Finding [{lead.Severity}]", 1));
            if (!string.IsNullOrWhiteSpace(lead.Title))
                blocks.Add(new TextBlock(lead.Title, 2));
            if (!string.IsNullOrWhiteSpace(lead.Summary))
                blocks.Add(new TextBlock(lead.Summary, 2));
            if (!string.IsNullOrWhiteSpace(lead.Recommendation))
                blocks.Add(new TextBlock($"Recommendation: {lead.Recommendation}", 2));
        }

        if (section.KeyMetrics is { Count: > 0 })
        {
            blocks.Add(new HeadingBlock("Analyzer Key Metrics", 1));
            foreach (var kv in section.KeyMetrics)
            {
                // kv.Key is the snake_case metric key; use the human label from the value where available
                var label = kv.Value switch
                {
                    NumericMetricValue n => (kv.Key ?? string.Empty),
                    TextMetricValue t => (kv.Key ?? string.Empty),
                    EnumMetricValue e => (kv.Key ?? string.Empty),
                    _ => kv.Key
                };
                blocks.Add(ToMetricBlock(kv.Key, kv.Value, 2));
            }
        }

        if (section.CompactTables is { Count: > 0 })
        {
            foreach (CompactTable table in section.CompactTables)
            {
                blocks.Add(new TableBlock(
                    Caption: table.Title,
                    Headers: table.Headers.Select(header => header.Name).ToArray(),
                    Rows: table.Rows.Select(row => new TableRow(row.Values.Select(value => new TableCell(value?.ToString() ?? string.Empty)).ToArray())).ToArray()));
            }
        }

        foreach (SectionBlock block in section.Blocks)
            blocks.Add(block);

        if (section.Provenance is { } provenance)
        {
            blocks.Add(new HeadingBlock("Provenance", 1));
            blocks.Add(new MetricBlock("Analyzer", provenance.Analyzer, null, 2));
            blocks.Add(new MetricBlock("Status", provenance.Status, null, 2));
            blocks.Add(new MetricBlock("Duration", $"{provenance.DurationMs:F0} ms", provenance.DurationMs, 2));
            blocks.Add(new MetricBlock("Objects Scanned", provenance.ObjectScanCount.ToString("N0"), provenance.ObjectScanCount, 2));
            blocks.Add(new MetricBlock("Cache Hits", provenance.CacheHits.ToString("N0"), provenance.CacheHits, 2));
            blocks.Add(new MetricBlock("Cache Misses", provenance.CacheMisses.ToString("N0"), provenance.CacheMisses, 2));
        }
    }

    // ── Key Metrics Helpers ───────────────────────────────────────────────────

    private static void AddKeyMetricsBlock(
        List<SectionBlock> blocks,
        AnalysisSnapshot snapshot,
        AnalysisSnapshot? baseline)
    {
        blocks.Add(new BlankBlock());
        blocks.Add(new HeadingBlock("Key Metrics"));

        // Extract values from domain results
        ulong? totalBytes = null;
        double? gen2Pct = null;
        int? leakCandidates = null;
        int? blockedThreads = null;
        int? finalizerQueue = null;
        int? activeExceptions = null;

        if (snapshot.DomainResults.TryGetValue("Memory Analysis", out AnalyzerDomainResult? memRaw) && memRaw is MemoryDomainResult mem)
            totalBytes = mem.TotalBytes;
        else if (snapshot.DomainResults.TryGetValue("MemoryAnalyzer", out AnalyzerDomainResult? memRaw2) && memRaw2 is MemoryDomainResult mem2)
            totalBytes = mem2.TotalBytes;

        if (snapshot.DomainResults.TryGetValue("GC Generation Analysis", out AnalyzerDomainResult? gcRaw) && gcRaw is GCGenerationDomainResult gc)
            gen2Pct = gc.Gen2Pct;
        else if (snapshot.DomainResults.TryGetValue("GCGenerationAnalyzer", out AnalyzerDomainResult? gcRaw2) && gcRaw2 is GCGenerationDomainResult gc2)
            gen2Pct = gc2.Gen2Pct;

        if (snapshot.DomainResults.TryGetValue("Leak Candidate Analysis", out AnalyzerDomainResult? leakRaw) && leakRaw is LeakCandidateDomainResult leak)
            leakCandidates = leak.TotalCandidates;
        else if (snapshot.DomainResults.TryGetValue("LeakCandidateAnalyzer", out AnalyzerDomainResult? leakRaw2) && leakRaw2 is LeakCandidateDomainResult leak2)
            leakCandidates = leak2.TotalCandidates;

        if (snapshot.DomainResults.TryGetValue("Thread Analysis", out AnalyzerDomainResult? threadRaw) && threadRaw is ThreadDomainResult thread)
        {
            blockedThreads    = thread.BlockedThreadCount;
            activeExceptions  = thread.ThreadsWithActiveExceptionsCount;
        }
        else if (snapshot.DomainResults.TryGetValue("ThreadAnalyzer", out AnalyzerDomainResult? threadRaw2) && threadRaw2 is ThreadDomainResult thread2)
        {
            blockedThreads   = thread2.BlockedThreadCount;
            activeExceptions = thread2.ThreadsWithActiveExceptionsCount;
        }

        if (snapshot.DomainResults.TryGetValue("Finalizable Object Analysis", out AnalyzerDomainResult? finRaw) && finRaw is FinalizableObjectDomainResult fin)
            finalizerQueue = fin.FinalizerQueueCount;
        else if (snapshot.DomainResults.TryGetValue("FinalizableObjectAnalyzer", out AnalyzerDomainResult? finRaw2) && finRaw2 is FinalizableObjectDomainResult fin2)
            finalizerQueue = fin2.FinalizerQueueCount;

        // Baseline values for Δ computation
        ulong? bTotalBytes = null;
        double? bGen2Pct = null;
        int? bLeakCandidates = null;
        int? bBlockedThreads = null;
        int? bFinalizerQueue = null;
        int? bActiveExceptions = null;

        if (baseline != null)
        {
            if (baseline.DomainResults.TryGetValue("Memory Analysis", out AnalyzerDomainResult? bMemRaw) && bMemRaw is MemoryDomainResult bMem)
                bTotalBytes = bMem.TotalBytes;
            else if (baseline.DomainResults.TryGetValue("MemoryAnalyzer", out AnalyzerDomainResult? bMemRaw2) && bMemRaw2 is MemoryDomainResult bMem2)
                bTotalBytes = bMem2.TotalBytes;

            if (baseline.DomainResults.TryGetValue("GC Generation Analysis", out AnalyzerDomainResult? bGcRaw) && bGcRaw is GCGenerationDomainResult bGc)
                bGen2Pct = bGc.Gen2Pct;
            else if (baseline.DomainResults.TryGetValue("GCGenerationAnalyzer", out AnalyzerDomainResult? bGcRaw2) && bGcRaw2 is GCGenerationDomainResult bGc2)
                bGen2Pct = bGc2.Gen2Pct;

            if (baseline.DomainResults.TryGetValue("Leak Candidate Analysis", out AnalyzerDomainResult? bLeakRaw) && bLeakRaw is LeakCandidateDomainResult bLeak)
                bLeakCandidates = bLeak.TotalCandidates;
            else if (baseline.DomainResults.TryGetValue("LeakCandidateAnalyzer", out AnalyzerDomainResult? bLeakRaw2) && bLeakRaw2 is LeakCandidateDomainResult bLeak2)
                bLeakCandidates = bLeak2.TotalCandidates;

            if (baseline.DomainResults.TryGetValue("Thread Analysis", out AnalyzerDomainResult? bThreadRaw) && bThreadRaw is ThreadDomainResult bThread)
            {
                bBlockedThreads   = bThread.BlockedThreadCount;
                bActiveExceptions = bThread.ThreadsWithActiveExceptionsCount;
            }
            else if (baseline.DomainResults.TryGetValue("ThreadAnalyzer", out AnalyzerDomainResult? bThreadRaw2) && bThreadRaw2 is ThreadDomainResult bThread2)
            {
                bBlockedThreads   = bThread2.BlockedThreadCount;
                bActiveExceptions = bThread2.ThreadsWithActiveExceptionsCount;
            }

            if (baseline.DomainResults.TryGetValue("Finalizable Object Analysis", out AnalyzerDomainResult? bFinRaw) && bFinRaw is FinalizableObjectDomainResult bFin)
                bFinalizerQueue = bFin.FinalizerQueueCount;
            else if (baseline.DomainResults.TryGetValue("FinalizableObjectAnalyzer", out AnalyzerDomainResult? bFinRaw2) && bFinRaw2 is FinalizableObjectDomainResult bFin2)
                bFinalizerQueue = bFin2.FinalizerQueueCount;
        }

        bool isBaseline = baseline == null;

        var rows = new List<TableRow>();
        if (totalBytes.HasValue)
            rows.Add(MetricRow("Total Bytes", FormatHelper.FormatBytes(totalBytes.Value),
                isBaseline ? null : DeltaBytes(totalBytes, bTotalBytes)));
        if (gen2Pct.HasValue)
            rows.Add(MetricRow("Gen2 %", $"{gen2Pct.Value:F1}%",
                isBaseline ? null : DeltaPct(gen2Pct, bGen2Pct)));
        if (leakCandidates.HasValue)
            rows.Add(MetricRow("Leak Candidates", leakCandidates.Value.ToString(),
                isBaseline ? null : DeltaInt(leakCandidates, bLeakCandidates)));
        if (blockedThreads.HasValue)
            rows.Add(MetricRow("Blocked Threads", blockedThreads.Value.ToString(),
                isBaseline ? null : DeltaInt(blockedThreads, bBlockedThreads)));
        if (activeExceptions.HasValue)
            rows.Add(MetricRow("Active Exceptions", activeExceptions.Value.ToString(),
                isBaseline ? null : DeltaInt(activeExceptions, bActiveExceptions)));
        if (finalizerQueue.HasValue)
            rows.Add(MetricRow("Finalizer Queue", finalizerQueue.Value.ToString(),
                isBaseline ? null : DeltaInt(finalizerQueue, bFinalizerQueue)));

        if (rows.Count > 0)
        {
            blocks.Add(new TableBlock(
                Caption: "Snapshot Key Metrics",
                Headers: ["Metric", "Value", "Δ vs Baseline"],
                Rows: rows));
        }
    }

    private static MetricBlock ToMetricBlock(string snakeKey, MetricValue value, int indent = 0)
    {
        // Humanize snake_case key into a display label
        static string Humanize(string k)
        {
            if (string.IsNullOrWhiteSpace(k)) return string.Empty;
            var parts = k.Split('_', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + (parts[i].Length > 1 ? parts[i].Substring(1) : string.Empty);
            return string.Join(' ', parts);
        }

        var label = Humanize(snakeKey ?? string.Empty);
        return value switch
        {
            NumericMetricValue number => new MetricBlock(label, FormatNumericValue(number), number.Value, indent),
            TextMetricValue text => new MetricBlock(label, text.Value, null, indent),
            EnumMetricValue enumValue => new MetricBlock(label, enumValue.Value, null, indent),
            _ => new MetricBlock(label, string.Empty, null, indent)
        };
    }

    private static string FormatNumericValue(NumericMetricValue value)
    {
        return value.Unit switch
        {
            MetricUnit.Bytes => FormatHelper.FormatBytes((ulong)Math.Max(0, value.Value)),
            MetricUnit.Percent => $"{value.Value:F1}%",
            MetricUnit.Ratio => $"{value.Value:F2}x",
            MetricUnit.Milliseconds => FormatMilliseconds(value.Value),
            MetricUnit.Custom => !string.IsNullOrWhiteSpace(value.Formatted)
                ? value.Formatted
                : value.Value % 1 == 0 ? value.Value.ToString("N0") : value.Value.ToString("N2"),
            _ => value.Value % 1 == 0 ? value.Value.ToString("N0") : value.Value.ToString("N2")
        };
    }

    private static string FormatMilliseconds(double value)
    {
        double abs = Math.Abs(value);
        if (abs < 1000)
            return $"{value:F0} ms";
        if (abs < 60_000)
            return $"{value / 1000.0:F2} s";
        if (abs < 3_600_000)
            return $"{value / 60_000.0:F2} min";
        return $"{value / 3_600_000.0:F2} h";
    }

    private static TableRow MetricRow(string metric, string value, string? delta) =>
        new([new TableCell(metric), new TableCell(value), new TableCell(delta ?? "—")]);

    private static string DeltaBytes(ulong? current, ulong? baseline)
    {
        if (!current.HasValue || !baseline.HasValue) return "—";
        long delta = (long)current.Value - (long)baseline.Value;
        return delta == 0 ? "=" : $"{(delta > 0 ? "+" : string.Empty)}{FormatHelper.FormatDeltaValue(delta, "bytes")}";
    }

    private static string DeltaPct(double? current, double? baseline)
    {
        if (!current.HasValue || !baseline.HasValue) return "—";
        double delta = current.Value - baseline.Value;
        return delta == 0 ? "=" : $"{delta:+0.0;-0.0}pp";
    }

    private static string DeltaInt(int? current, int? baseline)
    {
        if (!current.HasValue || !baseline.HasValue) return "—";
        int delta = current.Value - baseline.Value;
        return delta == 0 ? "=" : $"{delta:+0;-0}";
    }
}