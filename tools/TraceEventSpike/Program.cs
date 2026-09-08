using System.Diagnostics;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

// Throwaway probe for docs/refactor/modularity/phase-1-contracts-sdk.md step 3a (the TraceEvent
// dependency spike): before docs/refactor/modularity/phase-6-trace-source.md commits to
// Microsoft.Diagnostics.Tracing.TraceEvent, confirm its low-level reader is genuinely a
// single-pass streaming callback API with memory bounded by events-in-flight rather than by
// trace size — the same discipline this project already requires of heap scanning.
//
// Tests ETWTraceEventSource (.etl) as a stand-in for EventPipeEventSource (.nettrace): both are
// TraceEventDispatcher subclasses sharing the same callback-dispatch architecture in this same
// package, but no .nettrace sample was available for this run — see the caveat printed below.
//
// Also runs TraceLog.OpenOrConvert for contrast, since that's the API the entity-join spike
// (tools/EntityJoinSpike) already leaned on, and its memory/disk profile is a different animal
// from raw streaming.
//
// Usage: TraceEventSpike <etl-path>

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: TraceEventSpike <etl-path>");
    return 1;
}

string etlPath = args[0];
var fileInfo = new FileInfo(etlPath);
Console.WriteLine($"Trace file: {etlPath} ({fileInfo.Length / 1024.0 / 1024.0:F1} MB)");
Console.WriteLine();
Console.WriteLine("CAVEAT: this is an .etl (ETW) trace, not a .nettrace (EventPipe) trace — no");
Console.WriteLine(".nettrace sample was available. ETWTraceEventSource and EventPipeEventSource");
Console.WriteLine("share the same TraceEventDispatcher streaming architecture in this package, so");
Console.WriteLine("this is a reasonable proxy for the streaming-behavior question, but it is not a");
Console.WriteLine("direct test of the EventPipe reader itself. Re-run against a real .nettrace");
Console.WriteLine("before treating this as final confirmation for Phase 6.");
Console.WriteLine();

RunStreamingPass(etlPath);
Console.WriteLine();
RunTraceLogConversion(etlPath);

return 0;

static void RunStreamingPass(string etlPath)
{
    Console.WriteLine("--- Pass 1: ETWTraceEventSource, raw single-pass streaming (AllEvents callback) ---");
    Process process = Process.GetCurrentProcess();
    long baselineWorkingSet = GetWorkingSet(process);
    var stopwatch = Stopwatch.StartNew();

    long eventCount = 0;
    long peakWorkingSetDeltaBytes = 0;
    var samples = new List<(long EventCount, long WorkingSetDeltaMb)>();

    using var source = new ETWTraceEventSource(etlPath);
    source.AllEvents += delegate (TraceEvent data)
    {
        eventCount++;
        if (eventCount % 100_000 == 0)
        {
            long delta = GetWorkingSet(process) - baselineWorkingSet;
            if (delta > peakWorkingSetDeltaBytes)
                peakWorkingSetDeltaBytes = delta;
            samples.Add((eventCount, delta / 1024 / 1024));
        }
    };
    source.Process();

    Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");
    Console.WriteLine($"Events processed: {eventCount:N0}");
    Console.WriteLine("Working-set delta over time (event count -> MB above baseline):");
    foreach ((long count, long mb) in samples)
        Console.WriteLine($"  {count,12:N0} events -> {mb,6} MB");
    Console.WriteLine($"Peak working-set delta during streaming pass: {peakWorkingSetDeltaBytes / 1024 / 1024} MB");
    Console.WriteLine($"Trace file size: {new FileInfo(etlPath).Length / 1024.0 / 1024.0:F1} MB");
}

static void RunTraceLogConversion(string etlPath)
{
    Console.WriteLine("--- Pass 2: TraceLog.OpenOrConvert, for contrast (what EntityJoinSpike used) ---");
    string etlxPath = Path.ChangeExtension(etlPath, ".etlx");
    bool etlxAlreadyExisted = File.Exists(etlxPath);

    Process process = Process.GetCurrentProcess();
    long baselineWorkingSet = GetWorkingSet(process);
    var stopwatch = Stopwatch.StartNew();

    using TraceLog traceLog = TraceLog.OpenOrConvert(etlPath);
    long methodCount = traceLog.CodeAddresses.Methods.Count();

    long workingSetDeltaBytes = GetWorkingSet(process) - baselineWorkingSet;
    long etlxSize = File.Exists(etlxPath) ? new FileInfo(etlxPath).Length : -1;

    Console.WriteLine($"Elapsed ({(etlxAlreadyExistedText(etlxAlreadyExisted))}): {stopwatch.Elapsed}");
    Console.WriteLine($"Distinct methods indexed: {methodCount:N0}");
    Console.WriteLine($"Working-set delta after load: {workingSetDeltaBytes / 1024 / 1024} MB");
    if (etlxSize >= 0)
    {
        Console.WriteLine(
            $".etlx index on disk: {etlxSize / 1024.0 / 1024.0:F1} MB " +
            $"(source .etl was {new FileInfo(etlPath).Length / 1024.0 / 1024.0:F1} MB, " +
            $"{etlxSize / (double)new FileInfo(etlPath).Length:F2}x)");
    }

    static string etlxAlreadyExistedText(bool existed) =>
        existed ? "reused existing .etlx, index build not measured" : "includes .etlx conversion";
}

static long GetWorkingSet(Process process)
{
    process.Refresh();
    return process.WorkingSet64;
}
