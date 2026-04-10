using System.Diagnostics;
using Spectre.Console;

namespace DumpDetective.Utilities
{
    internal static class ConsoleUx
    {
        private static readonly object _scanLineLock = new();
        private static bool _scanLineActive;
        private static int _lastScanLineLength;
        private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        private static int _spinnerIndex;

        public static void Header(string title)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{Escape(title)}[/]").LeftJustified());
        }

        public static void Info(string message)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.MarkupLine($"{Timestamp()} [deepskyblue1]ℹ[/] {Escape(message)}");
        }

        public static void StageStart(int current, int total, string stageName, TimeSpan? eta = null)
        {
            FlushScanLineIfNeeded();
            double percent = total > 0 ? (current - 1) * 100.0 / total : 0;
            AnsiConsole.WriteLine();
            if (eta.HasValue)
            {
                AnsiConsole.MarkupLine($"{Timestamp()} [yellow]▶[/] [bold]Stage {current}/{total}[/] ([silver]{percent:F0}%[/]): {Escape(stageName)} [silver]| ETA {Escape(FormatElapsed(eta.Value))}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"{Timestamp()} [yellow]▶[/] [bold]Stage {current}/{total}[/] ([silver]{percent:F0}%[/]): {Escape(stageName)}");
            }
        }

        public static void StageComplete(string stageName, Stopwatch stopwatch)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✅[/] Stage complete: {Escape(stageName)} [silver]({Escape(FormatElapsed(stopwatch.Elapsed))})[/]");
        }

        public static void AnalyzerStart(int current, int total, string analyzerName)
        {
            // Intentionally silent to reduce console noise.
            // Completion line includes analyzer index and timing.
        }

        public static void AnalyzerComplete(int current, int total, string analyzerName, Stopwatch stopwatch)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.MarkupLine($"{Timestamp()}   [green]✓[/] Analyzer {current}/{total}: {Escape(analyzerName)} [silver]({Escape(FormatElapsed(stopwatch.Elapsed))})[/]");
        }

        public static void Success(string message)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✅[/] {Escape(message)}");
        }

        public static void Error(string message)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.MarkupLine($"{Timestamp()} [red]❌[/] {Escape(message)}");
        }

        public static void MemorySnapshot(MemorySnapshot snapshot)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.MarkupLine($"{Timestamp()} [bold yellow]Memory Snapshot[/] [grey]({Escape(snapshot.Label)})[/]");
            AnsiConsole.MarkupLine(
                $"{Timestamp()} [silver]Managed:[/] [deepskyblue1]{Escape(FormatHelper.FormatBytes((ulong)snapshot.ManagedMemory))}[/]  [silver]WS:[/] [deepskyblue1]{Escape(FormatHelper.FormatBytes((ulong)snapshot.WorkingSet))}[/]  [silver]Private:[/] [deepskyblue1]{Escape(FormatHelper.FormatBytes((ulong)snapshot.PrivateMemory))}[/]  [silver]GC:[/] {snapshot.Gen0Collections}/{snapshot.Gen1Collections}/{snapshot.Gen2Collections}");
        }

        public static void MemoryDelta(MemorySnapshot before, MemorySnapshot after)
        {
            FlushScanLineIfNeeded();
            long managedDelta = after.ManagedMemory - before.ManagedMemory;
            long workingSetDelta = after.WorkingSet - before.WorkingSet;
            long privateDelta = after.PrivateMemory - before.PrivateMemory;

            string checkpointLabel = $"{before.Label} -> {after.Label}";
            AnsiConsole.MarkupLine($"{Timestamp()} [bold yellow]Memory Delta[/] [grey]({Escape(checkpointLabel)})[/]");
            AnsiConsole.MarkupLine(
                $"{Timestamp()} [silver]Managed:[/] {FormatDelta(managedDelta)} [grey]({FormatPercentDelta(managedDelta, before.ManagedMemory)})[/]  [silver]WS:[/] {FormatDelta(workingSetDelta)} [grey]({FormatPercentDelta(workingSetDelta, before.WorkingSet)})[/]  [silver]Private:[/] {FormatDelta(privateDelta)} [grey]({FormatPercentDelta(privateDelta, before.PrivateMemory)})[/]");
            AnsiConsole.MarkupLine(
                $"{Timestamp()} [silver]GC Δ:[/] +{after.Gen0Collections - before.Gen0Collections}/+{after.Gen1Collections - before.Gen1Collections}/+{after.Gen2Collections - before.Gen2Collections} [grey](Gen0/Gen1/Gen2)[/]");

            string health = BuildMemoryInsight(managedDelta, workingSetDelta, privateDelta);
            AnsiConsole.MarkupLine($"{Timestamp()} [silver]Memory Insight ({Escape(checkpointLabel)}):[/] {health}");
        }

        public static void PipelineSummary(IReadOnlyList<(string StageName, TimeSpan Duration, int AnalyzerCount)> stageResults)
        {
            FlushScanLineIfNeeded();
            if (stageResults.Count == 0)
            {
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn(new TableColumn("Stage").LeftAligned());
            table.AddColumn(new TableColumn("Analyzers").RightAligned());
            table.AddColumn(new TableColumn("Duration").RightAligned());

            TimeSpan totalDuration = TimeSpan.Zero;
            foreach (var result in stageResults)
            {
                totalDuration += result.Duration;
                table.AddRow(
                    Escape(result.StageName),
                    result.AnalyzerCount.ToString(),
                    Escape(FormatElapsed(result.Duration)));
            }

            table.AddEmptyRow();
            table.AddRow("[bold]Total[/]", stageResults.Sum(s => s.AnalyzerCount).ToString(), $"[bold]{Escape(FormatElapsed(totalDuration))}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(table).Header("[bold green]Pipeline Summary[/]").Border(BoxBorder.Rounded));
        }

        public static void PipelineProgress(int completedStages, int totalStages, TimeSpan elapsed, TimeSpan? eta)
        {
            FlushScanLineIfNeeded();
            double percent = totalStages > 0 ? completedStages * 100.0 / totalStages : 0;
            string etaText = eta.HasValue ? FormatElapsed(eta.Value) : "calculating...";
            AnsiConsole.MarkupLine($"{Timestamp()} [deepskyblue1]ℹ[/] Progress: [bold]{completedStages}/{totalStages}[/] ([silver]{percent:F0}%[/]) | Elapsed: [silver]{Escape(FormatElapsed(elapsed))}[/] | Remaining: [silver]{Escape(etaText)}[/]");
        }

        public static void PerformanceCheckpoint(string label, TimeSpan duration)
        {
            FlushScanLineIfNeeded();
            AnsiConsole.MarkupLine($"{Timestamp()} [mediumpurple2]⏱[/] {Escape(label)}: [silver]{Escape(FormatElapsed(duration))}[/]");
        }

        public static void PerformanceBreakdown(string title, IReadOnlyList<(string Name, TimeSpan Duration)> timings, TimeSpan total)
        {
            FlushScanLineIfNeeded();
            if (timings.Count == 0)
            {
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Timestamp()} [bold mediumpurple2]⏱ {Escape(title)}[/]");
            foreach (var timing in timings.OrderByDescending(t => t.Duration))
            {
                double percent = total.TotalMilliseconds > 0
                    ? timing.Duration.TotalMilliseconds * 100.0 / total.TotalMilliseconds
                    : 0;
                AnsiConsole.MarkupLine($"{Timestamp()}   [mediumpurple2]•[/] {Escape(timing.Name)}: [silver]{Escape(FormatElapsed(timing.Duration))}[/] ([silver]{percent:F1}%[/])");
            }

            AnsiConsole.MarkupLine($"{Timestamp()}   [bold]Total:[/] [silver]{Escape(FormatElapsed(total))}[/]");
        }

        public static void ObjectScanProgress(string operation, long scannedCount, TimeSpan elapsed)
        {
            double perSecond = elapsed.TotalSeconds > 0 ? scannedCount / elapsed.TotalSeconds : 0;
            string spinner = SpinnerFrames[_spinnerIndex++ % SpinnerFrames.Length];
            string text = $"[{DateTime.Now:HH:mm:ss}] {spinner} {operation}: {scannedCount:N0} objs • {FormatElapsed(elapsed)} • ~{perSecond:N0}/s";

            lock (_scanLineLock)
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
            string text = $"[{DateTime.Now:HH:mm:ss}] ✓ {operation}: {scannedCount:N0} objs • {FormatElapsed(elapsed)} • ~{perSecond:N0}/s";

            lock (_scanLineLock)
            {
                _scanLineActive = true;
                int paddedLength = Math.Max(_lastScanLineLength, text.Length);
                AnsiConsole.Write(new Text($"\r{text.PadRight(paddedLength)}"));
                AnsiConsole.WriteLine();
                _scanLineActive = false;
                _lastScanLineLength = 0;
            }
        }

        private static void FlushScanLineIfNeeded()
        {
            lock (_scanLineLock)
            {
                if (!_scanLineActive)
                    return;

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

        private static string FormatDelta(long value)
        {
            string direction = value >= 0 ? "↑" : "↓";
            string color = value >= 0 ? "yellow" : "green";
            string bytes = Escape(FormatHelper.FormatBytes((ulong)Math.Abs(value)));
            return $"[{color}]{bytes} {direction}[/]";
        }

        private static string FormatPercentDelta(long delta, long baseline)
        {
            if (baseline <= 0)
                return "n/a";

            double percent = Math.Abs(delta) * 100.0 / baseline;
            string arrow = delta >= 0 ? "↑" : "↓";
            string color = delta >= 0 ? "yellow" : "green";
            return $"[{color}]{percent:F1}% {arrow}[/]";
        }

        private static string BuildMemoryInsight(long managedDelta, long workingSetDelta, long privateDelta)
        {
            bool managedUp = managedDelta > 0;
            bool wsUp = workingSetDelta > 0;
            bool privateUp = privateDelta > 0;

            if (managedUp && wsUp && privateUp)
                return "[yellow]All memory indicators increased[/] (possible accumulation in this stage).";

            if (!managedUp && !wsUp && !privateUp)
                return "[green]All memory indicators decreased[/] (good memory recovery after stage).";

            if (managedUp && !wsUp)
                return "Managed heap increased while working set stayed flat/decreased (likely transient managed growth).";

            if (!managedUp && wsUp)
                return "Working set increased while managed heap decreased (native/OS memory effects likely).";

            return "Mixed memory movement across metrics (normal for complex analysis stages).";
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
}
