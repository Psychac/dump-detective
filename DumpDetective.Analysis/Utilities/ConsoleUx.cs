using DumpDetective.Analysis.Diagnostics;
using System.Diagnostics;

namespace DumpDetective.Analysis.Utilities;

// TEMP-REFRACTOR-BRIDGE: Placeholder console surface to keep Analysis buildable until CLI wiring is finalized.
internal static class ConsoleUx
{
    public static void StageStart(int current, int total, string stageName, TimeSpan? eta = null) { }
    public static void StageComplete(string stageName, Stopwatch stopwatch) { }
    public static void AnalyzerStart(int current, int total, string analyzerName) { }
    public static void AnalyzerComplete(int current, int total, string analyzerName, Stopwatch stopwatch) { }
    public static void Error(string message) => System.Console.WriteLine(message);
    public static void PipelineProgress(int completedStages, int totalStages, TimeSpan elapsed, TimeSpan? eta) { }
    public static void PipelineSummary(IReadOnlyList<(string StageName, TimeSpan Duration, int AnalyzerCount)> stageResults) { }
    public static void MemorySnapshot(MemorySnapshot snapshot) { }
    public static void MemoryDelta(MemorySnapshot before, MemorySnapshot after) { }
    public static void ObjectScanProgress(string operation, long scannedCount, TimeSpan elapsed) { }
    public static void ObjectScanComplete(string operation, long scannedCount, TimeSpan elapsed) { }
}