using System;
using System.Diagnostics;

namespace DumpDetective.Utilities
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

        public static void PrintMemoryUsage(string checkpointName, OutputWriter writer)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            long currentMemory = GC.GetTotalMemory(false);
            long deltaMemory = currentMemory - _baselineMemory;
            
            var process = Process.GetCurrentProcess();
            long workingSet = process.WorkingSet64;
            long privateMemory = process.PrivateMemorySize64;
            
            writer.WriteLine($"\n🔍 MEMORY CHECKPOINT: {checkpointName}");
            writer.WriteLine($"   Managed Heap: {FormatHelper.FormatBytes((ulong)currentMemory)} (Δ {FormatHelper.FormatBytes((ulong)Math.Abs(deltaMemory))})");
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

        public static void CompareSnapshots(MemorySnapshot before, MemorySnapshot after, OutputWriter writer)
        {
            long managedDelta = after.ManagedMemory - before.ManagedMemory;
            long workingSetDelta = after.WorkingSet - before.WorkingSet;
            long privateDelta = after.PrivateMemory - before.PrivateMemory;

            writer.WriteLine($"\n📊 MEMORY COMPARISON: {before.Label} → {after.Label}");
            writer.WriteLine($"   Managed Heap: {FormatHelper.FormatBytes((ulong)Math.Abs(managedDelta))} {(managedDelta >= 0 ? "↑" : "↓")}");
            writer.WriteLine($"   Working Set: {FormatHelper.FormatBytes((ulong)Math.Abs(workingSetDelta))} {(workingSetDelta >= 0 ? "↑" : "↓")}");
            writer.WriteLine($"   Private Memory: {FormatHelper.FormatBytes((ulong)Math.Abs(privateDelta))} {(privateDelta >= 0 ? "↑" : "↓")}");
            writer.WriteLine($"   Gen0/Gen1/Gen2 Collections: +{after.Gen0Collections - before.Gen0Collections} / +{after.Gen1Collections - before.Gen1Collections} / +{after.Gen2Collections - before.Gen2Collections}");

            Console.WriteLine($"[MEMORY DELTA] {before.Label} → {after.Label}: {FormatHelper.FormatBytes((ulong)Math.Abs(workingSetDelta))} {(workingSetDelta >= 0 ? "increase" : "decrease")}");
        }

        public static void PrintSnapshotToConsole(MemorySnapshot snapshot)
        {
            Console.WriteLine($"\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine($"🔍 MEMORY SNAPSHOT: {snapshot.Label}");
            Console.WriteLine($"   Managed Heap: {FormatHelper.FormatBytes((ulong)snapshot.ManagedMemory)}");
            Console.WriteLine($"   Working Set: {FormatHelper.FormatBytes((ulong)snapshot.WorkingSet)}");
            Console.WriteLine($"   Private Memory: {FormatHelper.FormatBytes((ulong)snapshot.PrivateMemory)}");
            Console.WriteLine($"═══════════════════════════════════════════════════════════════");
        }

        public static void PrintDeltaToConsole(MemorySnapshot before, MemorySnapshot after)
        {
            long managedDelta = after.ManagedMemory - before.ManagedMemory;
            long workingSetDelta = after.WorkingSet - before.WorkingSet;
            long privateDelta = after.PrivateMemory - before.PrivateMemory;

            string arrow = workingSetDelta >= 0 ? "↑" : "↓";
            string color = workingSetDelta > 100_000_000 ? "🔴" : (workingSetDelta > 10_000_000 ? "🟡" : "🟢");

            Console.WriteLine($"\n{color} {after.Label}");
            Console.WriteLine($"   Managed: {FormatHelper.FormatBytes((ulong)Math.Abs(managedDelta))} {arrow}");
            Console.WriteLine($"   Working Set: {FormatHelper.FormatBytes((ulong)Math.Abs(workingSetDelta))} {arrow}  [Total: {FormatHelper.FormatBytes((ulong)after.WorkingSet)}]");
            Console.WriteLine($"   Private: {FormatHelper.FormatBytes((ulong)Math.Abs(privateDelta))} {arrow}");
            Console.WriteLine($"   GC: Gen0={after.Gen0Collections - before.Gen0Collections}, Gen1={after.Gen1Collections - before.Gen1Collections}, Gen2={after.Gen2Collections - before.Gen2Collections}");
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
