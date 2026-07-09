using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDetective.Cli.Console;

internal static class ConsoleUx
{
    private static readonly Lock _consoleGate = new();
    // Live display for scan progress (replaces manual spinner / \r updates)
    private static CancellationTokenSource? _scanLiveCts;
    private static Task? _scanLiveTask;
    private static string _scanLiveMarkup = string.Empty;

    // Visual hierarchy (group headers use full-width Rule — no left indent):
    //   Level 1 – Pipeline stage  (IndentStage    = 2 spaces)
    //   Level 2 – Analyzer group  full-width Rule, no constant needed
    //   Level 3 – Analyzer        (IndentAnalyzer = 4 spaces)
    //   Level 4 – Phase/progress  (IndentSub      = 6 spaces)
    private const string IndentStage = "  ";
    private const string IndentAnalyzer = "    ";
    private const string IndentSub = "      ";

    // ── Level 0: global headers ─────────────────────────────────────────────

    public static void Header(string title)
    {
        SafeWriteLine();
        SafeWriteRule(new Rule($"[bold deepskyblue1]{Escape(title)}[/]").LeftJustified());
        SafeWriteLine();
    }

    // ── Pre-analysis preamble ────────────────────────────────────────────────

    /// <summary>Prints the dump filename and optional file size immediately after the header Rule.</summary>
    public static void DumpInfo(string dumpName, long? fileSizeBytes)
    {
        string sizeStr = fileSizeBytes.HasValue
            ? $"  [grey]·[/]  [grey]{FormatBytes((ulong)fileSizeBytes.Value)}[/]"
            : string.Empty;
        SafeMarkupLine($"  [bold white]{Escape(dumpName)}[/]{sizeStr}");
        SafeWriteLine();
    }

    // ── Level 1: Dump ───────────────────────────────────────────────────────

    public static void DumpStart(int current, int total, string dumpName)
    {
        SafeWriteLine();
        SafeMarkupLine($"{Timestamp()} [mediumpurple2]📦[/]  [bold]Dump {current}/{total}[/]");
        SafeMarkupLine($"           [grey]└─[/]  [silver]{Escape(dumpName)}[/]");
    }

    public static void DumpComplete(int current, int total, string dumpName, TimeSpan duration)
    {
        SafeWriteLine();
        SafeMarkupLine($"{Timestamp()} [green]✔[/]  [bold]Dump {current}/{total} complete[/]  [grey]·[/]  [silver]{Escape(FormatElapsed(duration))}[/]");
        SafeMarkupLine($"           [grey]└─[/]  [silver]{Escape(dumpName)}[/]");
    }

    // ── Level 2: Stage ──────────────────────────────────────────────────────

    public static void StageStart(int current, int total, string stageName)
    {
        SafeWriteLine();
        SafeMarkupLine($"{IndentStage}{Timestamp()} [bold orange1]▶  Stage {current}/{total}[/]  [grey]·[/]  [bold]{Escape(stageName)}[/]");
    }

    public static void StageComplete(int current, int total, string stageName, TimeSpan duration)
    {
        SafeMarkupLine($"{IndentStage}{Timestamp()} [green]✔[/]  [grey]Stage {current}/{total}  ·  {Escape(stageName)}  ·  {Escape(FormatElapsed(duration))}[/]");
    }

    // ── Level 2: Analyzer group ─────────────────────────────────────────────

    public static void AnalyzerGroupStart(int current, int total, string groupName)
    {
        SafeWriteLine();
        var openRule = new Rule($"[bold cyan1]{Escape(groupName)}[/]  [grey]{current} of {total}[/]").LeftJustified();
        openRule.Style = new Style(Color.Grey);
        SafeWriteRule(openRule);
    }

    public static void AnalyzerGroupComplete(int current, int total, string groupName, TimeSpan duration)
    {
        var closeRule = new Rule($"[grey]{Escape(FormatElapsed(duration))}[/]").RightJustified();
        closeRule.Style = new Style(foreground: Color.Grey, decoration: Decoration.Dim);
        SafeWriteRule(closeRule);
    }

    // ── Level 3: Analyzer ───────────────────────────────────────────────────

    public static void AnalyzerStart(int current, int total, string analyzerName)
    {
        SafeMarkupLine($"{IndentAnalyzer}[deepskyblue1]◆[/]  [grey][[{current}/{total}]][/]  [bold]{Escape(analyzerName)}[/]");
    }

    // ── Level 4: Phase / sub-module / progress ──────────────────────────────

    public static void AnalyzerPhase(string phase)
    {
        SafeMarkupLine($"{IndentSub}[grey]↳  {Escape(phase)}[/]");
    }

    /// <summary>
    /// Overwrites the current terminal line with a live scan progress indicator.
    /// <paramref name="operation"/> is used only in the completion summary; the rolling
    /// line intentionally omits it to reduce clutter (the analyzer name was just printed).
    /// </summary>
    public static void ObjectScanProgress(string operation, long scannedCount, TimeSpan elapsed, string? details = null, double? perSecondOverride = null)
    {
        double perSecond = perSecondOverride ?? (elapsed.TotalSeconds > 0 ? scannedCount / elapsed.TotalSeconds : 0);
        string scanPart = scannedCount > 0 ? $"  {scannedCount:N0} obj  ·  " : "  ";
        string ratePart = perSecond > 0 ? $"  ·  {perSecond:N0}/s" : string.Empty;
        string detailPart = string.IsNullOrWhiteSpace(details) ? string.Empty : $"  ·  {Escape(details)}";
        // Keep the live markup without leading indent; the status loop will prepend IndentSub
        string text = $"{FormatElapsed(elapsed)}{scanPart}{ratePart}{detailPart}";

        lock (_consoleGate)
        {
            _scanLiveMarkup = text;
            StartScanLiveIfNeeded();
        }
    }

    /// <summary>
    /// Replaces the live progress line with a styled one-line completion summary and advances to the next line.
    /// </summary>
    public static void ObjectScanComplete(string operation, long scannedCount, TimeSpan elapsed, string? details = null)
    {
        double perSecond = elapsed.TotalSeconds > 0 ? scannedCount / elapsed.TotalSeconds : 0;
        string scanPart = scannedCount > 0 ? $"  [grey]·[/]  [silver]{scannedCount:N0} obj  ·  {perSecond:N0}/s[/]" : string.Empty;
        string detailsPart = string.IsNullOrWhiteSpace(details) ? string.Empty : $"  [grey]·[/]  [grey]{Escape(details)}[/]";
        lock (_consoleGate)
        {
            StopScanLiveIfActive();
            AnsiConsole.MarkupLine(
                $"{IndentAnalyzer}[green]✔[/]  [bold]{Escape(operation)}[/]{scanPart}  [grey]·[/]  [silver]{Escape(FormatElapsed(elapsed))}[/]{detailsPart}");
        }
    }

    // ── Insights ─────────────────────────────────────────────────────────────

    public static void InsightsHeader(int count)
    {
        SafeWriteLine();
        var r = new Rule($"[bold yellow]Insights[/]  [grey]·  {count} finding{(count == 1 ? "" : "s")}[/]").LeftJustified();
        r.Style = new Style(Color.Yellow);
        SafeWriteRule(r);
    }

    public static void InsightLine(FindingSeverity severity, string title, string? evidence, bool showEvidence)
    {
        (string icon, string color) = severity switch
        {
            FindingSeverity.Critical => ("✖", "bold red"),
            FindingSeverity.Warning => ("▲", "yellow"),
            _ => ("ℹ", "silver"),
        };
        SafeMarkupLine($"  [{color}]{icon}  {Escape(title)}[/]");
        if (showEvidence && !string.IsNullOrWhiteSpace(evidence))
            SafeMarkupLine($"    [grey]└─  {Escape(evidence)}[/]");
    }

    // ── Post-analysis summary ────────────────────────────────────────────

    public static void RunStatusSummary(int success, int failed, int skippedByFilter, int skippedByCancellation, int findings)
    {
        string s = success > 0 ? $"[green]{success} ok[/]" : $"[grey]{success} ok[/]";
        string f = failed > 0 ? $"[red]{failed} failed[/]" : $"[grey]{failed} failed[/]";
        string k1 = skippedByFilter > 0 ? $"[yellow]{skippedByFilter} skipped filter[/]" : $"[grey]{skippedByFilter} skipped filter[/]";
        string k2 = skippedByCancellation > 0 ? $"[yellow]{skippedByCancellation} skipped cancelled[/]" : $"[grey]{skippedByCancellation} skipped cancelled[/]";
        SafeMarkupLine($"  {s}  [grey]·[/]  {f}  [grey]·[/]  {k1}  [grey]·[/]  {k2}  [grey]·[/]  [silver]{findings} finding{(findings == 1 ? "" : "s")}[/]");
    }

    public static void TopSlowAnalyzers(IReadOnlyList<AnalyzerRunResult> items)
    {
        if (items.Count == 0)
            return;
        SafeMarkupLine($"  [grey]slowest analyzers:[/]");
        foreach (AnalyzerRunResult r in items)
        {
            string scans = r.ObjectScanCount > 0 ? $"  ·  {r.ObjectScanCount:N0} obj" : string.Empty;
            string name = Escape(r.AnalyzerName).PadRight(40);
            SafeMarkupLine($"    [silver]{name}[/]  [grey]{r.Duration.TotalSeconds:F1}s{Escape(scans)}  ·  {r.FindingCount} finding{(r.FindingCount == 1 ? "" : "s")}[/]");
        }
    }

    public static void ReportWritten(string path)
    {
        string fileName = Path.GetFileName(path);
        SafeMarkupLine($"  [green]✔[/]  [grey]Report →[/]  [bold white]{Escape(fileName)}[/]");
        SafeMarkupLine($"         [grey]└─[/]  [silver]{Escape(path)}[/]");
    }

    public static void Footer(TimeSpan elapsed)
    {
        SafeWriteLine();
        var r = new Rule($"[grey]{Escape(FormatElapsed(elapsed))}[/]").RightJustified();
        r.Style = new Style(Color.Grey);
        SafeWriteRule(r);
    }

    // ── Utility messages ────────────────────────────────────────────────────

    public static void AnalyzerRunSummary(long scans, long cacheHits, long cacheMisses)
    {
        long total = cacheHits + cacheMisses;
        double hitRate = total > 0 ? cacheHits * 100.0 / total : 0;
        SafeMarkupLine($"{Timestamp()} [grey]ℹ[/]  [grey]Scanned [/][silver]{scans:N0}[/][grey] obj  ·  cache [/][silver]{hitRate:F0}%[/][grey] hit  ({cacheHits:N0}/{cacheMisses:N0})[/]");
    }

    public static void Info(string message)
    {
        SafeMarkupLine($"{Timestamp()} [deepskyblue1]ℹ[/]  {Escape(message)}");
    }

    public static void Warning(string message)
    {
        SafeMarkupLine($"{Timestamp()} [yellow]⚠[/]  {Escape(message)}");
    }

    public static void Error(string message)
    {
        SafeMarkupLine($"{Timestamp()} [red]✖[/]  {Escape(message)}");
    }

    public static void Success(string message)
    {
        SafeMarkupLine($"{Timestamp()} [green]✔[/]  {Escape(message)}");
    }

    public static void WriteVerbose(string message)
    {
        SafeMarkupLine($"{Timestamp()} [grey][DIAG][/]  {Escape(message)}");
    }

    /// <summary>Dim developer-facing notice — less prominent than <see cref="Warning"/>.</summary>
    public static void Note(string message)
    {
        SafeMarkupLine($"[grey]  ⋯  {Escape(message)}[/]");
    }

    // ── Memory diagnostics ──────────────────────────────────────────────────

    /// <summary>
    /// Prints the header row for the per-analyzer memory table.
    /// </summary>
    public static void MemoryTableHeader()
    {
        SafeWriteLine();
        SafeMarkupLine($"{Timestamp()} [deepskyblue1]🧠  Memory usage per analyzer[/]");
        SafePrintMemoryColumnHeader();
    }

    /// <summary>
    /// Prints the header row for the per-stage memory table.
    /// </summary>
    public static void MemoryStageTableHeader()
    {
        SafeWriteLine();
        SafeMarkupLine($"{Timestamp()} [deepskyblue1]🧠  Memory usage per pipeline stage[/]");
        SafePrintMemoryColumnHeader();
    }

    private static void SafePrintMemoryColumnHeader()
    {
        lock (_consoleGate)
        {
            StopScanLiveIfActive();
            PrintMemoryColumnHeader_NoLock();
        }
    }

    private static void PrintMemoryColumnHeader_NoLock()
    {
        AnsiConsole.MarkupLine($"{IndentAnalyzer}[grey]{"Name",-42}  {"WS Δ",10}  {"WS After",10}  {"Managed Δ",10}[/]");
        AnsiConsole.MarkupLine($"{IndentAnalyzer}[grey]{new string('─', 42)}  {new string('─', 10)}  {new string('─', 10)}  {new string('─', 10)}[/]");
    }

    /// <summary>
    /// Prints one row for an analyzer in the memory table.
    /// <paramref name="wsDelta"/> and <paramref name="managedDelta"/> are raw byte values (signed).
    /// </summary>
    public static void MemoryTableRow(string analyzerName, long wsDelta, long wsAfter, long managedDelta)
    {
        string wsDeltaStr = FormatSignedBytes(wsDelta);
        string wsAfterStr = FormatBytes((ulong)Math.Max(0, wsAfter));
        string managedStr = FormatSignedBytes(managedDelta);
        string deltaColor = wsDelta > 50 * 1024 * 1024 ? "yellow" : wsDelta > 200 * 1024 * 1024 ? "red" : "grey";

        Safe(() => AnsiConsole.MarkupLine(
            $"{IndentAnalyzer}[white]{Escape(analyzerName),-42}[/]" +
            $"  [{deltaColor}]{Escape(wsDeltaStr),10}[/]" +
            $"  [grey]{Escape(wsAfterStr),10}[/]" +
            $"  [grey]{Escape(managedStr),10}[/]"));
    }

    /// <summary>
    /// Prints the process-level peak working set observed across the analysis run.
    /// </summary>
    public static void MemoryTableFooter(long peakWorkingSet, long baselineWorkingSet)
    {
        long totalDelta = peakWorkingSet - baselineWorkingSet;
        string peakStr = FormatBytes((ulong)Math.Max(0, peakWorkingSet));
        string deltaStr = FormatSignedBytes(totalDelta);

        Safe(() =>
        {
            AnsiConsole.MarkupLine($"{IndentAnalyzer}[grey]{new string('─', 78)}[/]");
            AnsiConsole.MarkupLine(
                $"{IndentAnalyzer}[grey]Peak process working set:[/]  [bold white]{Escape(peakStr)}[/]" +
                $"  [grey](Δ from baseline: {Escape(deltaStr)})[/]");
        });
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private static string Timestamp() => $"[grey][[{DateTime.Now:HH:mm:ss}]][/]";

    private static string Escape(string value) => Markup.Escape(value);

    // Acquire the console lock, ensure any live scan line is flushed, then run the action.
    private static void Safe(Action action)
    {
        lock (_consoleGate)
        {
            StopScanLiveIfActive();
            action();
        }
    }

    // Performance helpers: avoid allocating lambdas for common single-call patterns.
    private static void SafeMarkupLine(string text)
    {
        lock (_consoleGate)
        {
            StopScanLiveIfActive();
            AnsiConsole.MarkupLine(text);
        }
    }

    private static void SafeWriteRule(Rule r)
    {
        lock (_consoleGate)
        {
            StopScanLiveIfActive();
            AnsiConsole.Write(r);
        }
    }

    private static void SafeWriteLine()
    {
        lock (_consoleGate)
        {
            StopScanLiveIfActive();
            AnsiConsole.WriteLine();
        }
    }

    private static void StartScanLiveIfNeeded()
    {
        if (_scanLiveTask != null && !_scanLiveTask.IsCompleted)
            return;

        _scanLiveCts = new CancellationTokenSource();
        var cts = _scanLiveCts;
        _scanLiveTask = Task.Run(() =>
        {
            try
            {
                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(new Style(Color.Grey))
                    .Start(IndentSub + (_scanLiveMarkup ?? "scanning..."), ctx =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            string snapshot;
                            lock (_consoleGate)
                                snapshot = _scanLiveMarkup;

                            if (string.IsNullOrEmpty(snapshot))
                                ctx.Status = IndentSub + "scanning...";
                            else
                                ctx.Status = IndentSub + snapshot;

                            Thread.Sleep(100);
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                // expected on cancel
            }
            catch
            {
                // swallow other console exceptions to avoid crashing CLI
            }
        }, cts.Token);
    }

    private static void StopScanLiveIfActive()
    {
        if (_scanLiveCts == null)
            return;

        try
        {
            _scanLiveCts.Cancel();
            _scanLiveTask?.Wait(250);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _scanLiveTask = null;
            _scanLiveCts.Dispose();
            _scanLiveCts = null;
            _scanLiveMarkup = string.Empty;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds >= 1)
        {
            return $"{elapsed.TotalSeconds:F1}s";
        }

        return $"{elapsed.TotalMilliseconds:F0}ms";
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024UL * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024UL) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string FormatSignedBytes(long bytes)
    {
        string sign = bytes >= 0 ? "+" : "-";
        return $"{sign}{FormatBytes((ulong)Math.Abs(bytes))}";
    }
}
