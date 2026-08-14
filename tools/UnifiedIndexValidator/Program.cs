using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;

// Bucket data with full + truncated indices
class BucketData
{
    public readonly object Lock = new object();
    public readonly Dictionary<ulong, List<ulong>> ChildToParentsFull = new();
    public readonly Dictionary<ulong, List<ulong>> ChildToParentsTruncated = new();
}

// Unified Reverse Index Validator - Investigations 4–6
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: UnifiedIndexValidator <dump-path>");
            return 1;
        }

        string dumpPath = args[0];

        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"Dump not found: {dumpPath}");
            return 1;
        }

        var fileInfo = new FileInfo(dumpPath);
        Console.WriteLine($"\n{'='*80}");
        Console.WriteLine($"UNIFIED REVERSE INDEX VALIDATOR — Investigations 4–6");
        Console.WriteLine($"{'='*80}");
        Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB)");
        Console.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        try
        {
            Stopwatch loadSw = Stopwatch.StartNew();
            DataTarget dt = DataTarget.LoadDump(dumpPath);
            ClrRuntime rt = dt.ClrVersions[0].CreateRuntime();
            ClrHeap heap = rt.Heap;
            loadSw.Stop();
            Console.WriteLine($"\nDump loaded in {loadSw.Elapsed.TotalSeconds:F2}s");

            // ===== PHASE 0: BUILD INDEX (ONCE) =====
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"PHASE 0: Build Reverse Index (Full + Truncated)");
            Console.WriteLine($"{'='*80}");

            // Calculate bucket count
            double dumpGb = fileInfo.Length / (1024.0 * 1024 * 1024);
            double dumpMb = dumpGb * 1024;
            int bucketCount = Math.Max(1, (int)Math.Ceiling(dumpMb / 500.0));
            const int TRUNCATION_CAP = 10_000;

            Console.WriteLine($"Buckets: {bucketCount} (formula: ceil({dumpGb:F2} GB × 1024 / 500))");
            Console.WriteLine($"Truncation cap: {TRUNCATION_CAP:N0}");

            // Build index with per-bucket locks
            var buckets = Enumerable.Range(0, bucketCount)
                .Select(_ => new BucketData())
                .ToArray();

            var objectSizes = new Dictionary<ulong, ulong>();
            var allChildren = new HashSet<ulong>();

            Stopwatch buildSw = Stopwatch.StartNew();
            long objectsProcessed = 0;
            long edgesExtracted = 0;
            long truncatedEdges = 0;

            foreach (var obj in heap.EnumerateObjects())
            {
                objectsProcessed++;
                if (objectsProcessed % 100_000 == 0)
                    Console.Write($"\r  Objects: {objectsProcessed:N0}, Edges: {edgesExtracted:N0}");

                if (obj.Type == null || !obj.IsValid)
                    continue;

                objectSizes[obj.Address] = obj.Size;

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
                            int bucketIdx = GetBucketIndex(refObj.Address, bucketCount);
                            var bucket = buckets[bucketIdx];

                            lock (bucket.Lock)
                            {
                                // Full index
                                if (!bucket.ChildToParentsFull.ContainsKey(refObj.Address))
                                    bucket.ChildToParentsFull[refObj.Address] = new List<ulong>();
                                bucket.ChildToParentsFull[refObj.Address].Add(obj.Address);

                                // Truncated index (with cap)
                                if (!bucket.ChildToParentsTruncated.ContainsKey(refObj.Address))
                                    bucket.ChildToParentsTruncated[refObj.Address] = new List<ulong>();

                                if (bucket.ChildToParentsTruncated[refObj.Address].Count < TRUNCATION_CAP)
                                {
                                    bucket.ChildToParentsTruncated[refObj.Address].Add(obj.Address);
                                }
                                else if (bucket.ChildToParentsTruncated[refObj.Address].Count == TRUNCATION_CAP)
                                {
                                    truncatedEdges++;
                                }
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
            buildSw.Stop();

            Console.WriteLine($"\r  Objects: {objectsProcessed:N0}, Edges: {edgesExtracted:N0}");
            Console.WriteLine($"Index built in {buildSw.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"Unique children: {allChildren.Count:N0}");
            Console.WriteLine($"Truncated edges: {truncatedEdges:N0}");

            // ===== INVESTIGATION 4: QUERY LATENCY =====
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"INVESTIGATION 4: Query Latency");
            Console.WriteLine($"{'='*80}");

            var queryCandidates = allChildren.ToList();
            var random = new Random(42);
            int queryCount = Math.Min(10_000, queryCandidates.Count);

            var queryAddresses = new ulong[queryCount];
            for (int i = 0; i < queryCount; i++)
                queryAddresses[i] = queryCandidates[random.Next(queryCandidates.Count)];

            // Single-thread latency
            var latencies = new long[queryCount];
            var latencySw = Stopwatch.StartNew();

            for (int i = 0; i < queryCount; i++)
            {
                var itemSw = Stopwatch.StartNew();
                var child = queryAddresses[i];
                int bucketIdx = GetBucketIndex(child, bucketCount);
                var bucket = buckets[bucketIdx];

                lock (bucket.Lock)
                {
                    if (bucket.ChildToParentsFull.TryGetValue(child, out var parents))
                    {
                        var count = parents.Count;
                    }
                }

                itemSw.Stop();
                latencies[i] = itemSw.ElapsedMilliseconds;
            }
            latencySw.Stop();

            Array.Sort(latencies);
            var p50_l = latencies[(int)(queryCount * 0.50)];
            var p95_l = latencies[(int)(queryCount * 0.95)];
            var p99_l = latencies[(int)(queryCount * 0.99)];
            var avg_l = latencies.Average();

            Console.WriteLine($"Queries: {queryCount:N0}");
            Console.WriteLine($"Single-thread throughput: {queryCount / latencySw.Elapsed.TotalSeconds:F0} qps");
            Console.WriteLine($"  p50: {p50_l} ms | p95: {p95_l} ms | p99: {p99_l} ms (target: <50) | avg: {avg_l:F2} ms");

            // Concurrent throughput (10 threads)
            const int concurrentThreads = 10;
            int concurrentQueriesPerThread = 1000;
            var concurrentLatencies = new List<long>();
            var concurrentLock = new object();

            var concurrentSw = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, concurrentThreads)
                .Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < concurrentQueriesPerThread; i++)
                    {
                        var child = queryAddresses[random.Next(queryAddresses.Length)];
                        int bucketIdx = GetBucketIndex(child, bucketCount);
                        var bucket = buckets[bucketIdx];

                        var itemSw = Stopwatch.StartNew();
                        lock (bucket.Lock)
                        {
                            if (bucket.ChildToParentsFull.TryGetValue(child, out var parents))
                            {
                                var count = parents.Count;
                            }
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
            var concurrentP99 = concurrentLatenciesArray[(int)(concurrentLatenciesArray.Length * 0.99)];
            var concurrentThroughput = (concurrentThreads * concurrentQueriesPerThread) / concurrentSw.Elapsed.TotalSeconds;

            Console.WriteLine($"Concurrent ({concurrentThreads}t): {concurrentThroughput:F0} qps (target: >10K) | p99: {concurrentP99} ms");

            var inv4Pass = p99_l < 50 && concurrentThroughput > 10_000;
            var inv4Decision = inv4Pass ? "✅ PASS" : (p99_l < 100 && concurrentThroughput > 5_000 ? "⚠️  YELLOW" : "❌ RED");
            Console.ForegroundColor = inv4Pass ? ConsoleColor.Green : (p99_l < 100 ? ConsoleColor.Yellow : ConsoleColor.Red);
            Console.WriteLine($"Result: {inv4Decision}");
            Console.ResetColor();

            // ===== INVESTIGATION 5: TRUNCATION IMPACT =====
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"INVESTIGATION 5: Truncation Impact on Leak Detection");
            Console.WriteLine($"{'='*80}");

            // Aggregate indices
            long fullChildren = 0, truncatedChildren = 0;
            foreach (var bucket in buckets)
            {
                lock (bucket.Lock)
                {
                    fullChildren += bucket.ChildToParentsFull.Count;
                    truncatedChildren += bucket.ChildToParentsTruncated.Count;
                }
            }

            var truncatedObjects = 0;
            var highFanout = 0;

            foreach (var bucket in buckets)
            {
                lock (bucket.Lock)
                {
                    foreach (var kvp in bucket.ChildToParentsFull)
                    {
                        if (kvp.Value.Count > TRUNCATION_CAP)
                        {
                            truncatedObjects++;
                            if (kvp.Value.Count > 100_000)
                                highFanout++;
                        }
                    }
                }
            }

            var truncationRate = truncatedObjects / (double)fullChildren * 100;

            // Simulate leak detection: find top 100 large objects
            var topLargeObjects = objectSizes
                .OrderByDescending(kvp => kvp.Value)
                .Take(100)
                .ToList();

            long suspectsLost = 0;
            long retentionPathsLost = 0;

            foreach (var (addr, size) in topLargeObjects)
            {
                int bucketIdx = GetBucketIndex(addr, bucketCount);
                var bucket = buckets[bucketIdx];

                lock (bucket.Lock)
                {
                    bool inFull = bucket.ChildToParentsFull.ContainsKey(addr);
                    bool inTruncated = bucket.ChildToParentsTruncated.ContainsKey(addr);

                    if (inFull && !inTruncated)
                    {
                        suspectsLost++;
                    }
                    else if (inFull && inTruncated)
                    {
                        var fullCount = bucket.ChildToParentsFull[addr].Count;
                        var truncatedCount = bucket.ChildToParentsTruncated[addr].Count;
                        if (truncatedCount < fullCount)
                            retentionPathsLost += (fullCount - truncatedCount);
                    }
                }
            }

            var falseNegativeRate = suspectsLost / 100.0 * 100;

            Console.WriteLine($"Full children: {fullChildren:N0}");
            Console.WriteLine($"Truncated objects (>10K): {truncatedObjects} ({truncationRate:F2}%)");
            Console.WriteLine($"  High fanout (>100K): {highFanout}");
            Console.WriteLine($"Leak suspects lost: {suspectsLost}/100 ({falseNegativeRate:F1}%)");
            Console.WriteLine($"Retention paths lost: {retentionPathsLost:N0}");

            var inv5Pass = truncationRate < 1.0 && falseNegativeRate < 0.5;
            var inv5Decision = inv5Pass ? "✅ PASS" : (truncationRate < 2.0 && falseNegativeRate < 1.0 ? "⚠️  YELLOW" : "❌ RED");
            Console.ForegroundColor = inv5Pass ? ConsoleColor.Green : (truncationRate < 2.0 ? ConsoleColor.Yellow : ConsoleColor.Red);
            Console.WriteLine($"Result: {inv5Decision}");
            Console.ResetColor();

            // ===== INVESTIGATION 6: CONCURRENT THROUGHPUT =====
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"INVESTIGATION 6: Concurrent Throughput Scaling");
            Console.WriteLine($"{'='*80}");

            Console.WriteLine("{0,-8} {1,-10} {2,-8} {3,-12} {4,-8}", "Threads", "Queries", "Time (s)", "Throughput", "Scaling");
            Console.WriteLine(new string('-', 56));

            long baselineThroughput = 0;
            var scalingResults = new List<(int threads, long throughput)>();

            foreach (int threadCount in new[] { 1, 5, 10, 25, 50 })
            {
                int queriesPerT = 1000;
                int totalQueries = threadCount * queriesPerT;

                var scalingSw = Stopwatch.StartNew();
                var scalingTasks = Enumerable.Range(0, threadCount)
                    .Select(_ => Task.Run(() =>
                    {
                        for (int i = 0; i < queriesPerT; i++)
                        {
                            var child = queryAddresses[random.Next(queryAddresses.Length)];
                            int bucketIdx = GetBucketIndex(child, bucketCount);
                            var bucket = buckets[bucketIdx];

                            lock (bucket.Lock)
                            {
                                if (bucket.ChildToParentsFull.TryGetValue(child, out var parents))
                                {
                                    var count = parents.Count;
                                }
                            }
                        }
                    }))
                    .ToArray();

                Task.WaitAll(scalingTasks);
                scalingSw.Stop();

                long throughput = (long)(totalQueries / scalingSw.Elapsed.TotalSeconds);
                double scaling = baselineThroughput > 0 ? throughput / (double)baselineThroughput : 1.0;

                if (threadCount == 1)
                    baselineThroughput = throughput;

                scalingResults.Add((threadCount, throughput));

                Console.WriteLine("{0,-8} {1,-10:N0} {2,-8:F2} {3,-12:N0} {4,-8:F2}x",
                    threadCount, totalQueries, scalingSw.Elapsed.TotalSeconds, throughput, scaling);
            }

            var scalingAt10 = scalingResults.FirstOrDefault(r => r.threads == 10);
            var scalingAt50 = scalingResults.FirstOrDefault(r => r.threads == 50);

            var inv6Pass = scalingAt10.throughput > 10_000 && scalingAt50.throughput > 5_000;
            var inv6Decision = inv6Pass ? "✅ PASS" : (scalingAt10.throughput > 5_000 && scalingAt50.throughput > 1_000 ? "⚠️  YELLOW" : "❌ RED");
            Console.ForegroundColor = inv6Pass ? ConsoleColor.Green : (scalingAt10.throughput > 5_000 ? ConsoleColor.Yellow : ConsoleColor.Red);
            Console.WriteLine($"Result: {inv6Decision}");
            Console.ResetColor();

            // ===== SUMMARY =====
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"VALIDATION SUMMARY");
            Console.WriteLine($"{'='*80}");
            Console.WriteLine($"Investigation 4 (Query Latency): {inv4Decision}");
            Console.WriteLine($"Investigation 5 (Truncation Impact): {inv5Decision}");
            Console.WriteLine($"Investigation 6 (Concurrent Throughput): {inv6Decision}");

            int redCount = (inv4Pass ? 0 : 1) + (inv5Pass ? 0 : 1) + (inv6Pass ? 0 : 1);
            if (redCount == 0)
            {
                Console.WriteLine($"\n🎯 GATE DECISION: ✅ GO");
                Console.WriteLine($"All investigations PASS. Proceed to Phase 1 implementation.");
            }
            else if (redCount == 1)
            {
                Console.WriteLine($"\n🎯 GATE DECISION: ⚠️  CONDITIONAL GO");
                Console.WriteLine($"1 investigation YELLOW; acceptable with mitigations.");
            }
            else
            {
                Console.WriteLine($"\n🎯 GATE DECISION: ❌ NO-GO");
                Console.WriteLine($"{redCount} investigations RED; design review required.");
            }

            dt.Dispose();
            Console.WriteLine($"\n{'='*80}\n");

            return redCount > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

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
}
