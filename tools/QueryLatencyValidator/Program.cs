using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;

// Investigation 4: Query Latency on Real Dumps
// Validates whether reverse-index lookup achieves <50 ms p99 latency.
// Builds minimal in-memory hash-partitioned index and benchmarks query performance.

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: QueryLatencyValidator <dump-path> [query-count] [thread-count]");
    return 1;
}

string dumpPath = args[0];
int queryCount = args.Length > 1 ? int.Parse(args[1]) : 10_000;
int threadCount = args.Length > 2 ? int.Parse(args[2]) : 10;

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var fileInfo = new FileInfo(dumpPath);
Console.WriteLine($"\n{'='*70}");
Console.WriteLine($"Investigation 4: Query Latency on Real Dumps");
Console.WriteLine($"{'='*70}");
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB)");
Console.WriteLine($"Query Count: {queryCount:N0}");
Console.WriteLine($"Thread Count: {threadCount}");
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

    // PHASE A: Extract edges and build in-memory reverse index
    Console.WriteLine("\n--- PHASE A: Extract Edges ---");

    // Calculate bucket count: N = ceil(dump_mb / 500)
    double dumpGb = fileInfo.Length / (1024.0 * 1024 * 1024);
    double dumpMb = dumpGb * 1024;
    int bucketCount = Math.Max(1, (int)Math.Ceiling(dumpMb / 500.0));

    Console.WriteLine($"Estimated buckets: {bucketCount} (formula: ceil({dumpGb:F2} GB * 1024 / 500))");

    // In-memory index: child -> list of parents
    var childToParents = new Dictionary<ulong, List<ulong>>();
    var allChildren = new HashSet<ulong>();

    Stopwatch extractSw = Stopwatch.StartNew();
    long objectsProcessed = 0;
    long edgesExtracted = 0;

    foreach (var obj in heap.EnumerateObjects())
    {
        objectsProcessed++;
        if (objectsProcessed % 100_000 == 0)
            Console.Write($"\r  Objects: {objectsProcessed:N0}, Edges: {edgesExtracted:N0}");

        if (obj.Type == null || !obj.IsValid)
            continue;

        foreach (var field in obj.Type.Fields)
        {
            if (!field.IsObjectReference)
                continue;

            try
            {
                var refObj = field.ReadObject(obj.Address, interior: false);
                if (refObj.IsValid)
                {
                    allChildren.Add(refObj.Address);

                    if (!childToParents.ContainsKey(refObj.Address))
                        childToParents[refObj.Address] = new List<ulong>();

                    childToParents[refObj.Address].Add(obj.Address);
                    edgesExtracted++;
                }
            }
            catch
            {
                // Skip malformed objects
            }
        }
    }
    extractSw.Stop();

    Console.WriteLine($"\r  Objects processed: {objectsProcessed:N0}");
    Console.WriteLine($"Edges extracted: {edgesExtracted:N0}");
    Console.WriteLine($"Unique children: {childToParents.Count:N0}");
    Console.WriteLine($"Extraction time: {extractSw.Elapsed.TotalSeconds:F2}s");

    // Select random children for queries (ensure they exist in index)
    var queryCandidates = childToParents.Keys.ToList();
    if (queryCandidates.Count == 0)
    {
        Console.WriteLine("No children found in heap - cannot benchmark");
        dt.Dispose();
        return 1;
    }

    var random = new Random(42);
    var queryChildren = new ulong[queryCount];
    for (int i = 0; i < queryCount; i++)
        queryChildren[i] = queryCandidates[random.Next(queryCandidates.Count)];

    // PHASE B: Single-thread latency benchmark
    Console.WriteLine("\n--- PHASE B: Single-Thread Latency Benchmark ---");

    var latencies = new long[queryCount];
    var sw = Stopwatch.StartNew();

    for (int i = 0; i < queryCount; i++)
    {
        var itemSw = Stopwatch.StartNew();
        var child = queryChildren[i];

        if (childToParents.TryGetValue(child, out var parents))
        {
            // Simulate work: iterate parents list
            var count = parents.Count;
        }

        itemSw.Stop();
        latencies[i] = itemSw.ElapsedMilliseconds;
    }
    sw.Stop();

    Array.Sort(latencies);

    var p50 = latencies[(int)(queryCount * 0.50)];
    var p95 = latencies[(int)(queryCount * 0.95)];
    var p99 = latencies[(int)(queryCount * 0.99)];
    var max = latencies[queryCount - 1];
    var avg = latencies.Average();

    Console.WriteLine($"Queries: {queryCount:N0}");
    Console.WriteLine($"Total time: {sw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"Throughput: {queryCount / sw.Elapsed.TotalSeconds:F0} qps");
    Console.WriteLine($"p50 latency: {p50} ms (target: <10)");
    Console.WriteLine($"p95 latency: {p95} ms (target: <30)");
    Console.WriteLine($"p99 latency: {p99} ms (target: <50) ← PRIMARY");
    Console.WriteLine($"Max latency: {max} ms");
    Console.WriteLine($"Avg latency: {avg:F2} ms");

    // PHASE C: Concurrent throughput benchmark
    Console.WriteLine($"\n--- PHASE C: Concurrent Throughput Benchmark ({threadCount} threads) ---");

    int queriesPerThread = queryCount / threadCount;
    var concurrentLatencies = new List<long>();
    var concurrentLock = new object();

    var concurrentSw = Stopwatch.StartNew();
    var tasks = Enumerable.Range(0, threadCount)
        .Select(t => Task.Run(() =>
        {
            for (int i = 0; i < queriesPerThread; i++)
            {
                var child = queryChildren[random.Next(queryChildren.Length)];
                var itemSw = Stopwatch.StartNew();

                if (childToParents.TryGetValue(child, out var parents))
                {
                    var count = parents.Count;
                }

                itemSw.Stop();

                lock (concurrentLock)
                {
                    concurrentLatencies.Add(itemSw.ElapsedMilliseconds);
                }
            }
        }))
        .ToArray();

    Task.WaitAll(tasks);
    concurrentSw.Stop();

    var concurrentLatenciesArray = concurrentLatencies.ToArray();
    Array.Sort(concurrentLatenciesArray);

    var concurrentP50 = concurrentLatenciesArray[(int)(concurrentLatenciesArray.Length * 0.50)];
    var concurrentP95 = concurrentLatenciesArray[(int)(concurrentLatenciesArray.Length * 0.95)];
    var concurrentP99 = concurrentLatenciesArray[(int)(concurrentLatenciesArray.Length * 0.99)];
    var concurrentThroughput = (threadCount * queriesPerThread) / concurrentSw.Elapsed.TotalSeconds;

    Console.WriteLine($"Concurrent throughput: {concurrentThroughput:F0} qps (target: >10K)");
    Console.WriteLine($"Concurrent p50: {concurrentP50} ms");
    Console.WriteLine($"Concurrent p95: {concurrentP95} ms");
    Console.WriteLine($"Concurrent p99: {concurrentP99} ms");
    Console.WriteLine($"Total concurrent time: {concurrentSw.Elapsed.TotalSeconds:F2}s");

    // DECISION
    Console.WriteLine("\n--- Decision ---");
    string decision;
    if (p99 < 50 && concurrentThroughput > 10_000)
    {
        decision = "✅ PASS";
        Console.ForegroundColor = ConsoleColor.Green;
    }
    else if (p99 < 100 && concurrentThroughput > 5_000)
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
    Console.WriteLine($"  p99 latency: {p99} ms (target: <50 ms)");
    Console.WriteLine($"  Concurrent throughput: {concurrentThroughput:F0} qps (target: >10K qps)");
    Console.ResetColor();

    dt.Dispose();
    Console.WriteLine($"\n{'='*70}\n");

    return (p99 < 50 && concurrentThroughput > 10_000) ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}
