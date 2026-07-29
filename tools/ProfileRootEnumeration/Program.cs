using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;

string dumpPath = args.Length > 0
    ? args[0]
    : @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

string indexDir = DumpIndexPaths.GetIndexDirectory(dumpPath);
string backupDir = indexDir + ".profilebak";
bool movedExisting = false;
if (Directory.Exists(indexDir))
{
    if (Directory.Exists(backupDir))
        Directory.Delete(backupDir, recursive: true);
    Directory.Move(indexDir, backupDir);
    movedExisting = true;
}

try
{
    using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
    ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
    ClrHeap heap = runtime.Heap;

    long dumpBytes = new FileInfo(dumpPath).Length;
    DumpSizeTier sizeTier = dumpBytes > 4L * 1024 * 1024 * 1024 ? DumpSizeTier.Large :
                            dumpBytes > 512L * 1024 * 1024 ? DumpSizeTier.Medium :
                            DumpSizeTier.Small;

    Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({dumpBytes / (1024.0 * 1024):F1} MB, tier={sizeTier})");
    Console.WriteLine($"Thread count: {runtime.Threads.Count()}");
    Console.WriteLine($"Heap segment count: {heap.Segments.Length}");

    var progress = new Progress<AnalyzerProgressReport>(r =>
        Console.WriteLine($"[{r.Elapsed?.TotalSeconds ?? 0:F2}s] {r.Phase}"));

    var writer = new DiskBackedObjectIndexWriter();

    Stopwatch totalSw = Stopwatch.StartNew();
    HeapIndexBuildResult result = writer.Build(heap, CancellationToken.None, progress, dumpPath, sizeTier);
    totalSw.Stop();

    Console.WriteLine($"Objects indexed: {result.ObjectCount:N0}");
    Console.WriteLine($"TOTAL cold build: {totalSw.Elapsed.TotalSeconds:F2}s");
    return 0;
}
finally
{
    if (Directory.Exists(indexDir))
        Directory.Delete(indexDir, recursive: true);
    if (movedExisting)
        Directory.Move(backupDir, indexDir);
}
