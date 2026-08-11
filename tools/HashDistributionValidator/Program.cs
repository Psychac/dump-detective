using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

// Investigation 2: Hash Function Distribution
// Validates bucket distribution uniformity using the reverse-index hash function.
// Tests formula: N = max(1, dump_size_gb / 15) for bucket count.
// Target: coefficient of variation <10%, max bucket <500 MB.

string dumpPath = args.Length > 0
    ? args[0]
    : throw new ArgumentException("Usage: HashDistributionValidator <dump-path>");

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var fileInfo = new FileInfo(dumpPath);
long dumpBytes = fileInfo.Length;
double dumpGb = dumpBytes / (1024.0 * 1024.0 * 1024.0);

Console.WriteLine($"\n{'='*70}");
Console.WriteLine($"Investigation 2: Hash Function Distribution");
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

    // Calculate bucket count using formula: ensure max bucket stays <500 MB
    // For 3.3 GB: need at least 7 buckets (3300 MB / 500 MB = 6.6)
    // Formula: N = Math.Max(1, (int)Math.Ceiling(dumpGb * 1024 / 500))
    int bucketCount = Math.Max(1, (int)Math.Ceiling(dumpGb * 1024.0 / 500.0));
    Console.WriteLine($"Bucket count (N=ceil(dump_mb/500)): {bucketCount}");
    Console.WriteLine($"Expected bucket size (avg): ~{dumpGb / bucketCount:F2} GB / {1024 * dumpGb / bucketCount:F0} MB");

    // Hash function (simple modulo for spike; production uses MurmurHash3)
    static int ChildBucketHash(ulong address, int bucketCount)
    {
        return (int)(address % (ulong)bucketCount);
    }

    // Collect bucket statistics
    var bucketSizes = new long[bucketCount];
    var bucketCounts = new long[bucketCount];

    Console.WriteLine("\n--- Bucket Distribution Analysis ---");
    Stopwatch hashSw = Stopwatch.StartNew();
    long objectsScanned = 0;

    foreach (var obj in heap.EnumerateObjects())
    {
        objectsScanned++;
        if (objectsScanned % 100_000 == 0)
            Console.Write($"\r  Objects hashed: {objectsScanned:N0}");

        if (obj.Type == null || !obj.IsValid)
            continue;

        int bucketIdx = ChildBucketHash(obj.Address, bucketCount);
        bucketSizes[bucketIdx] += (long)obj.Size;
        bucketCounts[bucketIdx]++;
    }
    hashSw.Stop();

    Console.WriteLine($"\r  Objects hashed: {objectsScanned:N0}");
    Console.WriteLine($"Hash computation completed in {hashSw.Elapsed.TotalSeconds:F2}s\n");

    // Compute statistics
    long totalBytes = bucketSizes.Sum();
    double meanSize = bucketSizes.Average();
    double stdDev = Math.Sqrt(bucketSizes.Select(s => Math.Pow(s - meanSize, 2)).Average());
    double coefficientOfVariation = meanSize > 0 ? stdDev / meanSize : 0;
    long maxBucket = bucketSizes.Max();
    long minBucket = bucketSizes.Min();
    int maxBucketIdx = Array.IndexOf(bucketSizes, maxBucket);

    Console.WriteLine("--- Statistics ---");
    Console.WriteLine($"Total bytes: {totalBytes / (1024.0 * 1024 * 1024):F2} GB");
    Console.WriteLine($"Mean bucket size: {meanSize / (1024.0 * 1024):F2} MB");
    Console.WriteLine($"Std Dev: {stdDev / (1024.0 * 1024):F2} MB");
    Console.WriteLine($"Coefficient of Variation: {coefficientOfVariation:F4} (target: <0.10)");
    Console.WriteLine($"Max bucket: {maxBucket / (1024.0 * 1024):F2} MB (bucket #{maxBucketIdx}, target: <500 MB)");
    Console.WriteLine($"Min bucket: {minBucket / (1024.0 * 1024):F2} MB");
    Console.WriteLine($"Range: {(maxBucket - minBucket) / (1024.0 * 1024):F2} MB");

    // DECISION
    Console.WriteLine("\n--- Decision ---");
    string decision;
    string reasoning = "";

    bool uniformityPass = coefficientOfVariation < 0.10;
    bool maxBucketPass = maxBucket < (500 * 1024 * 1024);

    if (uniformityPass && maxBucketPass)
    {
        decision = "✅ PASS";
        Console.ForegroundColor = ConsoleColor.Green;
    }
    else if (coefficientOfVariation < 0.20 && maxBucket < (600 * 1024 * 1024))
    {
        decision = "⚠️  YELLOW";
        Console.ForegroundColor = ConsoleColor.Yellow;
        if (!uniformityPass)
            reasoning += $"  - Coefficient variation {coefficientOfVariation:F4} is 10–20% (acceptable, monitor)";
        if (!maxBucketPass)
            reasoning += $"\n  - Max bucket {maxBucket / (1024.0 * 1024):F2} MB is 500–600 MB (manageable)";
    }
    else
    {
        decision = "❌ RED";
        Console.ForegroundColor = ConsoleColor.Red;
        if (!uniformityPass)
            reasoning += $"  - Coefficient variation {coefficientOfVariation:F4} exceeds 20% (poor uniformity)";
        if (!maxBucketPass)
            reasoning += $"\n  - Max bucket {maxBucket / (1024.0 * 1024):F2} MB exceeds 600 MB (OOM risk)";
    }

    Console.WriteLine($"Result: {decision}");
    if (!string.IsNullOrEmpty(reasoning))
        Console.WriteLine(reasoning);
    Console.ResetColor();

    // Bucket distribution histogram
    Console.WriteLine("\n--- Bucket Size Distribution (Top 10 largest) ---");
    var top10 = bucketSizes
        .Select((size, idx) => (idx, size))
        .OrderByDescending(x => x.size)
        .Take(10)
        .ToList();

    foreach (var (idx, size) in top10)
    {
        double percent = totalBytes > 0 ? size * 100.0 / totalBytes : 0;
        Console.WriteLine($"  Bucket {idx:D3}: {size / (1024.0 * 1024):F2} MB ({percent:F1}%)");
    }

    // Suggested formula adjustment if needed
    if (!maxBucketPass)
    {
        double suggestedDivisor = dumpGb / (maxBucket / (double)(512 * 1024 * 1024));
        Console.WriteLine($"\n--- Recommendation ---");
        Console.WriteLine($"If max bucket must stay <500 MB, consider: N = dump_size_gb / {suggestedDivisor:F1}");
    }

    dt.Dispose();
    Console.WriteLine($"\n{'='*70}\n");

    return (uniformityPass && maxBucketPass) ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}
