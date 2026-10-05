using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Phase 5 step 2's first real <see cref="ISynthesisRule"/> — replaces
/// <c>ArrayFindingGenerator</c>. Same four branches, same constants, same narrative text;
/// verified field-for-field against the old generator in
/// <c>tests/DumpDetective.Tests/Unit/Synthesis/ArraySynthesisRuleParityTests.cs</c>. See
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Deliberately does <b>not</b> re-sort the matched large/sparse/pinned-array observations before
/// picking "the largest"/capping sparse findings at <see cref="MaxSparseFindings"/> — it trusts
/// emission order exactly the way <c>ArrayFindingGenerator</c> trusted <c>ArrayDomainResult</c>'s
/// pre-sorted lists (<c>TopLargeArrays[0]</c>, not an arg-max search; a plain
/// <c>foreach</c>-with-break over <c>TopSparseArrays</c>, not a sort-then-take). In production this
/// is always safe, since <c>ArrayAnalyzer.Analyze()</c> sorts each list before returning and
/// observation emission preserves that order. A synthesis rule re-ranking its own inputs would be
/// more robust in general (per observation-and-correlation-model.md, ranking is a synthesis
/// concern) — caught by <c>ArraySynthesisRuleParityTests</c> re-ordering a fixture and getting a
/// different top-3 than the old generator. That would be a real improvement, but it's exactly the
/// kind the per-analyzer playbook says lands after parity is proven, not during — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's Risk/effort section.
/// </remarks>
internal sealed class ArraySynthesisRule : ISynthesisRule
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

    private const string AnalyzerName = "Array Analysis";

    public string RuleId => "array.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.array-composition", "heap.sparse-array", "heap.large-array", "heap.pinned-array"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var compositions = new List<Observation>();
        var sparseArrays = new List<Observation>();
        var largeArrays = new List<Observation>();
        var pinnedArrays = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.array-composition": compositions.Add(observation); break;
                case "heap.sparse-array": sparseArrays.Add(observation); break;
                case "heap.large-array": largeArrays.Add(observation); break;
                case "heap.pinned-array": pinnedArrays.Add(observation); break;
            }
        }

        var findings = new List<Claim>(4);

        EmitLohPressure(compositions, largeArrays, findings);
        EmitMultiDimAntiPattern(compositions, findings);
        EmitSparseArrays(sparseArrays, findings);
        EmitPinnedAccumulation(compositions, pinnedArrays, findings);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static void EmitLohPressure(List<Observation> compositions, List<Observation> largeArrays, List<Claim> findings)
    {
        ulong lohArrayBytes = 0;
        long lohArrayCount = 0;
        foreach (Observation c in compositions)
        {
            lohArrayBytes += (ulong)c.Measures["loh.bytes"].Value;
            lohArrayCount += (long)c.Measures["loh.count"].Value;
        }

        if (lohArrayBytes < LohWarningBytes) return;

        Severity sev = lohArrayBytes >= LohCriticalBytes ? Severity.Critical : Severity.Warning;
        Observation? largest = largeArrays.Count > 0 ? largeArrays[0] : null;
        string topType = largest is null ? "N/A" : ((TypeRef)largest.Subjects[0]).CanonicalName;

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("loh-array-pressure"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = sev,
            Title = $"LOH array pressure: {FormatBytes(lohArrayBytes)} in {lohArrayCount:N0} large arrays",
            Narrative = $"{lohArrayCount:N0} arrays reside in the Large Object Heap " +
                        $"consuming {FormatBytes(lohArrayBytes)}. " +
                        $"Largest array element type: {topType}.",
            Recommendation = "Large arrays allocated on the LOH are never compacted and fragment the heap " +
                              "over time. Prefer pooled buffers (ArrayPool<T>, MemoryPool<T>) for large temporary " +
                              "arrays, and avoid repeatedly creating and abandoning large arrays.",
            Tags = ["loh", "array", "fragmentation", "memory"],
            MetricValue = lohArrayBytes,
            MetricUnit = "bytes",
            DerivedFrom = compositions.Select(c => c.Id).Concat(largeArrays.Select(l => l.Id)).ToArray(),
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });
    }

    private static void EmitMultiDimAntiPattern(List<Observation> compositions, List<Claim> findings)
    {
        long multiDimCount = 0;
        ulong multiDimBytes = 0;
        var contributing = new List<ObservationId>();
        foreach (Observation c in compositions)
        {
            if (c.Measures["array.rank"].Value < 2) continue;
            multiDimCount += (long)c.Measures["instance.count"].Value;
            multiDimBytes += (ulong)c.Measures["shallow.bytes"].Value;
            contributing.Add(c.Id);
        }

        if (multiDimCount < MultiDimWarningCount && multiDimBytes < MultiDimWarningBytes) return;

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("multidim-array-anti-pattern"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = Severity.Warning,
            Title = $"Multi-dimensional arrays: {multiDimCount:N0} arrays ({FormatBytes(multiDimBytes)})",
            Narrative = $"{multiDimCount:N0} multi-dimensional (rank ≥ 2) arrays found, " +
                        $"consuming {FormatBytes(multiDimBytes)} of heap memory. " +
                        "Multi-dimensional arrays are significantly slower to access than jagged arrays " +
                        "due to bounds-checking overhead on every access.",
            Recommendation = "Replace multi-dimensional arrays (T[,]) with jagged arrays (T[][]) where " +
                              "performance is critical. Jagged arrays have better cache locality and avoid " +
                              "the CLR's multi-dimensional index calculation overhead.",
            Tags = ["array", "multidim", "performance"],
            MetricValue = multiDimBytes,
            MetricUnit = "bytes",
            DerivedFrom = contributing,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });
    }

    private static void EmitSparseArrays(List<Observation> sparseArrays, List<Claim> findings)
    {
        int emitted = 0;
        foreach (Observation s in sparseArrays)
        {
            if (s.Measures["sparse.ratio"].Value < SparseRatioThreshold) continue;
            if ((ulong)s.Measures["wasted.bytes"].Value < SparseWastedWarningBytes) continue;
            if (emitted >= MaxSparseFindings) break;

            string elementTypeName = ((TypeRef)s.Subjects[0]).CanonicalName;
            ulong address = s.Evidence.Count > 0 && ulong.TryParse(s.Evidence[0].Locator, out ulong parsed) ? parsed : 0;
            int length = (int)s.Measures["length"].Value;
            double sparseRatio = s.Measures["sparse.ratio"].Value;
            ulong wastedBytes = (ulong)s.Measures["wasted.bytes"].Value;

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint($"sparse-array-{emitted}"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = $"Sparse {elementTypeName}[] wastes {FormatBytes(wastedBytes)}",
                Narrative = $"Array of {elementTypeName} at 0x{address:X} has {length:N0} elements " +
                            $"but {sparseRatio:P0} are null/default, " +
                            $"wasting approximately {FormatBytes(wastedBytes)}.",
                Recommendation = "Consider replacing sparse arrays with a Dictionary<int, T> or SortedList<int, T> " +
                                  "when the majority of entries are null or default, to reclaim the wasted memory.",
                Tags = ["array", "sparse", "memory", "waste"],
                MetricValue = wastedBytes,
                MetricUnit = "bytes",
                DerivedFrom = [s.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });

            emitted++;
        }
    }

    private static void EmitPinnedAccumulation(List<Observation> compositions, List<Observation> pinnedArrays, List<Claim> findings)
    {
        long pinnedArrayCount = 0;
        ulong pinnedArrayBytes = 0;
        var contributing = new List<ObservationId>();
        foreach (Observation c in compositions)
        {
            long count = (long)c.Measures["pinned.count"].Value;
            if (count == 0) continue;
            pinnedArrayCount += count;
            pinnedArrayBytes += (ulong)c.Measures["pinned.bytes"].Value;
            contributing.Add(c.Id);
        }

        if (pinnedArrayCount < PinnedArrayWarningCount && pinnedArrayBytes < PinnedArrayWarningBytes) return;

        Severity sev = pinnedArrayBytes >= PinnedArrayCriticalBytes ? Severity.Critical : Severity.Warning;
        Observation? largest = pinnedArrays.Count > 0 ? pinnedArrays[0] : null;
        string topType = largest is null ? "N/A" : ((TypeRef)largest.Subjects[0]).CanonicalName;
        if (largest is not null) contributing.Add(largest.Id);

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("pinned-array-accumulation"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = sev,
            Title = $"Pinned array accumulation: {pinnedArrayCount:N0} arrays ({FormatBytes(pinnedArrayBytes)})",
            Narrative = $"{pinnedArrayCount:N0} arrays are targeted by a pinned or async-pinned GC handle, " +
                        $"consuming {FormatBytes(pinnedArrayBytes)}. Pinned objects cannot be moved by the GC, " +
                        "preventing compaction and fragmenting the surrounding heap segment. " +
                        $"Largest pinned array element type: {topType}.",
            Recommendation = "Pinned arrays are typically interop/native I/O buffers (P/Invoke, sockets, " +
                              "overlapped I/O). Minimize pin duration, reuse buffers instead of pinning new " +
                              "arrays repeatedly, and prefer GCHandleType.Pinned only when unavoidable.",
            Tags = ["array", "pinned", "fragmentation", "memory"],
            MetricValue = pinnedArrayBytes,
            MetricUnit = "bytes",
            DerivedFrom = contributing,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
