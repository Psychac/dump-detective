using Spectre.Console;

namespace DumpDetective.Cli.Console;

internal static class ConsoleUx
{
    private static readonly Lock _consoleGate = new();
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static bool _scanLineActive;
    private static int _lastScanLineLength;
    private static int _spinnerIndex;

    public static void Header(string title)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{Escape(title)}[/]").LeftJustified());
        }
    }

    public static void Info(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [deepskyblue1]ℹ[/] {Escape(message)}");
        }
    }

    public static void Warning(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [yellow]⚠[/] {Escape(message)}");
        }
    }

    public static void Error(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [red]❌[/] {Escape(message)}");
        }
    }

    public static void Success(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✅[/] {Escape(message)}");
        }
    }

    public static void StageStart(int current, int total, string stageName)
    {
        double percent = total > 0 ? (current - 1) * 100.0 / total : 0;
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Timestamp()} [yellow]▶[/] [bold]Stage {current}/{total}[/] ([silver]{percent:F0}%[/]): {Escape(stageName)}");
        }
    }

    public static void StageComplete(int current, int total, string stageName, TimeSpan duration)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✅[/] Stage complete: {Escape(stageName)} [silver]({Escape(FormatElapsed(duration))})[/]");
        }
    }

    public static void WriteVerbose(string message)
    {
        lock (_consoleGate)
        {
            FlushScanLineIfNeeded_NoLock();
            AnsiConsole.MarkupLine($"{Timestamp()} [grey][DIAG][/] {Escape(message)}");
        }
    }

    public static void ObjectScanProgress(string operation, long scannedCount, TimeSpan elapsed)
    {
        double perSecond = elapsed.TotalSeconds > 0 ? scannedCount / elapsed.TotalSeconds : 0;
        string spinner = SpinnerFrames[_spinnerIndex++ % SpinnerFrames.Length];
        string text = $"[{DateTime.Now:HH:mm:ss}] {spinner} scanning {operation} | {scannedCount:N0} objs | {FormatElapsed(elapsed)} | {perSecond:N0}/s";

        lock (_consoleGate)
        {
            _scanLineActive = true;
            int paddedLength = Math.Max(_lastScanLineLength, text.Length);
            _lastScanLineLength = paddedLength;
            AnsiConsole.Write(new Text($"\r{text.PadRight(paddedLength)}"));
        }
    }

    public static void ObjectScanComplete(string operation, long scannedCount, TimeSpan elapsed)
    {
        double perSecond = elapsed.TotalSeconds > 0 ? scannedCount / elapsed.TotalSeconds : 0;
        string text = $"[{DateTime.Now:HH:mm:ss}] ✓ scanned {operation} | {scannedCount:N0} objs | {FormatElapsed(elapsed)} | {perSecond:N0}/s";

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

    private static string Timestamp()
    {
        return $"[grey][[{DateTime.Now:HH:mm:ss}]][/]";
    }

    private static string Escape(string value)
    {
        return Markup.Escape(value);
    }

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
}
