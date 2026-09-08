using System.Diagnostics;
using System.Text.RegularExpressions;

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

// Compiler-generated breakdown: how do lambda/closure/state-machine names behave under exact-
// string join, and does stripping the compiler-assigned ordinal (which can shift between builds)
// recover matches that exact join missed? This only measures same-build behavior — it cannot
// prove ordinals are stable across builds, only whether canonicalization has anything to recover.
var dumpCompilerGenerated = new List<string>();
foreach (string name in dumpTypeNames)
{
    if (IsCompilerGenerated(name))
        dumpCompilerGenerated.Add(name);
}

var traceCompilerGenerated = new List<string>();
var traceCanonical = new HashSet<string>(StringComparer.Ordinal);
foreach (string name in traceTypeNames)
{
    if (IsCompilerGenerated(name))
        traceCompilerGenerated.Add(name);
    traceCanonical.Add(Canonicalize(name));
}

int compilerGeneratedExactMatches = 0;
var compilerGeneratedDumpOnly = new List<string>();
foreach (string name in dumpCompilerGenerated)
{
    if (traceTypeNames.Contains(name))
        compilerGeneratedExactMatches++;
    else
        compilerGeneratedDumpOnly.Add(name);
}

int recoveredByCanonicalization = 0;
var recoveredSamples = new List<(string Dump, string Canonical)>();
foreach (string name in compilerGeneratedDumpOnly)
{
    string canonical = Canonicalize(name);
    if (traceCanonical.Contains(canonical))
    {
        recoveredByCanonicalization++;
        if (recoveredSamples.Count < 15)
            recoveredSamples.Add((name, canonical));
    }
}

Console.WriteLine();
Console.WriteLine("--- Compiler-generated (lambda/closure/state-machine) breakdown ---");
Console.WriteLine($"Dump compiler-generated type names: {dumpCompilerGenerated.Count} of {dumpTypeNames.Count}");
Console.WriteLine($"Trace compiler-generated type names: {traceCompilerGenerated.Count} of {traceTypeNames.Count}");
Console.WriteLine($"Exact-string match within compiler-generated subset: {compilerGeneratedExactMatches} of {dumpCompilerGenerated.Count}"
    + (dumpCompilerGenerated.Count == 0 ? "" : $" ({(double)compilerGeneratedExactMatches / dumpCompilerGenerated.Count:P1})"));
Console.WriteLine($"Additional matches recovered by ordinal-stripping canonicalization: {recoveredByCanonicalization} of {compilerGeneratedDumpOnly.Count} remaining"
    + (compilerGeneratedDumpOnly.Count == 0 ? "" : $" ({(double)recoveredByCanonicalization / compilerGeneratedDumpOnly.Count:P1})"));

Console.WriteLine();
Console.WriteLine("Sample recovered by canonicalization (dump name -> canonical form, first 15):");
foreach ((string dumpName, string canonical) in recoveredSamples)
    Console.WriteLine($"  {dumpName}  ->  {canonical}");

Console.WriteLine();
Console.WriteLine("Sample still unmatched after canonicalization (first 15):");
int shown = 0;
foreach (string name in compilerGeneratedDumpOnly)
{
    if (traceCanonical.Contains(Canonicalize(name)))
        continue;
    Console.WriteLine($"  {name}");
    if (++shown >= 15)
        break;
}

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

static bool IsCompilerGenerated(string name) =>
    name.Contains("DisplayClass", StringComparison.Ordinal)
    || name.Contains("<>c", StringComparison.Ordinal)
    || Regex.IsMatch(name, @">d__\d")
    || Regex.IsMatch(name, @">b__\d");

// Strips the compiler-assigned ordinal suffix so e.g. "<>c__DisplayClass5_0" and "<Foo>d__12"
// compare equal across builds where the compiler renumbered them but the shape didn't change.
static string Canonicalize(string name)
{
    name = Regex.Replace(name, @"(DisplayClass)\d+(_\d+)?", "$1");
    name = Regex.Replace(name, @"(>d__)\d+", "$1");
    name = Regex.Replace(name, @"(>b__)\d+", "$1");
    return name;
}
