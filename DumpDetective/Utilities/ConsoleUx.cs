using System.Diagnostics;
using Spectre.Console;

namespace DumpDetective.Utilities
{
    internal static class ConsoleUx
    {
        public static void Header(string title)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{Escape(title)}[/]").LeftJustified());
        }

        public static void Info(string message)
        {
            AnsiConsole.MarkupLine($"{Timestamp()} [deepskyblue1]ℹ[/] {Escape(message)}");
        }

        public static void StageStart(int current, int total, string stageName)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Timestamp()} [yellow]▶[/] [bold]Stage {current}/{total}[/]: {Escape(stageName)}");
        }

        public static void StageComplete(string stageName, Stopwatch stopwatch)
        {
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✅[/] Stage complete: {Escape(stageName)} [silver]({Escape(FormatElapsed(stopwatch.Elapsed))})[/]");
        }

        public static void AnalyzerStart(int current, int total, string analyzerName)
        {
            AnsiConsole.MarkupLine($"{Timestamp()}   [grey]•[/] Analyzer {current}/{total}: {Escape(analyzerName)}");
        }

        public static void AnalyzerComplete(string analyzerName, Stopwatch stopwatch)
        {
            AnsiConsole.MarkupLine($"{Timestamp()}   [green]✓[/] {Escape(analyzerName)} finished in [silver]{Escape(FormatElapsed(stopwatch.Elapsed))}[/]");
        }

        public static void Success(string message)
        {
            AnsiConsole.MarkupLine($"{Timestamp()} [green]✅[/] {Escape(message)}");
        }

        public static void Error(string message)
        {
            AnsiConsole.MarkupLine($"{Timestamp()} [red]❌[/] {Escape(message)}");
        }

        public static void MemorySnapshot(MemorySnapshot snapshot)
        {
            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn(new TableColumn("Metric").LeftAligned());
            table.AddColumn(new TableColumn("Value").RightAligned());

            table.AddRow("Checkpoint", $"[bold]{Escape(snapshot.Label)}[/]");
            table.AddRow("Managed Heap", Escape(FormatHelper.FormatBytes((ulong)snapshot.ManagedMemory)));
            table.AddRow("Working Set", Escape(FormatHelper.FormatBytes((ulong)snapshot.WorkingSet)));
            table.AddRow("Private Memory", Escape(FormatHelper.FormatBytes((ulong)snapshot.PrivateMemory)));
            table.AddRow("GC Collections", $"Gen0={snapshot.Gen0Collections}, Gen1={snapshot.Gen1Collections}, Gen2={snapshot.Gen2Collections}");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(table).Header("[bold yellow]Memory Snapshot[/]").Border(BoxBorder.Rounded));
        }

        public static void MemoryDelta(MemorySnapshot before, MemorySnapshot after)
        {
            long managedDelta = after.ManagedMemory - before.ManagedMemory;
            long workingSetDelta = after.WorkingSet - before.WorkingSet;
            long privateDelta = after.PrivateMemory - before.PrivateMemory;

            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn(new TableColumn("Checkpoint").LeftAligned());
            table.AddColumn(new TableColumn("Managed Δ").RightAligned());
            table.AddColumn(new TableColumn("Working Set Δ").RightAligned());
            table.AddColumn(new TableColumn("Private Δ").RightAligned());
            table.AddColumn(new TableColumn("GC Δ").RightAligned());

            table.AddRow(
                Escape(after.Label),
                FormatDelta(managedDelta),
                FormatDelta(workingSetDelta),
                FormatDelta(privateDelta),
                $"{after.Gen0Collections - before.Gen0Collections}/{after.Gen1Collections - before.Gen1Collections}/{after.Gen2Collections - before.Gen2Collections}");

            AnsiConsole.Write(table);
        }

        public static void PipelineSummary(IReadOnlyList<(string StageName, TimeSpan Duration, int AnalyzerCount)> stageResults)
        {
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
