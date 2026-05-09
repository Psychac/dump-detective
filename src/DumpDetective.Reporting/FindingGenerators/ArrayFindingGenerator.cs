using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ArrayFindingGenerator : IFindingGenerator
{
    private const ulong LohWarningBytes = 500_000_000UL; // 500 MB
    private const ulong LohCriticalBytes = 2_000_000_000UL; // 2 GB
    private const int MultiDimWarningCount = 1_000;
    private const double SparseRatioThreshold = 0.70;
    private const ulong SparseWastedWarningBytes = 10_000_000UL; // 10 MB

    public string AnalyzerName => "Array Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ArrayDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ArrayDomainResult r) return [];

        var findings = new List<InsightFinding>(4);

        // ── LOH array pressure ────────────────────────────────────────────────
        if (r.LohArrayBytes >= LohWarningBytes)
        {
            FindingSeverity sev = r.LohArrayBytes >= LohCriticalBytes
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string topType = r.TopLargeArrays.Count > 0
                ? r.TopLargeArrays[0].ElementTypeName
                : "N/A";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"LOH array pressure: {FormatBytes(r.LohArrayBytes)} in {r.LohArrayCount:N0} large arrays",
                Evidence: $"{r.LohArrayCount:N0} arrays reside in the Large Object Heap " +
                          $"consuming {FormatBytes(r.LohArrayBytes)}. " +
                          $"Largest array element type: {topType}.",
                Recommendation: "Large arrays allocated on the LOH are never compacted and fragment the heap " +
                                "over time. Prefer pooled buffers (ArrayPool<T>, MemoryPool<T>) for large temporary " +
                                "arrays, and avoid repeatedly creating and abandoning large arrays.",
                Tags: ["loh", "array", "fragmentation", "memory"],
                MetricValue: r.LohArrayBytes,
                MetricUnit: "bytes"));
        }

        // ── Multi-dimensional array anti-pattern ──────────────────────────────
        if (r.MultiDimArrayCount >= MultiDimWarningCount)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: $"High multi-dimensional array count: {r.MultiDimArrayCount:N0}",
                Evidence: $"{r.MultiDimArrayCount:N0} multi-dimensional (rank ≥ 2) arrays found. " +
                          "Multi-dimensional arrays are significantly slower to access than jagged arrays " +
                          "due to bounds-checking overhead on every access.",
                Recommendation: "Replace multi-dimensional arrays (T[,]) with jagged arrays (T[][]) where " +
                                "performance is critical. Jagged arrays have better cache locality and avoid " +
                                "the CLR's multi-dimensional index calculation overhead.",
                Tags: ["array", "multidim", "performance"],
                MetricValue: r.MultiDimArrayCount,
                MetricUnit: "arrays"));
        }

        // ── Sparse / wasteful arrays ──────────────────────────────────────────
        foreach (SparseArrayEntry sparse in r.TopSparseArrays)
        {
            if (sparse.SparseRatio < SparseRatioThreshold) continue;
            if (sparse.WastedBytes < SparseWastedWarningBytes) continue;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: $"Sparse {sparse.ElementTypeName}[] wastes {FormatBytes(sparse.WastedBytes)}",
                Evidence: $"Array of {sparse.ElementTypeName} at 0x{sparse.Address:X} has {sparse.Length:N0} elements " +
                          $"but {sparse.SparseRatio:P0} are null/default, " +
                          $"wasting approximately {FormatBytes(sparse.WastedBytes)}.",
                Recommendation: "Consider replacing sparse arrays with a Dictionary<int, T> or SortedList<int, T> " +
                                "when the majority of entries are null or default, to reclaim the wasted memory.",
                Tags: ["array", "sparse", "memory", "waste"],
                MetricValue: sparse.WastedBytes,
                MetricUnit: "bytes"));

            break; // report at most one sparse finding per run to avoid noise
        }

        return findings;
    }

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
