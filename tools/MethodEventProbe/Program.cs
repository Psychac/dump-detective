using Microsoft.Diagnostics.Tracing;

// Throwaway probe: print real MethodLoadVerbose / MethodDCStartVerboseV2 payload shapes from a
// real .etl, to ground DumpDetective.Sources.NetTrace's trace.methods indexer in verified field
// values instead of guessed ones. Not part of the shipped product.
//
// Usage: MethodEventProbe <etl-path> [maxSamples]

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: MethodEventProbe <etl-path> [maxSamples]");
    return 1;
}

string etlPath = args[0];
int maxSamples = args.Length > 1 ? int.Parse(args[1]) : 20;

int loadVerboseCount = 0;
int dcStartVerboseCount = 0;
int printed = 0;
var distinctProcessIds = new HashSet<int>();

using var source = new ETWTraceEventSource(etlPath);

void MaybeStop()
{
    if (printed >= maxSamples && loadVerboseCount + dcStartVerboseCount >= 5000)
        source.StopProcessing();
}

source.Clr.MethodLoadVerbose += data =>
{
    loadVerboseCount++;
    distinctProcessIds.Add(data.ProcessID);
    if (printed < maxSamples)
    {
        printed++;
        Console.WriteLine($"[LoadVerbose] PID={data.ProcessID} MethodID={data.MethodID} ModuleID={data.ModuleID} " +
            $"StartAddr=0x{data.MethodStartAddress:X} Size={data.MethodSize} Token={data.MethodToken} " +
            $"Flags={data.MethodFlags} IsDynamic={data.IsDynamic} IsGeneric={data.IsGeneric} IsJitted={data.IsJitted}");
        Console.WriteLine($"    Namespace=[{data.MethodNamespace}]");
        Console.WriteLine($"    Name=[{data.MethodName}]");
        Console.WriteLine($"    Signature=[{data.MethodSignature}]");
    }
    MaybeStop();
};

source.Clr.MethodDCStartVerboseV2 += data =>
{
    dcStartVerboseCount++;
    distinctProcessIds.Add(data.ProcessID);
    if (printed < maxSamples)
    {
        printed++;
        Console.WriteLine($"[DCStartVerboseV2] PID={data.ProcessID} MethodID={data.MethodID} ModuleID={data.ModuleID} " +
            $"StartAddr=0x{data.MethodStartAddress:X} Size={data.MethodSize} Token={data.MethodToken} " +
            $"Flags={data.MethodFlags} IsDynamic={data.IsDynamic} IsGeneric={data.IsGeneric} IsJitted={data.IsJitted}");
        Console.WriteLine($"    Namespace=[{data.MethodNamespace}]");
        Console.WriteLine($"    Name=[{data.MethodName}]");
        Console.WriteLine($"    Signature=[{data.MethodSignature}]");
    }
    MaybeStop();
};

source.Process();

Console.WriteLine();
Console.WriteLine($"Total MethodLoadVerbose: {loadVerboseCount:N0}");
Console.WriteLine($"Total MethodDCStartVerboseV2: {dcStartVerboseCount:N0}");
Console.WriteLine($"Distinct process IDs seen: {distinctProcessIds.Count} -> {string.Join(", ", distinctProcessIds.Take(20))}");
return 0;
