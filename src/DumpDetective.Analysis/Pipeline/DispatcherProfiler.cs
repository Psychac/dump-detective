using System.Diagnostics;
using System.Text;

namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Opt-in (<c>DD_DISPATCHER_PROFILE=1</c>) attribution for <see cref="HeapIndexScanDispatcher"/>.
/// The dispatcher's normal diagnostics report one duration for the whole parallel pass, which
/// bundles three structurally different costs — per-worker participant setup, the actual
/// <c>Parallel.For</c> over the index, and the merge — so a slow run can't be attributed without
/// this. Writes to stderr so the breakdown survives the live status line rewriting stdout.
/// </summary>
internal static class DispatcherProfiler
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("DD_DISPATCHER_PROFILE") == "1";

    // Every Nth index entry has its per-participant OnHeapEntry cost timestamped. Timing every
    // entry would add ~2 QueryPerformanceCounter calls per participant per object (8 participants
    // x tens of millions of entries), which would dominate the very thing being measured; over a
    // sample this large the 1-in-N estimate is stable to well within the precision needed to rank
    // participants against each other.
    public const int SampleEvery = 256;

    private static readonly Lock Gate = new();
    private static readonly List<string> Lines = [];

    public static void Log(string line)
    {
        if (!Enabled)
            return;

        lock (Gate)
            Lines.Add(line);

        Console.Error.WriteLine($"[dispatcher-profile] {line}");
        Console.Error.Flush();
    }

    public static void LogTable(string title, IEnumerable<(string Label, double Ms, long Count)> rows)
    {
        if (!Enabled)
            return;

        var sorted = new List<(string Label, double Ms, long Count)>(rows);
        sorted.Sort((a, b) => b.Ms.CompareTo(a.Ms));

        var sb = new StringBuilder();
        sb.AppendLine(title);
        double total = 0;
        foreach (var r in sorted)
            total += r.Ms;

        foreach (var r in sorted)
        {
            double pct = total > 0 ? r.Ms * 100.0 / total : 0;
            sb.AppendLine($"    {r.Label,-34} {r.Ms,12:N1} ms  {pct,5:N1}%  n={r.Count:N0}");
        }
        sb.Append($"    {"TOTAL",-34} {total,12:N1} ms");

        Log(sb.ToString());
    }

    public static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
