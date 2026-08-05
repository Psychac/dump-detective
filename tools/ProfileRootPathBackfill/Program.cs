using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

// Throwaway benchmark for the §4 gate in
// docs/analysis/phase1-redesigns/root-path-finder.md — measures the standalone cost
// of a multi-source BFS from GC roots that records a predecessor per object, without
// touching production code, the disk index format, or any analyzer.
//
// Not wired into IAnalyzer or the cache. Delete once the gate decision is made.

const int Unvisited = -2;
const int RootSentinel = -1;

string dumpPath = args.Length > 0
    ? args[0]
    : @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

Process proc = Process.GetCurrentProcess();
long dumpBytes = new FileInfo(dumpPath).Length;
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({dumpBytes / (1024.0 * 1024):F1} MB)");

using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
ClrHeap heap = runtime.Heap;

// ── Step 1: collect + sort object addresses (stand-in for the existing sorted
// ObjectAddresses column; production already has this for free). ──
Stopwatch collectSw = Stopwatch.StartNew();
var addressList = new List<ulong>(capacity: 4_000_000);
long enumerated = 0;
long freeObjects = 0;
foreach (ClrObject obj in heap.EnumerateObjects())
{
    enumerated++;
    if (!obj.IsValid)
        continue;
    if (obj.IsFree)
    {
        freeObjects++;
        continue;
    }
    addressList.Add(obj.Address);

    if (enumerated % 5_000_000 == 0)
        Console.WriteLine($"  [{collectSw.Elapsed.TotalSeconds:F1}s] enumerated {enumerated:N0} objects, {addressList.Count:N0} valid, {freeObjects:N0} free");
}
ulong[] addresses = addressList.ToArray();
addressList = null;
Array.Sort(addresses);
collectSw.Stop();

int n = addresses.Length;
Console.WriteLine($"Objects: {n:N0} non-free ({freeObjects:N0} free excluded) (collect+sort: {collectSw.Elapsed.TotalSeconds:F2}s, working set {proc.WorkingSet64 / (1024.0 * 1024):F0} MB)");

// ── Step 2: seed roots. ──
var parent = new int[n];
Array.Fill(parent, Unvisited);

var queue = new Queue<int>(capacity: 65536);
int rootCount = 0;
int rootsZeroAddr = 0;
int rootsUnresolved = 0;
int rootsDuplicate = 0;
int rootsSeeded = 0;
var unresolvedKinds = new Dictionary<string, int>();
foreach (ClrRoot root in heap.EnumerateRoots())
{
    rootCount++;
    ulong addr = root.Object.Address;
    if (addr == 0)
    {
        rootsZeroAddr++;
        continue;
    }

    int ord = Array.BinarySearch(addresses, addr);
    if (ord < 0)
    {
        rootsUnresolved++;
        string kind = root.RootKind.ToString();
        unresolvedKinds[kind] = unresolvedKinds.GetValueOrDefault(kind) + 1;
        continue;
    }

    if (parent[ord] == Unvisited)
    {
        parent[ord] = RootSentinel;
        queue.Enqueue(ord);
        rootsSeeded++;
    }
    else
    {
        rootsDuplicate++;
    }
}
Console.WriteLine($"Roots: {rootCount:N0} enumerated, {rootsZeroAddr:N0} zero-addr, {rootsUnresolved:N0} unresolved (not in addresses[]), {rootsDuplicate:N0} duplicate, {rootsSeeded:N0} seeded");
if (unresolvedKinds.Count > 0)
{
    Console.WriteLine("Unresolved root kinds: " + string.Join(", ", unresolvedKinds.Select(kv => $"{kv.Key}={kv.Value}")));
}

// ── Step 3: multi-source BFS, recording first-visit predecessor. ──
Stopwatch bfsSw = Stopwatch.StartNew();
long visitedEdges = 0;
long dequeued = 0;
long peakWorkingSetDuringBfs = proc.WorkingSet64;

while (queue.Count > 0)
{
    int curOrd = queue.Dequeue();
    dequeued++;

    ulong curAddr = addresses[curOrd];
    ClrObject obj = heap.GetObject(curAddr);
    if (!obj.IsValid)
        continue;

    foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
    {
        if (!reference.IsValid)
            continue;

        visitedEdges++;
        int childOrd = Array.BinarySearch(addresses, reference.Address);
        if (childOrd < 0)
            continue;

        if (parent[childOrd] == Unvisited)
        {
            parent[childOrd] = curOrd;
            queue.Enqueue(childOrd);
        }
    }

    if (dequeued % 2_000_000 == 0)
    {
        proc.Refresh();
        peakWorkingSetDuringBfs = Math.Max(peakWorkingSetDuringBfs, proc.WorkingSet64);
        Console.WriteLine($"  [{bfsSw.Elapsed.TotalSeconds:F1}s] dequeued {dequeued:N0}, queue depth {queue.Count:N0}, working set {proc.WorkingSet64 / (1024.0 * 1024):F0} MB");
    }
}
bfsSw.Stop();

proc.Refresh();
peakWorkingSetDuringBfs = Math.Max(peakWorkingSetDuringBfs, proc.WorkingSet64);

int visitedCount = 0;
var totalByGen = new Dictionary<string, int>();
var visitedByGen = new Dictionary<string, int>();
for (int i = 0; i < n; i++)
{
    bool visited = parent[i] != Unvisited;
    if (visited)
        visitedCount++;

    ClrSegment? segment = heap.GetSegmentByAddress(addresses[i]);
    string gen = segment is null
        ? "Unknown"
        : segment.Kind == Microsoft.Diagnostics.Runtime.GCSegmentKind.Ephemeral
            ? $"Gen{(int)segment.GetGeneration(addresses[i])}"
            : segment.Kind.ToString();
    totalByGen[gen] = totalByGen.GetValueOrDefault(gen) + 1;
    if (visited)
        visitedByGen[gen] = visitedByGen.GetValueOrDefault(gen) + 1;
}

Console.WriteLine();
Console.WriteLine("── Results ──────────────────────────────────────────────");
Console.WriteLine($"BFS wall time:            {bfsSw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"Total (collect+sort+BFS): {(collectSw.Elapsed + bfsSw.Elapsed).TotalSeconds:F2}s");
Console.WriteLine($"Objects total:            {n:N0}");
Console.WriteLine($"Objects visited (rooted): {visitedCount:N0} ({100.0 * visitedCount / n:F1}%)");
Console.WriteLine("By segment kind:");
foreach (var kv in totalByGen.OrderBy(kv => kv.Key))
{
    int vis = visitedByGen.GetValueOrDefault(kv.Key);
    Console.WriteLine($"  {kv.Key,-12} total={kv.Value,10:N0}  visited={vis,10:N0}  ({100.0 * vis / kv.Value:F1}%)");
}
Console.WriteLine($"Edges traversed:          {visitedEdges:N0}");
Console.WriteLine($"Peak working set:         {peakWorkingSetDuringBfs / (1024.0 * 1024):F0} MB");
Console.WriteLine($"addresses[] size:         {n * sizeof(ulong) / (1024.0 * 1024):F0} MB");
Console.WriteLine($"parent[] size:            {n * sizeof(int) / (1024.0 * 1024):F0} MB");

return 0;
