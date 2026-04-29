using Spectre.Console;

namespace DumpDetective.Cli.Console;

internal static class ConsoleUx
{
    private static readonly Lock _consoleGate = new();
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static bool _scanLineActive;
    private static int _lastScanLineLength;
    private static int _spinnerIndex;

    // Visual hierarchy indentation:
    //   Level 1 – Dump      (no indent)
    //   Level 2 – Stage     (IndentStage)
    //   Level 3 – Analyzer  (IndentAnalyzer)
    //   Level 4 – Phase / progress  (IndentSub)
    private const string IndentStage    = "  ";
    private const string IndentAnalyzer = "    ";
    private const string IndentSub      = "      ";

    // ── Level 0: global headers ─────────────────────────────────────────────

    public static void Header(string title)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{Escape(title)}[/]").LeftJustified());
        }
    }

    // ── Level 1: Dump ───────────────────────────────────────────────────────

    public static void DumpStart(int current, int total, string dumpName)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Timestamp()} [mediumpurple2]📦[/]  [bold]Dump {current}/{total}[/]");
            AnsiConsole.MarkupLine($"           [grey]└─[/]  [silver]{Escape(dumpName)}[/]");
        }
    }

    public static void DumpComplete(int current, int total, string dumpName, TimeSpan duration)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✔[/]  [bold]Dump {current}/{total} complete[/]  [grey]·[/]  [silver]{Escape(FormatElapsed(duration))}[/]");
            AnsiConsole.MarkupLine($"           [grey]└─[/]  [silver]{Escape(dumpName)}[/]");
        }
    }

    // ── Level 2: Stage ──────────────────────────────────────────────────────

    public static void StageStart(int current, int total, string stageName)
    {
        double percent = total > 0 ? (current - 1) * 100.0 / total : 0;
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{IndentStage}{Timestamp()} [orange1]▸[/]  [bold]Stage {current}/{total}[/]  [grey]·[/]  [deepskyblue1]{Escape(stageName)}[/]  [grey]({percent:F0}%)[/]");
        }
    }

    public static void StageComplete(int current, int total, string stageName, TimeSpan duration)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{IndentStage}{Timestamp()} [green]✔[/]  [bold]Stage {current}/{total} done[/]  [grey]·[/]  [silver]{Escape(stageName)}[/]  [grey]([/][silver]{Escape(FormatElapsed(duration))}[/][grey])[/]");
        }
    }

    // ── Level 3: Analyzer ───────────────────────────────────────────────────

    public static void AnalyzerStart(int current, int total, string analyzerName)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{IndentAnalyzer}{Timestamp()} [deepskyblue1]◆[/]  [grey][[{current}/{total}]][/]  [bold white]{Escape(analyzerName)}[/]");
        }
    }

    // ── Level 4: Phase / sub-module / progress ──────────────────────────────

    public static void AnalyzerPhase(string phase)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{IndentSub}[grey]↳  {Escape(phase)}[/]");
        }
    }

    /// <summary>
    /// Overwrites the current terminal line with a live scan progress indicator.
    /// <paramref name="operation"/> is used only in the completion summary; the rolling
    /// line intentionally omits it to reduce clutter (the analyzer name was just printed).
    /// </summary>
    public static void ObjectScanProgress(string operation, long scannedCount, TimeSpan elapsed, string? details = null, double? perSecondOverride = null)
    {
        double perSecond = perSecondOverride ?? (elapsed.TotalSeconds > 0 ? scannedCount / elapsed.TotalSeconds : 0);
        string spinner    = SpinnerFrames[_spinnerIndex++ % SpinnerFrames.Length];
        string scanPart   = scannedCount > 0 ? $"{scannedCount:N0} obj" : string.Empty;
        string ratePart   = perSecond > 0 ? $"  ·  {perSecond:N0}/s" : string.Empty;
        string detailsPart = string.IsNullOrWhiteSpace(details) ? string.Empty : $"  ·  {details}";
        string sep        = string.IsNullOrEmpty(scanPart) ? string.Empty : "  ·  ";
        string text       = $"{IndentSub}{spinner}  {scanPart}{sep}{FormatElapsed(elapsed)}{ratePart}{detailsPart}";

        lock (_consoleGate)
        {
            _scanLineActive = true;
            int paddedLength = Math.Max(_lastScanLineLength, text.Length);
            _lastScanLineLength = paddedLength;
            AnsiConsole.Write(new Text($"\r{text.PadRight(paddedLength)}"));
        }
    }

    /// <summary>
    /// Replaces the live progress line with a final one-line completion summary and advances to the next line.
    /// </summary>
    public static void ObjectScanComplete(string operation, long scannedCount, TimeSpan elapsed, string? details = null)
    {
        double perSecond   = elapsed.TotalSeconds > 0 ? scannedCount / elapsed.TotalSeconds : 0;
        string scanPart    = scannedCount > 0 ? $"  ·  {scannedCount:N0} obj  ·  {perSecond:N0}/s" : string.Empty;
        string detailsPart = string.IsNullOrWhiteSpace(details) ? string.Empty : $"  ·  {details}";
        string text        = $"{IndentAnalyzer}  ✔  {operation}{scanPart}  ·  {FormatElapsed(elapsed)}{detailsPart}";

        lock (_consoleGate)
        {
            _scanLineActive = true;
            int paddedLength = Math.Max(_lastScanLineLength, text.Length);
            AnsiConsole.Write(new Text($"\r{text.PadRight(paddedLength)}"));
            AnsiConsole.WriteLine();
            _scanLineActive = false;
            _lastScanLineLength = 0;
        }
    }

    // ── Utility messages ────────────────────────────────────────────────────

    public static void Info(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [deepskyblue1]ℹ[/]  {Escape(message)}");
        }
    }

    public static void Warning(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [yellow]⚠[/]  {Escape(message)}");
        }
    }

    public static void Error(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [red]✖[/]  {Escape(message)}");
        }
    }

    public static void Success(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✔[/]  {Escape(message)}");
        }
    }

    public static void WriteVerbose(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [grey][DIAG][/]  {Escape(message)}");
        }
    }

    // ── Memory diagnostics ──────────────────────────────────────────────────

    /// <summary>
    /// Prints the header row for the per-analyzer memory table.
    /// </summary>
    public static void MemoryTableHeader()
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Timestamp()} [deepskyblue1]🧠  Memory usage per analyzer[/]");
            PrintMemoryColumnHeader_NoLock();
        }
    }

    /// <summary>
    /// Prints the header row for the per-stage memory table.
    /// </summary>
    public static void MemoryStageTableHeader()
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Timestamp()} [deepskyblue1]🧠  Memory usage per pipeline stage[/]");
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
        string wsDeltaStr    = FormatSignedBytes(wsDelta);
        string wsAfterStr    = FormatBytes((ulong)Math.Max(0, wsAfter));
        string managedStr    = FormatSignedBytes(managedDelta);
        string deltaColor    = wsDelta > 50 * 1024 * 1024 ? "yellow" : wsDelta > 200 * 1024 * 1024 ? "red" : "grey";

        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine(
                $"{IndentAnalyzer}[white]{Escape(analyzerName),-42}[/]" +
                $"  [{deltaColor}]{Escape(wsDeltaStr),10}[/]" +
                $"  [grey]{Escape(wsAfterStr),10}[/]" +
                $"  [grey]{Escape(managedStr),10}[/]");
        }
    }

    /// <summary>
    /// Prints the process-level peak working set observed across the analysis run.
    /// </summary>
    public static void MemoryTableFooter(long peakWorkingSet, long baselineWorkingSet)
    {
        long totalDelta = peakWorkingSet - baselineWorkingSet;
        string peakStr  = FormatBytes((ulong)Math.Max(0, peakWorkingSet));
        string deltaStr = FormatSignedBytes(totalDelta);

        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{IndentAnalyzer}[grey]{new string('─', 78)}[/]");
            AnsiConsole.MarkupLine(
                $"{IndentAnalyzer}[grey]Peak process working set:[/]  [bold white]{Escape(peakStr)}[/]" +
                $"  [grey](Δ from baseline: {Escape(deltaStr)})[/]");
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private static string Timestamp() => $"[grey][[{DateTime.Now:HH:mm:ss}]][/]";

    private static string Escape(string value) => Markup.Escape(value);

    private static void FlushScanLineIfNeeded_NoLock()
    {
        if (!_scanLineActive)
        {
            return;
        }

        AnsiConsole.WriteLine();
        _scanLineActive = false;
        _lastScanLineLength = 0;
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
        if (bytes >= 1024UL * 1024)        return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024UL)               return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string FormatSignedBytes(long bytes)
    {
        string sign = bytes >= 0 ? "+" : "-";
        return $"{sign}{FormatBytes((ulong)Math.Abs(bytes))}";
    }
}
