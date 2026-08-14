using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

// Investigation 5: Truncation Impact on Leak Detection
// Validates whether 10K fanout cap truncates critical retention paths
// and causes false negatives in leak detection.

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: TruncationImpactValidator <dump-path> [top-n-leaks]");
    return 1;
}

string dumpPath = args[0];
int topNLeaks = args.Length > 1 ? int.Parse(args[1]) : 100;
const int TRUNCATION_CAP = 10_000;

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var fileInfo = new FileInfo(dumpPath);
Console.WriteLine($"\n{'='*70}");
Console.WriteLine($"Investigation 5: Truncation Impact on Leak Detection");
Console.WriteLine($"{'='*70}");
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB)");
Console.WriteLine($"Truncation cap: {TRUNCATION_CAP:N0}");
Console.WriteLine($"Top N leaks to analyze: {topNLeaks}");
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

    // PHASE A: Extract edges (full reverse index)
    Console.WriteLine("\n--- PHASE A: Extract All Edges ---");

    var fullChildToParents = new Dictionary<ulong, List<ulong>>();
    var truncatedChildToParents = new Dictionary<ulong, List<ulong>>();
    var objectSizes = new Dictionary<ulong, ulong>();

    Stopwatch extractSw = Stopwatch.StartNew();
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
                    // Full index
                    if (!fullChildToParents.ContainsKey(refObj.Address))
                        fullChildToParents[refObj.Address] = new List<ulong>();
                    fullChildToParents[refObj.Address].Add(obj.Address);

                    // Truncated index (with 10K cap)
                    if (!truncatedChildToParents.ContainsKey(refObj.Address))
                        truncatedChildToParents[refObj.Address] = new List<ulong>();

                    if (truncatedChildToParents[refObj.Address].Count < TRUNCATION_CAP)
                    {
                        truncatedChildToParents[refObj.Address].Add(obj.Address);
                    }
                    else if (truncatedChildToParents[refObj.Address].Count == TRUNCATION_CAP)
                    {
                        truncatedEdges++;
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
    Console.WriteLine($"Full edges: {edgesExtracted:N0}");
    Console.WriteLine($"Truncated edges (beyond cap): {truncatedEdges:N0}");
    Console.WriteLine($"Extraction time: {extractSw.Elapsed.TotalSeconds:F2}s");

    // PHASE B: Analyze truncation impact
    Console.WriteLine("\n--- PHASE B: Truncation Analysis ---");

    var truncatedObjects = fullChildToParents
        .Where(kvp => kvp.Value.Count > TRUNCATION_CAP)
        .ToList();

    var truncationRate = truncatedObjects.Count / (double)fullChildToParents.Count * 100;

    Console.WriteLine($"Children with parents: {fullChildToParents.Count:N0}");
    Console.WriteLine($"Truncated children (>10K parents): {truncatedObjects.Count:N0} ({truncationRate:F2}%)");

    // Categorize by truncation severity
    var highTruncation = truncatedObjects.Where(kvp => kvp.Value.Count > 100_000).ToList();
    var mediumTruncation = truncatedObjects.Where(kvp => kvp.Value.Count > 10_000 && kvp.Value.Count <= 100_000).ToList();

    Console.WriteLine($"  Severe (>100K): {highTruncation.Count:N0}");
    Console.WriteLine($"  Medium (10K-100K): {mediumTruncation.Count:N0}");

    // PHASE C: Leak detection simulation
    Console.WriteLine($"\n--- PHASE C: Leak Detection Simulation (top {topNLeaks} leaks) ---");

    // Find top N large objects (simulated "leak suspects")
    var suspectsBySize = objectSizes
        .OrderByDescending(kvp => kvp.Value)
        .Take(topNLeaks)
        .ToList();

    long suspectsIdentifiedFull = 0;
    long suspectsIdentifiedTruncated = 0;
    long suspectsLostToTruncation = 0;
    long retentionPathsLost = 0;

    foreach (var (addr, size) in suspectsBySize)
    {
        bool inFull = fullChildToParents.ContainsKey(addr);
        bool inTruncated = truncatedChildToParents.ContainsKey(addr);

        if (inFull)
            suspectsIdentifiedFull++;

        if (inTruncated)
            suspectsIdentifiedTruncated++;
        else if (inFull)
            suspectsLostToTruncation++;

        // Check if truncation affected retention path analysis
        if (inFull && inTruncated)
        {
            var fullParents = fullChildToParents[addr].Count;
            var truncatedParents = truncatedChildToParents[addr].Count;

            if (truncatedParents < fullParents)
                retentionPathsLost += (fullParents - truncatedParents);
        }
    }

    var retentionAccuracy = suspectsIdentifiedTruncated / (double)suspectsIdentifiedFull * 100;
    var falseNegativeRate = suspectsLostToTruncation / (double)suspectsIdentifiedFull * 100;

    Console.WriteLine($"Leak suspects analyzed: {topNLeaks}");
    Console.WriteLine($"Identified (full index): {suspectsIdentifiedFull}");
    Console.WriteLine($"Identified (truncated): {suspectsIdentifiedTruncated}");
    Console.WriteLine($"Lost to truncation: {suspectsLostToTruncation} ({falseNegativeRate:F2}%)");
    Console.WriteLine($"Retention paths lost: {retentionPathsLost:N0}");
    Console.WriteLine($"Retention accuracy: {retentionAccuracy:F1}%");

    // DECISION
    Console.WriteLine("\n--- Decision ---");
    string decision;
    if (truncationRate < 1.0 && falseNegativeRate < 0.5)
    {
        decision = "✅ PASS";
        Console.ForegroundColor = ConsoleColor.Green;
    }
    else if (truncationRate < 2.0 && falseNegativeRate < 1.0)
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
    Console.WriteLine($"  Truncation rate: {truncationRate:F2}% (target: <1%)");
    Console.WriteLine($"  False negative rate: {falseNegativeRate:F2}% (target: <0.5%)");
    Console.ResetColor();

    if (truncationRate >= 0.1)
    {
        Console.WriteLine($"\nTruncation is occurring. Mitigation strategy:");
        if (suspectsLostToTruncation > 0)
            Console.WriteLine($"  - Implement fallback: re-enumerate {suspectsLostToTruncation} lost suspects");
        if (retentionPathsLost > 0)
            Console.WriteLine($"  - Fallback cost would be: ~{retentionPathsLost} additional edge scans");
    }

    dt.Dispose();
    Console.WriteLine($"\n{'='*70}\n");

    return (truncationRate < 1.0 && falseNegativeRate < 0.5) ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}
