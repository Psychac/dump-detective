using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ArrayFindingGenerator : IFindingGenerator
{
    private const ulong LohWarningBytes = 500_000_000UL; // 500 MB
    private const ulong LohCriticalBytes = 2_000_000_000UL; // 2 GB
    private const int MultiDimWarningCount = 1_000;
    private const ulong MultiDimWarningBytes = 100_000_000UL; // 100 MB
    private const double SparseRatioThreshold = 0.70;
    private const ulong SparseWastedWarningBytes = 10_000_000UL; // 10 MB
    private const int MaxSparseFindings = 3;
    private const int PinnedArrayWarningCount = 100;
    private const ulong PinnedArrayWarningBytes = 50_000_000UL; // 50 MB
    private const ulong PinnedArrayCriticalBytes = 200_000_000UL; // 200 MB

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
        if (r.MultiDimArrayCount >= MultiDimWarningCount || r.MultiDimArrayBytes >= MultiDimWarningBytes)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: $"Multi-dimensional arrays: {r.MultiDimArrayCount:N0} arrays ({FormatBytes(r.MultiDimArrayBytes)})",
                Evidence: $"{r.MultiDimArrayCount:N0} multi-dimensional (rank ≥ 2) arrays found, " +
                          $"consuming {FormatBytes(r.MultiDimArrayBytes)} of heap memory. " +
                          "Multi-dimensional arrays are significantly slower to access than jagged arrays " +
                          "due to bounds-checking overhead on every access.",
                Recommendation: "Replace multi-dimensional arrays (T[,]) with jagged arrays (T[][]) where " +
                                "performance is critical. Jagged arrays have better cache locality and avoid " +
                                "the CLR's multi-dimensional index calculation overhead.",
                Tags: ["array", "multidim", "performance"],
                MetricValue: r.MultiDimArrayBytes,
                MetricUnit: "bytes"));
        }

        // ── Sparse / wasteful arrays ──────────────────────────────────────────
        int sparseFindingCount = 0;
        foreach (SparseArrayEntry sparse in r.TopSparseArrays)
        {
            if (sparse.SparseRatio < SparseRatioThreshold) continue;
            if (sparse.WastedBytes < SparseWastedWarningBytes) continue;
            if (sparseFindingCount >= MaxSparseFindings) break;

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

            sparseFindingCount++;
        }

        // ── Pinned array accumulation ──────────────────────────────────────────
        if (r.PinnedArrayCount >= PinnedArrayWarningCount || r.PinnedArrayBytes >= PinnedArrayWarningBytes)
        {
            FindingSeverity sev = r.PinnedArrayBytes >= PinnedArrayCriticalBytes
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string topType = r.TopPinnedArrays is { Count: > 0 } pinned
                ? pinned[0].ElementTypeName
                : "N/A";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"Pinned array accumulation: {r.PinnedArrayCount:N0} arrays ({FormatBytes(r.PinnedArrayBytes)})",
                Evidence: $"{r.PinnedArrayCount:N0} arrays are targeted by a pinned or async-pinned GC handle, " +
                          $"consuming {FormatBytes(r.PinnedArrayBytes)}. Pinned objects cannot be moved by the GC, " +
                          $"preventing compaction and fragmenting the surrounding heap segment. " +
                          $"Largest pinned array element type: {topType}.",
                Recommendation: "Pinned arrays are typically interop/native I/O buffers (P/Invoke, sockets, " +
                                "overlapped I/O). Minimize pin duration, reuse buffers instead of pinning new " +
                                "arrays repeatedly, and prefer GCHandleType.Pinned only when unavoidable.",
                Tags: ["array", "pinned", "fragmentation", "memory"],
                MetricValue: r.PinnedArrayBytes,
                MetricUnit: "bytes"));
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
