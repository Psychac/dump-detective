using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

// Throwaway benchmark for the §4 gate in
// docs/analysis/phase1-redesigns/root-path-finder.md — measures a level-synchronous,
// multi-runtime parallel BFS from GC roots (one independent ClrRuntime per worker,
// atomic CAS on a shared parent[] column) against the same single-threaded design in
// tools/ProfileRootPathBackfill. Not wired into IAnalyzer or the cache. Delete once the
// gate decision is made.

const int Unvisited = -2;
const int RootSentinel = -1;

string dumpPath = args.Length > 0
    ? args[0]
    : @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";
int workerCount = args.Length > 1 ? int.Parse(args[1]) : 4;

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

Process proc = Process.GetCurrentProcess();
long dumpBytes = new FileInfo(dumpPath).Length;
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({dumpBytes / (1024.0 * 1024):F1} MB), workers={workerCount}");

var options = new DataTargetOptions { UseLockFreeMemoryMapReader = true };

// ── Open one runtime per worker up front (validated in MultiRuntimeCheck: independent
// DataTarget/ClrRuntime instances over the same dump file are what actually parallelizes —
// threading a single shared ClrRuntime does not, due to internal DAC-call serialization). ──
Stopwatch openSw = Stopwatch.StartNew();
var runtimes = new (DataTarget dt, ClrRuntime rt, ClrHeap heap)[workerCount];
for (int w = 0; w < workerCount; w++)
{
    DataTarget dt = DataTarget.LoadDump(dumpPath, options);
    ClrRuntime rt = dt.ClrVersions[0].CreateRuntime();
    runtimes[w] = (dt, rt, rt.Heap);
}
openSw.Stop();
Console.WriteLine($"Opened {workerCount} independent runtimes in {openSw.Elapsed.TotalSeconds:F2}s");

ClrHeap heap0 = runtimes[0].heap;

// ── Step 1: collect + sort object addresses (single-threaded, same as sequential tool). ──
Stopwatch collectSw = Stopwatch.StartNew();
var addressList = new List<ulong>(capacity: 4_000_000);
long enumerated = 0;
long freeObjects = 0;
foreach (ClrObject obj in heap0.EnumerateObjects())
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

// ── Step 2: seed roots (single-threaded, same as sequential tool). ──
var parent = new int[n];
Array.Fill(parent, Unvisited);

var initialFrontier = new List<int>(capacity: 65536);
int rootCount = 0;
int rootsZeroAddr = 0;
int rootsUnresolved = 0;
int rootsDuplicate = 0;
int rootsSeeded = 0;
var unresolvedKinds = new Dictionary<string, int>();
foreach (ClrRoot root in heap0.EnumerateRoots())
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
        initialFrontier.Add(ord);
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

// ── Step 3: level-synchronous parallel BFS. Each worker owns its own ClrRuntime/ClrHeap
// and processes a contiguous slice of the current frontier; first-visit ownership of a
// child ordinal is decided by an atomic CAS on parent[childOrd], so concurrent discovery
// from multiple parents in the same level is race-free without a shared queue. ──
Stopwatch bfsSw = Stopwatch.StartNew();
long visitedEdges = 0;
long totalDequeued = 0;
int levels = 0;
long peakWorkingSetDuringBfs = proc.WorkingSet64;

int[] currentFrontier = initialFrontier.ToArray();
initialFrontier = null;

while (currentFrontier.Length > 0)
{
    levels++;
    totalDequeued += currentFrontier.Length;

    var nextFrontierPerWorker = new List<int>[workerCount];
    var edgesPerWorker = new long[workerCount];

    int chunkSize = (currentFrontier.Length + workerCount - 1) / workerCount;
    Parallel.For(0, workerCount, w =>
    {
        ClrHeap myHeap = runtimes[w].heap;
        var localNext = new List<int>();
        long localEdges = 0;

        int start = w * chunkSize;
        int end = Math.Min(start + chunkSize, currentFrontier.Length);
        for (int i = start; i < end; i++)
        {
            int curOrd = currentFrontier[i];
            ulong curAddr = addresses[curOrd];
            ClrObject obj = myHeap.GetObject(curAddr);
            if (!obj.IsValid)
                continue;

            foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
            {
                if (!reference.IsValid)
                    continue;

                localEdges++;
                int childOrd = Array.BinarySearch(addresses, reference.Address);
                if (childOrd < 0)
                    continue;

                if (Interlocked.CompareExchange(ref parent[childOrd], curOrd, Unvisited) == Unvisited)
                    localNext.Add(childOrd);
            }
        }

        nextFrontierPerWorker[w] = localNext;
        edgesPerWorker[w] = localEdges;
    });

    long levelEdges = 0;
    int nextSize = 0;
    for (int w = 0; w < workerCount; w++)
    {
        levelEdges += edgesPerWorker[w];
        nextSize += nextFrontierPerWorker[w].Count;
    }
    visitedEdges += levelEdges;

    var nextFrontier = new int[nextSize];
    int pos = 0;
    for (int w = 0; w < workerCount; w++)
    {
        nextFrontierPerWorker[w].CopyTo(nextFrontier, pos);
        pos += nextFrontierPerWorker[w].Count;
    }

    proc.Refresh();
    peakWorkingSetDuringBfs = Math.Max(peakWorkingSetDuringBfs, proc.WorkingSet64);
    Console.WriteLine($"  [{bfsSw.Elapsed.TotalSeconds:F1}s] level {levels}, frontier {currentFrontier.Length:N0} -> {nextSize:N0}, dequeued {totalDequeued:N0}, working set {proc.WorkingSet64 / (1024.0 * 1024):F0} MB");

    currentFrontier = nextFrontier;
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

    ClrSegment? segment = heap0.GetSegmentByAddress(addresses[i]);
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
Console.WriteLine($"Runtimes opened:          {workerCount} in {openSw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"BFS levels:               {levels:N0}");
Console.WriteLine($"BFS wall time:            {bfsSw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"Total (open+collect+sort+BFS): {(openSw.Elapsed + collectSw.Elapsed + bfsSw.Elapsed).TotalSeconds:F2}s");
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

foreach (var (dt, rt, _) in runtimes)
{
    rt.Dispose();
    dt.Dispose();
}

return 0;
