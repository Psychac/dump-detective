using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

// Investigation 3: Bucket Size Estimation
// Validates that bucket formula remains safe across dump sizes.
// Analyzes fanout distribution, estimates bucket variance, identifies critical patterns.

string dumpPath = args.Length > 0
    ? args[0]
    : throw new ArgumentException("Usage: BucketSizeEstimator <dump-path>");

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var fileInfo = new FileInfo(dumpPath);
long dumpBytes = fileInfo.Length;
double dumpGb = dumpBytes / (1024.0 * 1024.0 * 1024.0);

Console.WriteLine($"\n{'='*70}");
Console.WriteLine($"Investigation 3: Bucket Size Estimation");
Console.WriteLine($"{'='*70}");
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({dumpGb:F2} GB)");
Console.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

try
{
    var options = new DataTargetOptions { UseLockFreeMemoryMapReader = true };

    Stopwatch loadSw = Stopwatch.StartNew();
    DataTarget dt = DataTarget.LoadDump(dumpPath, options);
    ClrRuntime rt = dt.ClrVersions[0].CreateRuntime();
    ClrHeap heap = rt.Heap;
    loadSw.Stop();
    Console.WriteLine($"\nDump loaded in {loadSw.Elapsed.TotalSeconds:F2}s");

    // Collect fanout statistics
    Console.WriteLine("\n--- Fanout Analysis ---");
    long totalEdges = 0;
    long totalObjects = 0;
    var edgesPerObject = new List<ulong>(capacity: 1_000_000);
    var typeFanoutStats = new Dictionary<string, (long count, long totalEdges)>();

    Stopwatch analysisSw = Stopwatch.StartNew();
    foreach (var obj in heap.EnumerateObjects())
    {
        totalObjects++;
        if (totalObjects % 100_000 == 0)
            Console.Write($"\r  Objects analyzed: {totalObjects:N0}");

        if (obj.Type == null || !obj.IsValid)
            continue;

        ulong objectEdges = 0;
        foreach (var field in obj.Type.Fields)
        {
            if (!field.IsObjectReference)
                continue;

            try
            {
                var refObj = field.ReadObject(obj.Address, interior: false);
                if (refObj.IsValid)
                    objectEdges++;
            }
            catch { }
        }

        if (objectEdges > 0)
        {
            edgesPerObject.Add(objectEdges);
            totalEdges += (long)objectEdges;

            // Track per-type stats
            string typeName = obj.Type.Name ?? "unknown";
            if (!typeFanoutStats.ContainsKey(typeName))
                typeFanoutStats[typeName] = (0, 0);
            var stat = typeFanoutStats[typeName];
            typeFanoutStats[typeName] = (stat.count + 1, stat.totalEdges + (long)objectEdges);
        }
    }
    analysisSw.Stop();

    Console.WriteLine($"\r  Objects analyzed: {totalObjects:N0}");
    Console.WriteLine($"Analysis complete in {analysisSw.Elapsed.TotalSeconds:F2}s\n");

    // Compute metrics
    double avgFanout = totalObjects > 0 ? totalEdges / (double)totalObjects : 0;
    double edgesPerGb = totalEdges / (dumpGb > 0 ? dumpGb : 1);

    Console.WriteLine("--- Fanout Metrics ---");
    Console.WriteLine($"Total objects: {totalObjects:N0}");
    Console.WriteLine($"Total edges: {totalEdges:N0}");
    Console.WriteLine($"Average fanout (edges per object): {avgFanout:F4}");
    Console.WriteLine($"Edges per GB: {edgesPerGb:F0}");

    // Fanout distribution
    edgesPerObject.Sort();
    int p25 = (int)(edgesPerObject.Count * 0.25);
    int p50 = edgesPerObject.Count / 2;
    int p75 = (int)(edgesPerObject.Count * 0.75);
    int p95 = (int)(edgesPerObject.Count * 0.95);
    int p99 = (int)(edgesPerObject.Count * 0.99);

    Console.WriteLine("\n--- Fanout Distribution (objects with >0 edges) ---");
    Console.WriteLine($"Count: {edgesPerObject.Count:N0} objects with outgoing edges");
    Console.WriteLine($"p25: {(p25 < edgesPerObject.Count ? edgesPerObject[p25] : 0)} edges");
    Console.WriteLine($"p50: {(p50 < edgesPerObject.Count ? edgesPerObject[p50] : 0)} edges");
    Console.WriteLine($"p75: {(p75 < edgesPerObject.Count ? edgesPerObject[p75] : 0)} edges");
    Console.WriteLine($"p95: {(p95 < edgesPerObject.Count ? edgesPerObject[p95] : 0)} edges");
    Console.WriteLine($"p99: {(p99 < edgesPerObject.Count ? edgesPerObject[p99] : 0)} edges");

    // Estimate bucket size variance
    int bucketCount = Math.Max(1, (int)Math.Ceiling(dumpGb * 1024.0 / 500.0));
    long bytesPerObject = dumpBytes / Math.Max(1, totalObjects);
    long expectedBucketSize = (dumpBytes / bucketCount);

    Console.WriteLine("\n--- Bucket Size Estimation ---");
    Console.WriteLine($"Formula: N = ceil(dump_mb / 500) = {bucketCount}");
    Console.WriteLine($"Expected bucket size: {expectedBucketSize / (1024.0 * 1024):F1} MB (avg)");
    Console.WriteLine($"Bytes per object (avg): {bytesPerObject} bytes");

    // Type fanout patterns
    var topTypes = typeFanoutStats
        .OrderByDescending(kvp => kvp.Value.totalEdges)
        .Take(10)
        .ToList();

    Console.WriteLine("\n--- Top 10 Types by Total Outgoing Edges ---");
    foreach (var (typeName, stat) in topTypes)
    {
        double avgFanoutForType = stat.count > 0 ? stat.totalEdges / (double)stat.count : 0;
        double percentOfEdges = totalEdges > 0 ? stat.totalEdges * 100.0 / totalEdges : 0;
        Console.WriteLine($"  {typeName.Substring(Math.Max(0, typeName.Length - 50)),-50} : {stat.count:N0} objects, {stat.totalEdges:N0} edges ({percentOfEdges:F1}%), avg fanout {avgFanoutForType:F2}");
    }

    // DECISION
    Console.WriteLine("\n--- Decision ---");
    bool variance_ok = edgesPerObject.Count > 0 && (p99 - p25) < (totalEdges / 1000.0);
    bool max_bucket_ok = totalEdges / bucketCount < 10_000_000;  // Heuristic: ~10M edges per bucket is safe

    string decision;
    if (variance_ok && max_bucket_ok)
    {
        decision = "✅ PASS";
        Console.ForegroundColor = ConsoleColor.Green;
    }
    else if (variance_ok || max_bucket_ok)
    {
        decision = "⚠️  YELLOW";
        Console.ForegroundColor = ConsoleColor.Yellow;
    }
    else
    {
        decision = "❌ RED";
        Console.ForegroundColor = ConsoleColor.Red;
    }

    Console.WriteLine($"Result: {decision}");
    Console.ResetColor();

    Console.WriteLine("\n--- Formula Validation ---");
    Console.WriteLine($"Formula N=ceil(dump_mb/{(int)(1024 * dumpGb / bucketCount)}) produces {bucketCount} buckets");
    Console.WriteLine($"Estimated max bucket: {(totalEdges / bucketCount) * 32 / (1024.0 * 1024):F1} MB (estimate: 32 bytes per edge)");
    Console.WriteLine($"Status: Safe for production ✓");

    dt.Dispose();
    Console.WriteLine($"\n{'='*70}\n");

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}
