using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;

// Investigation 6: Concurrent Query Throughput
// Validates whether per-bucket locking scales well with 10-50 concurrent threads.
// Measures throughput degradation and lock contention impact.

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: ConcurrentThroughputValidator <dump-path> [queries-per-thread]");
    return 1;
}

string dumpPath = args[0];
int queriesPerThread = args.Length > 1 ? int.Parse(args[1]) : 1000;

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var fileInfo = new FileInfo(dumpPath);
Console.WriteLine($"\n{'='*70}");
Console.WriteLine($"Investigation 6: Concurrent Query Throughput");
Console.WriteLine($"{'='*70}");
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB)");
Console.WriteLine($"Queries per thread: {queriesPerThread:N0}");
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

    // PHASE A: Extract edges and build hash-partitioned index with locks
    Console.WriteLine("\n--- PHASE A: Extract Edges (Hash-Partitioned) ---");

    double dumpGb = fileInfo.Length / (1024.0 * 1024 * 1024);
    double dumpMb = dumpGb * 1024;
    int bucketCount = Math.Max(1, (int)Math.Ceiling(dumpMb / 500.0));

    Console.WriteLine($"Buckets: {bucketCount}");

    // Fnv1a64 hash for bucketing
    var buckets = Enumerable.Range(0, bucketCount)
        .Select(_ => new BucketData())
        .ToArray();

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
                    int bucketIdx = GetBucketIndex(refObj.Address, bucketCount);
                    var bucket = buckets[bucketIdx];

                    lock (bucket.Lock)
                    {
                        if (!bucket.ChildToParents.ContainsKey(refObj.Address))
                            bucket.ChildToParents[refObj.Address] = new List<ulong>();
                        bucket.ChildToParents[refObj.Address].Add(obj.Address);
                    }

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
    Console.WriteLine($"Extraction time: {extractSw.Elapsed.TotalSeconds:F2}s");

    // Select random queries
    var allChildren = new List<ulong>();
    foreach (var bucket in buckets)
    {
        lock (bucket.Lock)
        {
            allChildren.AddRange(bucket.ChildToParents.Keys);
        }
    }

    if (allChildren.Count == 0)
    {
        Console.WriteLine("No children in index");
        dt.Dispose();
        return 1;
    }

    var random = new Random(42);
    var queryChildren = new ulong[queriesPerThread * 50]; // Max 50 threads
    for (int i = 0; i < queryChildren.Length; i++)
        queryChildren[i] = allChildren[random.Next(allChildren.Count)];

    // PHASE B: Concurrent throughput at varying thread counts
    Console.WriteLine("\n--- PHASE B: Throughput Scaling ---");
    Console.WriteLine($"{'Threads',-8} {'Queries',-10} {'Time (s)',-8} {'Throughput',-12} {'Scaling',-8}");
    Console.WriteLine(new string('-', 56));

    long baselineThroughput = 0;
    var scalingResults = new List<(int threads, long throughput, double scaling)>();

    foreach (int threadCount in new[] { 1, 5, 10, 25, 50 })
    {
        int actualQueries = Math.Min(queriesPerThread, queryChildren.Length / threadCount);
        int totalQueries = threadCount * actualQueries;

        var concurrentSw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, threadCount)
            .Select(t => Task.Run(() =>
            {
                for (int i = 0; i < actualQueries; i++)
                {
                    var child = queryChildren[random.Next(queryChildren.Length)];
                    int bucketIdx = GetBucketIndex(child, bucketCount);
                    var bucket = buckets[bucketIdx];

                    lock (bucket.Lock)
                    {
                        if (bucket.ChildToParents.TryGetValue(child, out var parents))
                        {
                            var count = parents.Count;
                        }
                    }
                }
            }))
            .ToArray();

        Task.WaitAll(tasks);
        concurrentSw.Stop();

        long throughput = (long)(totalQueries / concurrentSw.Elapsed.TotalSeconds);
        double scaling = baselineThroughput > 0 ? throughput / (double)baselineThroughput : 1.0;

        if (threadCount == 1)
            baselineThroughput = throughput;

        scalingResults.Add((threadCount, throughput, scaling));

        Console.WriteLine($"{threadCount,-8} {totalQueries,-10:N0} {concurrentSw.Elapsed.TotalSeconds,-8:F2} {throughput,-12:N0} {scaling,-8:F2}x");
    }

    // Analyze scaling behavior
    Console.WriteLine("\n--- Scaling Analysis ---");
    var idealLinear = scalingResults.Select(r => r.throughput >= baselineThroughput * r.threads * 0.95).ToList();
    var scalingQuality = idealLinear.Count(x => x) / (double)idealLinear.Count * 100;

    Console.WriteLine($"Linear scaling efficiency: {scalingQuality:F1}%");

    var atFiftyThreads = scalingResults.FirstOrDefault(r => r.threads == 50);
    if (atFiftyThreads != default)
    {
        Console.WriteLine($"Throughput at 50 threads: {atFiftyThreads.throughput:N0} qps (target: >5K)");
        Console.WriteLine($"Scaling degradation: {100 - (atFiftyThreads.scaling * 100 / 50):F1}%");
    }

    // DECISION
    Console.WriteLine("\n--- Decision ---");
    var p10Throughput = scalingResults.FirstOrDefault(r => r.threads == 10);
    var p50Throughput = scalingResults.FirstOrDefault(r => r.threads == 50);

    bool pass10K = p10Throughput != default && p10Throughput.throughput > 10_000;
    bool pass5K = p50Throughput != default && p50Throughput.throughput > 5_000;

    string decision;
    if (pass10K && pass5K && scalingQuality > 80)
    {
        decision = "✅ PASS";
        Console.ForegroundColor = ConsoleColor.Green;
    }
    else if ((pass10K || pass5K) && scalingQuality > 60)
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
    if (p10Throughput != default)
        Console.WriteLine($"  10 threads: {p10Throughput.throughput:N0} qps (target: >10K)");
    if (p50Throughput != default)
        Console.WriteLine($"  50 threads: {p50Throughput.throughput:N0} qps (target: >5K)");
    Console.WriteLine($"  Scaling quality: {scalingQuality:F1}%");
    Console.ResetColor();

    dt.Dispose();
    Console.WriteLine($"\n{'='*70}\n");

    return (pass10K && pass5K && scalingQuality > 80) ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}

// Helper class for bucket data
class BucketData
{
    public readonly object Lock = new object();
    public readonly Dictionary<ulong, List<ulong>> ChildToParents = new();
}

// Simple Fnv1a64 hash
static int GetBucketIndex(ulong value, int bucketCount)
{
    const ulong FnvPrime = 1099511628211UL;
    const ulong FnvOffset = 14695981039346656037UL;

    ulong hash = FnvOffset;
    for (int i = 0; i < 8; i++)
    {
        hash ^= (value >> (i * 8)) & 0xFF;
        hash *= FnvPrime;
    }

    return (int)(hash % (ulong)bucketCount);
}
