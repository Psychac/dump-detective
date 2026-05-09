using DumpDetective.Core.Utilities;

using System;
using System.Diagnostics;
using System.IO;

namespace DumpDetective.Analysis.Diagnostics
{
    internal static class MemoryDiagnostic
    {
        private static long _baselineMemory;

        public static void SetBaseline()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _baselineMemory = GC.GetTotalMemory(false);
        }

        public static void PrintMemoryUsage(string checkpointName, TextWriter writer)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long currentMemory = GC.GetTotalMemory(false);
            long deltaMemory = currentMemory - _baselineMemory;

            var process = Process.GetCurrentProcess();
            long workingSet = process.WorkingSet64;
            long privateMemory = process.PrivateMemorySize64;

            writer.WriteLine($"\nðŸ” MEMORY CHECKPOINT: {checkpointName}");
            writer.WriteLine($"   Managed Heap: {FormatHelper.FormatBytes((ulong)currentMemory)} (Î” {FormatHelper.FormatBytes((ulong)Math.Abs(deltaMemory))})");
            writer.WriteLine($"   Working Set: {FormatHelper.FormatBytes((ulong)workingSet)}");
            writer.WriteLine($"   Private Memory: {FormatHelper.FormatBytes((ulong)privateMemory)}");

            Console.WriteLine($"[MEMORY] {checkpointName}: Working Set = {FormatHelper.FormatBytes((ulong)workingSet)}, Private = {FormatHelper.FormatBytes((ulong)privateMemory)}");
        }

        public static MemorySnapshot TakeSnapshot(string label)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var process = Process.GetCurrentProcess();

            return new MemorySnapshot
            {
                Label = label,
                ManagedMemory = GC.GetTotalMemory(false),
                WorkingSet = process.WorkingSet64,
                PrivateMemory = process.PrivateMemorySize64,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2)
            };
        }

        public static void CompareSnapshots(MemorySnapshot before, MemorySnapshot after, TextWriter writer)
        {
            long managedDelta = after.ManagedMemory - before.ManagedMemory;
            long workingSetDelta = after.WorkingSet - before.WorkingSet;
            long privateDelta = after.PrivateMemory - before.PrivateMemory;

            writer.WriteLine($"\nðŸ“Š MEMORY COMPARISON: {before.Label} â†’ {after.Label}");
            writer.WriteLine($"   Managed Heap: {FormatHelper.FormatBytes((ulong)Math.Abs(managedDelta))} {(managedDelta >= 0 ? "â†‘" : "â†“")}");
            writer.WriteLine($"   Working Set: {FormatHelper.FormatBytes((ulong)Math.Abs(workingSetDelta))} {(workingSetDelta >= 0 ? "â†‘" : "â†“")}");
            writer.WriteLine($"   Private Memory: {FormatHelper.FormatBytes((ulong)Math.Abs(privateDelta))} {(privateDelta >= 0 ? "â†‘" : "â†“")}");
            writer.WriteLine($"   Gen0/Gen1/Gen2 Collections: +{after.Gen0Collections - before.Gen0Collections} / +{after.Gen1Collections - before.Gen1Collections} / +{after.Gen2Collections - before.Gen2Collections}");

            Console.WriteLine($"[MEMORY DELTA] {before.Label} â†’ {after.Label}: {FormatHelper.FormatBytes((ulong)Math.Abs(workingSetDelta))} {(workingSetDelta >= 0 ? "increase" : "decrease")}");
        }

        public static void PrintSnapshotToConsole(MemorySnapshot snapshot)
        {
            Console.WriteLine($"[MEMORY] {snapshot.Label}: Managed={FormatHelper.FormatBytes((ulong)snapshot.ManagedMemory)}, WorkingSet={FormatHelper.FormatBytes((ulong)snapshot.WorkingSet)}, Private={FormatHelper.FormatBytes((ulong)snapshot.PrivateMemory)}");
        }

        public static void PrintDeltaToConsole(MemorySnapshot before, MemorySnapshot after)
        {
            long managedDelta = after.ManagedMemory - before.ManagedMemory;
            long workingSetDelta = after.WorkingSet - before.WorkingSet;
            long privateDelta = after.PrivateMemory - before.PrivateMemory;

            Console.WriteLine($"[MEMORY DELTA] {before.Label} -> {after.Label}: Managed={FormatHelper.FormatBytes((ulong)Math.Abs(managedDelta))} {(managedDelta >= 0 ? "increase" : "decrease")}, WorkingSet={FormatHelper.FormatBytes((ulong)Math.Abs(workingSetDelta))} {(workingSetDelta >= 0 ? "increase" : "decrease")}, Private={FormatHelper.FormatBytes((ulong)Math.Abs(privateDelta))} {(privateDelta >= 0 ? "increase" : "decrease")}");
        }
    }

    internal class MemorySnapshot
    {
        public string Label { get; set; } = string.Empty;
        public long ManagedMemory { get; set; }
        public long WorkingSet { get; set; }
        public long PrivateMemory { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
    }
}


