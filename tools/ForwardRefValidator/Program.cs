using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

// Investigation 1: ClrMD 4 Forward-Ref Completeness
// Validates whether single-pass edge enumeration is complete or requires re-iteration.
// Compares edge counts from single-pass vs dual-pass heap enumeration to detect missing edges.

string dumpPath = args.Length > 0
    ? args[0]
    : throw new ArgumentException("Usage: ForwardRefValidator <dump-path>");

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var fileInfo = new FileInfo(dumpPath);
Console.WriteLine($"\n{'='*70}");
Console.WriteLine($"Investigation 1: ClrMD Forward-Ref Completeness");
Console.WriteLine($"{'='*70}");
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB)");
Console.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

try
{
    var options = new DataTargetOptions { UseLockFreeMemoryMapReader = true };

    // Load dump once for both passes
    Stopwatch loadSw = Stopwatch.StartNew();
    DataTarget dt = DataTarget.LoadDump(dumpPath, options);
    ClrRuntime rt = dt.ClrVersions[0].CreateRuntime();
    ClrHeap heap = rt.Heap;
    loadSw.Stop();
    Console.WriteLine($"\nDump loaded in {loadSw.Elapsed.TotalSeconds:F2}s");

    // PASS 1: Single-pass edge enumeration
    Console.WriteLine("\n--- PASS 1: Single-Pass Edge Enumeration ---");
    var pass1Edges = new HashSet<(ulong parent, ulong child)>();

    Stopwatch pass1Sw = Stopwatch.StartNew();
    long objectsPass1 = 0;
    foreach (var obj in heap.EnumerateObjects())
    {
        objectsPass1++;
        if (objectsPass1 % 100_000 == 0)
            Console.Write($"\r  Objects: {objectsPass1:N0}");

        if (obj.Type == null || !obj.IsValid)
            continue;

        // Enumerate fields for forward references
        foreach (var field in obj.Type.Fields)
        {
            if (!field.IsObjectReference)
                continue;

            try
            {
                var refObj = field.ReadObject(obj.Address, interior: false);
                if (refObj.IsValid)
                {
                    pass1Edges.Add((obj.Address, refObj.Address));
                }
            }
            catch
            {
                // Skip malformed objects
            }
        }
    }
    pass1Sw.Stop();
    Console.WriteLine($"\r  Objects scanned: {objectsPass1:N0}");
    Console.WriteLine($"Pass 1 complete: {pass1Edges.Count:N0} edges in {pass1Sw.Elapsed.TotalSeconds:F2}s");

    // PASS 2: Dual-pass enumeration (re-enumerate heap)
    Console.WriteLine("\n--- PASS 2: Dual-Pass Edge Enumeration (Re-enumerate) ---");

    // Close and reload to ensure fresh enumeration
    dt.Dispose();
    dt = DataTarget.LoadDump(dumpPath, options);
    rt = dt.ClrVersions[0].CreateRuntime();
    heap = rt.Heap;

    var pass2Edges = new HashSet<(ulong parent, ulong child)>();

    Stopwatch pass2Sw = Stopwatch.StartNew();
    long objectsPass2 = 0;
    foreach (var obj in heap.EnumerateObjects())
    {
        objectsPass2++;
        if (objectsPass2 % 100_000 == 0)
            Console.Write($"\r  Objects: {objectsPass2:N0}");

        if (obj.Type == null || !obj.IsValid)
            continue;

        // Enumerate fields for forward references
        foreach (var field in obj.Type.Fields)
        {
            if (!field.IsObjectReference)
                continue;

            try
            {
                var refObj = field.ReadObject(obj.Address, interior: false);
                if (refObj.IsValid)
                {
                    pass2Edges.Add((obj.Address, refObj.Address));
                }
            }
            catch
            {
                // Skip malformed objects
            }
        }
    }
    pass2Sw.Stop();
    Console.WriteLine($"\r  Objects scanned: {objectsPass2:N0}");
    Console.WriteLine($"Pass 2 complete: {pass2Edges.Count:N0} edges in {pass2Sw.Elapsed.TotalSeconds:F2}s");

    // ANALYSIS
    Console.WriteLine("\n--- Analysis ---");
    var edgeDifference = pass2Edges.Count - pass1Edges.Count;
    var edgeDiffPercent = pass1Edges.Count > 0
        ? Math.Abs(edgeDifference) / (double)pass1Edges.Count * 100
        : 0;

    Console.WriteLine($"Pass 1 edges:        {pass1Edges.Count:N0}");
    Console.WriteLine($"Pass 2 edges:        {pass2Edges.Count:N0}");
    Console.WriteLine($"Difference:          {edgeDifference:+#;-#;0} ({edgeDiffPercent:F4}%)");

    // Find edges only in pass 2 (missed in pass 1)
    var onlyInPass2 = pass2Edges.Except(pass1Edges).Count();
    var onlyInPass1 = pass1Edges.Except(pass2Edges).Count();

    Console.WriteLine($"Edges only in Pass 2: {onlyInPass2:N0}");
    Console.WriteLine($"Edges only in Pass 1: {onlyInPass1:N0}");

    // DECISION
    Console.WriteLine("\n--- Decision ---");
    string decision;
    if (edgeDiffPercent < 0.1)
    {
        decision = "✅ PASS";
        Console.ForegroundColor = ConsoleColor.Green;
    }
    else if (edgeDiffPercent < 1.0)
    {
        decision = "⚠️  YELLOW";
        Console.ForegroundColor = ConsoleColor.Yellow;
    }
    else
    {
        decision = "❌ RED";
        Console.ForegroundColor = ConsoleColor.Red;
    }

    Console.WriteLine($"Result: {decision} (edge delta {edgeDiffPercent:F4}%)");
    Console.ResetColor();

    if (edgeDiffPercent >= 0.1)
    {
        Console.WriteLine($"\nNote: Edge difference {edgeDiffPercent:F4}% suggests:");
        if (onlyInPass2 > 0)
            Console.WriteLine($"  - Single-pass may miss some edges (found {onlyInPass2:N0} additional in pass 2)");
        if (onlyInPass1 > 0)
            Console.WriteLine($"  - Or enumeration order affects object traversal ({onlyInPass1:N0} edges in pass 1 only)");
    }

    // PERFORMANCE
    Console.WriteLine("\n--- Performance ---");
    var reiterationCost = pass2Sw.Elapsed.TotalSeconds / pass1Sw.Elapsed.TotalSeconds;
    Console.WriteLine($"Re-iteration overhead: {(reiterationCost - 1) * 100:F1}%");
    Console.WriteLine($"Total time for validation: {pass1Sw.Elapsed.TotalSeconds + pass2Sw.Elapsed.TotalSeconds:F2}s");

    dt.Dispose();
    Console.WriteLine($"\n{'='*70}\n");

    return edgeDiffPercent < 0.1 ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}
