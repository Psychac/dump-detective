using Microsoft.Diagnostics.Tracing;

// Throwaway probe: confirm PerfInfoSample (CPU sampling) events actually exist in a real .etl and
// inspect their real field shapes, before designing trace.cpu-samples / CpuHotspotAnalyzer around
// them. Not part of the shipped product.
//
// Usage: CpuSampleProbe <etl-path> [maxSamples]

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: CpuSampleProbe <etl-path> [maxSamples]");
    return 1;
}

string etlPath = args[0];
int maxSamples = args.Length > 1 ? int.Parse(args[1]) : 20;

long sampleCount = 0;
long stackWalkCount = 0;
int printed = 0;
var distinctProcessIds = new HashSet<int>();
var samplesPerProcess = new Dictionary<int, long>();

using var source = new ETWTraceEventSource(etlPath);

source.Kernel.PerfInfoSample += data =>
{
    sampleCount++;
    distinctProcessIds.Add(data.ProcessID);
    samplesPerProcess[data.ProcessID] = samplesPerProcess.GetValueOrDefault(data.ProcessID) + 1;

    if (printed < maxSamples)
    {
        printed++;
        Console.WriteLine($"[PerfInfoSample] PID={data.ProcessID} TID={data.ThreadID} " +
            $"TimeStampRelMSec={data.TimeStampRelativeMSec:F4} IP=0x{data.InstructionPointer:X} " +
            $"NonProcess={data.NonProcess} Priority={data.Priority}");
    }
};

source.Kernel.StackWalkStack += data =>
{
    stackWalkCount++;
    if (stackWalkCount <= 5)
    {
        Console.WriteLine($"[StackWalkStack] PID={data.ProcessID} TID={data.ThreadID} " +
            $"EventTimeStampRelativeMSec={data.EventTimeStampRelativeMSec:F4} FrameCount={data.FrameCount} " +
            $"IP0=0x{(data.FrameCount > 0 ? data.InstructionPointer(0) : 0):X}");
    }
};

source.Process();

Console.WriteLine();
Console.WriteLine($"Total PerfInfoSample: {sampleCount:N0}");
Console.WriteLine($"Total StackWalkStack: {stackWalkCount:N0}");
Console.WriteLine($"Distinct process IDs with samples: {distinctProcessIds.Count}");
foreach ((int pid, long count) in samplesPerProcess.OrderByDescending(kv => kv.Value).Take(10))
    Console.WriteLine($"  PID {pid}: {count:N0} samples");

return 0;
