// Q1 of docs/cache/cache-ideal-design.md §10 — the GATE for O5 (overlap GC-root enumeration with
// the heap scan, worth up to 200.3 s).
//
// This does NOT measure O5's saving. It measures the one thing that decides whether O5 can exist:
// does ClrMD/the DAC let a root enumeration and a segment scan run at the same time, or does it
// serialise them? If serialised, O5 is worth zero and dies here in minutes rather than a 45-minute
// A/B of the real pipeline.
//
// EVERY ARM GETS A FRESH DataTarget. This is not optional: ClrMD caches root enumeration inside a
// runtime, so a second EnumerateRoots() in the same process returns the same roots in ~0.02 s
// against ~11.5 s for the first. Reusing one runtime across arms measures that cache, not
// concurrency — the first version of this tool did exactly that and reported a meaningless
// "349% overlap".
//
// Arms, each on its own runtime, all doing identical work:
//   0  discard  — warms the OS page cache so arm 1 is not the only one paying first-touch I/O
//   1  roots alone
//   2  scan alone, DOP 8
//   3  roots || scan, DOP 8   — the realistic configuration
//   4  roots || scan, DOP 4   — separates DAC lock serialisation from plain CPU saturation: at
//                               DOP 4 cores are free, so if roots STILL stalls it is a lock
//
// Reading the result:
//   concurrent_total ~= max(roots_alone, scan_alone)   -> independent, O5 is real
//   concurrent_total ~= roots_alone + scan_alone       -> serialised, O5 is worth zero

using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

string dumpPath = args.Length > 0
    ? args[0]
    : @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";
long maxObjects = args.Length > 1 ? long.Parse(args[1]) : long.MaxValue;

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)}  ({new FileInfo(dumpPath).Length / (1024.0 * 1024 * 1024):F2} GB)");
Console.WriteLine($"Cores: {Environment.ProcessorCount}   " +
                  $"object cap: {(maxObjects == long.MaxValue ? "none" : maxObjects.ToString("N0"))}");
Console.WriteLine("Each arm runs on a fresh DataTarget — ClrMD caches roots per runtime.");
Console.WriteLine();

Run(dumpPath, doRoots: true, doScan: true, dop: 8, maxObjects);   // arm 0, discarded

Console.WriteLine($"{"arm",-30}{"total s",10}{"roots s",10}{"scan s",10}{"roots",12}{"objects",14}");
Console.WriteLine(new string('-', 86));

var a1 = Run(dumpPath, doRoots: true, doScan: false, dop: 0, maxObjects);
Report("1  roots alone", a1);

var a2 = Run(dumpPath, doRoots: false, doScan: true, dop: 8, maxObjects);
Report("2  scan alone, DOP 8", a2);

var a3 = Run(dumpPath, doRoots: true, doScan: true, dop: 8, maxObjects);
Report("3  roots || scan, DOP 8", a3);

var a4 = Run(dumpPath, doRoots: true, doScan: true, dop: 4, maxObjects);
Report("4  roots || scan, DOP 4", a4);

// If ClrMD's serialisation is per-DacLibrary rather than global, giving the two workloads their
// own DataTarget lets them actually run at once. That costs a second DAC instance and a second
// mapping of the dump, so it is only worth knowing about if it works.
var a5 = RunSplitRuntimes(dumpPath, dop: 8, maxObjects);
Report("5  roots || scan, SPLIT targets", a5);

Console.WriteLine();
if (a1.RootCount != a3.RootCount || a1.RootCount != a4.RootCount)
    Console.WriteLine($"  *** root counts differ across arms ({a1.RootCount}/{a3.RootCount}/{a4.RootCount}) — arms are not comparable");
if (a2.ObjectCount != a3.ObjectCount)
    Console.WriteLine($"  *** object counts differ ({a2.ObjectCount:N0} vs {a3.ObjectCount:N0}) — arms are not comparable");

double serial = a1.Roots + a2.Scan;
double ideal = Math.Max(a1.Roots, a2.Scan);
Console.WriteLine($"  serialised would be   {serial,8:F2} s   (roots alone + scan alone)");
Console.WriteLine($"  fully concurrent      {ideal,8:F2} s   (max of the two)");
Console.WriteLine($"  measured, DOP 8       {a3.Total,8:F2} s");
Console.WriteLine($"  measured, DOP 4       {a4.Total,8:F2} s");
Console.WriteLine($"  measured, split       {a5.Total,8:F2} s   (separate DataTarget per workload)");
Console.WriteLine();

double span = serial - ideal;
if (span > 0.01)
{
    Console.WriteLine($"  overlap achieved, DOP 8: {100.0 * (serial - a3.Total) / span,6:F1} %   " +
                      $"(100% = fully concurrent, 0% = fully serialised)");
    Console.WriteLine($"  overlap achieved, DOP 4: {100.0 * (serial - a4.Total) / span,6:F1} %");
    Console.WriteLine($"  overlap achieved, split:  {100.0 * (serial - a5.Total) / span,6:F1} %");
    Console.WriteLine($"  wall clock saved vs serial, DOP 8: {serial - a3.Total,6:F2} s   split: {serial - a5.Total,6:F2} s");
}
Console.WriteLine($"  root enumeration slowdown while scanning: " +
                  $"DOP 8 {a3.Roots / a1.Roots,5:F2}x,  DOP 4 {a4.Roots / a1.Roots,5:F2}x");
Console.WriteLine($"  scan slowdown while enumerating roots:    " +
                  $"DOP 8 {a3.Scan / a2.Scan,5:F2}x,  DOP 4 {a4.Scan / a2.Scan,5:F2}x");

foreach (var (n, a) in new[] { (1, a1), (2, a2), (3, a3), (4, a4), (5, a5) })
    if (a.Error is not null) Console.WriteLine($"\n  *** arm {n} error: {a.Error}");

return 0;

static Arm Run(string dumpPath, bool doRoots, bool doScan, int dop, long maxObjects)
{
    using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
    ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
    ClrHeap heap = runtime.Heap;

    // Resolve segments before the clock starts — ClrHeap.Segments is lazily built by ClrMD on
    // first access and would otherwise be charged to whichever arm touches it first.
    ClrSegment[] segments = heap.Segments.ToArray();

    long rootCount = 0, objectCount = 0;
    double rootsSec = 0, scanSec = 0;
    string? error = null;

    var total = Stopwatch.StartNew();

    Thread? rootThread = null;
    if (doRoots)
    {
        rootThread = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                long n = 0;
                foreach (ClrRoot root in heap.EnumerateRoots()) n++;
                Interlocked.Add(ref rootCount, n);
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref error, $"roots: {ex.GetType().Name}: {ex.Message}", null); }
            sw.Stop();
            rootsSec = sw.Elapsed.TotalSeconds;
        }) { IsBackground = true };
        rootThread.Start();
    }

    if (doScan)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            Parallel.For(0, segments.Length, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
            {
                long local = 0;
                foreach (ClrObject obj in segments[i].EnumerateObjects())
                {
                    // Touching Type is the per-object DAC/metadata work the real scan does.
                    if (obj.IsValid && obj.Type is not null) local++;
                    if ((local & 65535) == 0 && Interlocked.Read(ref objectCount) + local >= maxObjects) break;
                }
                Interlocked.Add(ref objectCount, local);
            });
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref error, $"scan: {ex.GetType().Name}: {ex.Message}", null); }
        sw.Stop();
        scanSec = sw.Elapsed.TotalSeconds;
    }

    rootThread?.Join();
    total.Stop();

    return new Arm(total.Elapsed.TotalSeconds, rootsSec, scanSec, rootCount, objectCount, error);
}

// Same workloads as Run's concurrent case, but each on its own DataTarget/ClrRuntime/ClrHeap.
static Arm RunSplitRuntimes(string dumpPath, int dop, long maxObjects)
{
    using DataTarget rootTarget = DataTarget.LoadDump(dumpPath);
    using DataTarget scanTarget = DataTarget.LoadDump(dumpPath);
    ClrHeap rootHeap = rootTarget.ClrVersions[0].CreateRuntime().Heap;
    ClrHeap scanHeap = scanTarget.ClrVersions[0].CreateRuntime().Heap;
    ClrSegment[] segments = scanHeap.Segments.ToArray();
    _ = rootHeap.Segments.Length;

    long rootCount = 0, objectCount = 0;
    double rootsSec = 0, scanSec = 0;
    string? error = null;

    var total = Stopwatch.StartNew();

    var rootThread = new Thread(() =>
    {
        var sw = Stopwatch.StartNew();
        try
        {
            long n = 0;
            foreach (ClrRoot root in rootHeap.EnumerateRoots()) n++;
            Interlocked.Add(ref rootCount, n);
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref error, $"roots: {ex.GetType().Name}: {ex.Message}", null); }
        sw.Stop();
        rootsSec = sw.Elapsed.TotalSeconds;
    }) { IsBackground = true };
    rootThread.Start();

    var scanSw = Stopwatch.StartNew();
    try
    {
        Parallel.For(0, segments.Length, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
        {
            long local = 0;
            foreach (ClrObject obj in segments[i].EnumerateObjects())
            {
                if (obj.IsValid && obj.Type is not null) local++;
                if ((local & 65535) == 0 && Interlocked.Read(ref objectCount) + local >= maxObjects) break;
            }
            Interlocked.Add(ref objectCount, local);
        });
    }
    catch (Exception ex) { Interlocked.CompareExchange(ref error, $"scan: {ex.GetType().Name}: {ex.Message}", null); }
    scanSw.Stop();
    scanSec = scanSw.Elapsed.TotalSeconds;

    rootThread.Join();
    total.Stop();
    return new Arm(total.Elapsed.TotalSeconds, rootsSec, scanSec, rootCount, objectCount, error);
}

static void Report(string label, Arm a) =>
    Console.WriteLine($"{label,-30}{a.Total,10:F2}{a.Roots,10:F2}{a.Scan,10:F2}{a.RootCount,12:N0}{a.ObjectCount,14:N0}");

record struct Arm(double Total, double Roots, double Scan, long RootCount, long ObjectCount, string? Error);
