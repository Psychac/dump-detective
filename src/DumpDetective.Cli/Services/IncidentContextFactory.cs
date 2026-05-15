using System.IO;
using System.Reflection;
using DumpDetective.Analysis.Dump;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Services;

internal static class IncidentContextFactory
{
    public static AnalysisIncidentContext Create(
        string mode,
        DumpLoadContext? loadContext,
        ResolvedExecutionOptions resolved,
        IReadOnlyList<IAnalyzer> activeAnalyzers,
        TimeSpan elapsed,
        IReadOnlyList<TrendSnapshotContext>? trendSnapshots = null)
    {
        string dumpPath = loadContext?.DumpPath ?? resolved.DumpPath;
        string? runtimeVersion = GetStringProperty(GetPropertyValue(loadContext?.Runtime, "ClrInfo"), "Version");
        string? runtimeFlavor = GetStringProperty(GetPropertyValue(loadContext?.Runtime, "ClrInfo"), "Flavor");
        string? gcMode = GetGcMode(loadContext?.Runtime);
        int? heapCount = GetIntProperty(GetPropertyValue(loadContext?.Runtime, "Heap"), "HeapCount");

        string? dumpSizeTierLabel = null;
        long? dumpFileSizeBytes = null;
        try
        {
            if (File.Exists(dumpPath))
            {
                long bytes = new FileInfo(dumpPath).Length;
                dumpFileSizeBytes = bytes;
                dumpSizeTierLabel = bytes > 4L * 1024 * 1024 * 1024 ? "Large (> 4 GB)"
                                  : bytes > 512L * 1024 * 1024     ? "Medium (512 MB – 4 GB)"
                                  : "Small (< 512 MB)";
            }
        }
        catch { /* best-effort — skip if file not accessible */ }

        return new AnalysisIncidentContext(
            Mode: mode,
            DumpPath: dumpPath,
            BaselineDumpPath: resolved.BaselineDumpPath,
            TrendDumpPaths: resolved.TrendDumpPaths,
            ReportFormat: resolved.Report.Format.ToString(),
            ReportAudience: resolved.Report.Audience.ToString(),
            ConfigPath: resolved.ConfigPath,
            UsedConfigFile: resolved.UsedConfigFile,
            DiagnosticMode: resolved.DiagnosticMode,
            IndexPrebuildMode: resolved.IndexPrebuildMode.ToString(),
            ActiveAnalyzerCount: activeAnalyzers.Count,
            ActiveAnalyzers: activeAnalyzers.Select(a => a.Name).ToList(),
            RuntimeVersion: runtimeVersion,
            RuntimeFlavor: runtimeFlavor,
            GcMode: gcMode,
            HeapCount: heapCount,
            HeapCanWalk: loadContext?.Heap?.CanWalkHeap ?? false,
            IsTrendReport: string.Equals(mode, "Trend", StringComparison.OrdinalIgnoreCase),
            AnalysisElapsedSeconds: elapsed.TotalSeconds,
            TrendSnapshots: trendSnapshots,
            DumpSizeTierLabel: dumpSizeTierLabel,
            DumpFileSizeBytes: dumpFileSizeBytes);
    }

    public static TrendSnapshotContext CreateSnapshot(
        int index,
        string dumpPath,
        DateTime generatedAtUtc,
        TimeSpan elapsed,
        IReadOnlyList<AnalyzerRunResult> runs,
        bool isBaseline,
        bool isCurrent)
        => new(
            Index: index,
            DumpPath: dumpPath,
            GeneratedAtUtc: generatedAtUtc,
            ElapsedSeconds: elapsed.TotalSeconds,
            AnalyzerCount: runs.Count,
            FindingCount: runs.Sum(r => r.FindingCount),
            IsBaseline: isBaseline,
            IsCurrent: isCurrent);

    private static object? GetPropertyValue(object? instance, string propertyName)
    {
        if (instance is null)
            return null;

        PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(instance);
    }

    private static string? GetStringProperty(object? instance, string propertyName)
    {
        object? value = GetPropertyValue(instance, propertyName);
        return value?.ToString();
    }

    private static int? GetIntProperty(object? instance, string propertyName)
    {
        object? value = GetPropertyValue(instance, propertyName);
        return value switch
        {
            int i => i,
            long l => (int)Math.Min(int.MaxValue, l),
            short s => s,
            byte b => b,
            _ => int.TryParse(value?.ToString(), out int parsed) ? parsed : null
        };
    }

    private static string? GetGcMode(object? runtime)
    {
        object? heap = GetPropertyValue(runtime, "Heap");
        bool? isServer = GetBoolProperty(heap, "IsServerGC") ?? GetBoolProperty(heap, "IsServer");
        if (!isServer.HasValue)
            return null;

        return isServer.Value ? "Server GC" : "Workstation GC";
    }

    private static bool? GetBoolProperty(object? instance, string propertyName)
    {
        object? value = GetPropertyValue(instance, propertyName);
        return value switch
        {
            bool b => b,
            _ => bool.TryParse(value?.ToString(), out bool parsed) ? parsed : null
        };
    }
}