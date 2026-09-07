using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Tracing.Etlx;

// Throwaway probe for docs/refactor/modularity-plan.md line 141's "Recommended fix — pull the
// entity-join spike into Phase 1": measures the join rate between trace-side method/type names
// (from an ETL, resolved via TraceEvent's TraceLog) and dump-side ClrMD type names for the SAME
// process, before EntityCanonicalizer or any of Phases 0-5 get built around an assumed join rate.
//
// Usage: EntityJoinSpike <dump-path> <etl-path>

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: EntityJoinSpike <dump-path> <etl-path>");
    return 1;
}

string dumpPath = args[0];
string etlPath = args[1];
var stopwatch = Stopwatch.StartNew();

var dumpTypeNames = new HashSet<string>(StringComparer.Ordinal);
using (DataTarget dataTarget = DataTarget.LoadDump(dumpPath))
{
    ClrInfo clrInfo = dataTarget.ClrVersions[0];
    using ClrRuntime runtime = clrInfo.CreateRuntime();
    ClrHeap heap = runtime.Heap;

    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        if (!obj.IsValid || obj.Type is null)
            continue;
        dumpTypeNames.Add(obj.Type.Name ?? "<unknown>");
    }
}

Console.WriteLine($"[{stopwatch.Elapsed}] Dump distinct heap-live type names: {dumpTypeNames.Count}");

var traceTypeNames = new HashSet<string>(StringComparer.Ordinal);
using (TraceLog traceLog = TraceLog.OpenOrConvert(etlPath))
{
    foreach (TraceMethod method in traceLog.CodeAddresses.Methods)
    {
        string? typeName = ExtractTypeName(method.FullMethodName);
        if (typeName is not null)
            traceTypeNames.Add(typeName);
    }
}

Console.WriteLine($"[{stopwatch.Elapsed}] Trace distinct type names: {traceTypeNames.Count}");

var matched = new List<string>();
var dumpOnly = new List<string>();
foreach (string name in dumpTypeNames)
{
    if (traceTypeNames.Contains(name))
        matched.Add(name);
    else
        dumpOnly.Add(name);
}

double joinRateOfDump = dumpTypeNames.Count == 0 ? 0 : (double)matched.Count / dumpTypeNames.Count;
double joinRateOfTrace = traceTypeNames.Count == 0 ? 0 : (double)matched.Count / traceTypeNames.Count;

Console.WriteLine();
Console.WriteLine($"Exact-string join: {matched.Count} matched");
Console.WriteLine($"  {joinRateOfDump:P1} of dump types found in trace");
Console.WriteLine($"  {joinRateOfTrace:P1} of trace types found in dump");

Console.WriteLine();
Console.WriteLine("Sample matched (first 20):");
foreach (string name in matched.Take(20))
    Console.WriteLine($"  {name}");

Console.WriteLine();
Console.WriteLine("Sample dump-only, i.e. canonicalization candidates (first 30):");
foreach (string name in dumpOnly.Take(30))
    Console.WriteLine($"  {name}");

return 0;

static string? ExtractTypeName(string fullMethodName)
{
    if (string.IsNullOrEmpty(fullMethodName) || fullMethodName.Contains('!'))
        return null; // native frame, e.g. "ntdll!RtlUserThreadStart" — not a managed type

    int parenIndex = fullMethodName.IndexOf('(');
    string beforeArgs = parenIndex >= 0 ? fullMethodName[..parenIndex] : fullMethodName;

    int lastDot = beforeArgs.LastIndexOf('.');
    return lastDot > 0 ? beforeArgs[..lastDot] : null;
}
