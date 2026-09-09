using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// Throwaway probe: print the real dispatch order and payload shapes of GC suspend/restart/start/stop
// and contention start/stop events from a real .etl, to ground the trace.gcevents/trace.contention
// indexers in verified sequencing instead of a guessed pairing model. Not part of the shipped product.
//
// Usage: GcContentionEventProbe <etl-path> [maxGcCycles]

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: GcContentionEventProbe <etl-path> [maxGcCycles]");
    return 1;
}

string etlPath = args[0];
int maxGcCycles = args.Length > 1 ? int.Parse(args[1]) : 15;

int gcCyclesPrinted = 0;
int contentionPrinted = 0;
const int maxContentionPrinted = 15;
var gcEventCounts = new Dictionary<string, int>();
var contentionEventCounts = new Dictionary<string, int>();
var contentionDurationNsByFlag = new Dictionary<string, (int zero, int nonZero)>();
int managedStopsPrintedSeparately = 0;
const int maxManagedStopsPrinted = 15;

using var source = new ETWTraceEventSource(etlPath);

void CountGc(string name) => gcEventCounts[name] = gcEventCounts.GetValueOrDefault(name) + 1;
void CountContention(string name) => contentionEventCounts[name] = contentionEventCounts.GetValueOrDefault(name) + 1;

source.Clr.GCSuspendEEStart += data =>
{
    CountGc(nameof(source.Clr.GCSuspendEEStart));
    if (gcCyclesPrinted < maxGcCycles)
    {
        Console.WriteLine($"[SuspendEEStart] PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4} " +
            $"Reason={data.Reason} Count={data.Count}");
    }
};

source.Clr.GCSuspendEEStop += data =>
{
    CountGc(nameof(source.Clr.GCSuspendEEStop));
    if (gcCyclesPrinted < maxGcCycles)
        Console.WriteLine($"[SuspendEEStop]  PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4}");
};

source.Clr.GCStart += data =>
{
    CountGc(nameof(source.Clr.GCStart));
    if (gcCyclesPrinted < maxGcCycles)
        Console.WriteLine($"[GCStart]        PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4} " +
            $"Count={data.Count} Depth={data.Depth} Reason={data.Reason} Type={data.Type}");
};

source.Clr.GCStop += data =>
{
    CountGc(nameof(source.Clr.GCStop));
    if (gcCyclesPrinted < maxGcCycles)
        Console.WriteLine($"[GCStop]         PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4} " +
            $"Count={data.Count} Depth={data.Depth}");
};

source.Clr.GCHeapStats += data =>
{
    CountGc(nameof(source.Clr.GCHeapStats));
    if (gcCyclesPrinted < maxGcCycles)
        Console.WriteLine($"[GCHeapStats]    PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4} " +
            $"TotalHeapSize={data.TotalHeapSize}");
};

source.Clr.GCRestartEEStart += data =>
{
    CountGc(nameof(source.Clr.GCRestartEEStart));
    if (gcCyclesPrinted < maxGcCycles)
        Console.WriteLine($"[RestartEEStart] PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4}");
};

source.Clr.GCRestartEEStop += data =>
{
    CountGc(nameof(source.Clr.GCRestartEEStop));
    if (gcCyclesPrinted < maxGcCycles)
    {
        Console.WriteLine($"[RestartEEStop]  PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4}");
        Console.WriteLine();
        gcCyclesPrinted++;
    }
};

source.Clr.ContentionStart += data =>
{
    CountContention(nameof(source.Clr.ContentionStart));
    if (contentionPrinted < maxContentionPrinted)
        Console.WriteLine($"[ContentionStart] PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4} " +
            $"Flags={data.ContentionFlags} LockID=0x{data.LockID:X} AssocObj=0x{data.AssociatedObjectID:X} OwnerTID={data.LockOwnerThreadID}");
};

source.Clr.ContentionStop += data =>
{
    CountContention(nameof(source.Clr.ContentionStop));
    if (contentionPrinted < maxContentionPrinted)
    {
        Console.WriteLine($"[ContentionStop]  PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4} " +
            $"Flags={data.ContentionFlags} DurationNs={data.DurationNs} Version={data.Version}");
        contentionPrinted++;
    }

    string flagKey = data.ContentionFlags.ToString();
    (int zero, int nonZero) counts = contentionDurationNsByFlag.GetValueOrDefault(flagKey);
    contentionDurationNsByFlag[flagKey] = data.DurationNs == 0.0
        ? (counts.zero + 1, counts.nonZero)
        : (counts.zero, counts.nonZero + 1);

    if (data.ContentionFlags == ContentionFlags.Managed && managedStopsPrintedSeparately < maxManagedStopsPrinted)
    {
        Console.WriteLine($"[ManagedStop]     PID={data.ProcessID} TID={data.ThreadID} TimeStampRelMSec={data.TimeStampRelativeMSec:F4} " +
            $"DurationNs={data.DurationNs} Version={data.Version}");
        managedStopsPrintedSeparately++;
    }
};

source.Process();

Console.WriteLine();
Console.WriteLine("GC event counts:");
foreach ((string name, int count) in gcEventCounts.OrderBy(kv => kv.Key))
    Console.WriteLine($"  {name}: {count:N0}");

Console.WriteLine("Contention event counts:");
foreach ((string name, int count) in contentionEventCounts.OrderBy(kv => kv.Key))
    Console.WriteLine($"  {name}: {count:N0}");

Console.WriteLine("ContentionStop DurationNs zero/non-zero by flag:");
foreach ((string flag, (int zero, int nonZero) counts) in contentionDurationNsByFlag.OrderBy(kv => kv.Key))
    Console.WriteLine($"  {flag}: zero={counts.zero:N0} nonZero={counts.nonZero:N0}");

return 0;
