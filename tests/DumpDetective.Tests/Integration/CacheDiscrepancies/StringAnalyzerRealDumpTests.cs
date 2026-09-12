using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Types;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for <see cref="StringAnalyzer"/>'s Tier-1+Tier-2 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — proves
/// <see cref="StringAnalyzerLegacyAdapter"/> produces an internally consistent result against a
/// real dump, exercising the shared disk-index scan pass for both string-content dedup (via
/// <see cref="DumpDetective.Sdk.Analysis.IDumpObjectFieldQuery.TryGetStringValue(ulong, int, out string?)"/>)
/// and the P1-3 string-field-ownership reservoir sampling — neither of which the fake-backed
/// characterization tests exercise against real ClrMD data/content. Every real dump has a real,
/// non-empty string population, so an empty result here would itself be the discrepancy, not a
/// tolerated edge case.
/// </summary>
public sealed class StringAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public StringAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using StringAnalyzerLegacyAdapter adapter = new();
        new HeapIndexScanDispatcher().Run(cache, context, [adapter], CancellationToken.None);

        var result = (StringDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("String Analysis");

        // A real dump always has a real, non-empty string population.
        result.TotalStrings.Should().BeGreaterThan(0);
        result.TotalStringMemoryBytes.Should().BeGreaterThan(0UL);
        (result.Gen0StringCount + result.Gen1StringCount + result.Gen2StringCount).Should().BeLessThanOrEqualTo(result.TotalStrings);
        result.PctOfManagedHeap.Should().BeInRange(0.0, 100.0);

        result.DedupSource.Should().Be("IndexScan");
        result.StringsSampled.Should().BeGreaterThan(0);
        result.SamplingCoverage.Should().BeInRange(0.0, 1.0);
        result.SampledUniquePatterns.Should().BeLessThanOrEqualTo(result.StringsSampled);

        for (int i = 1; i < result.TopDuplicates.Count; i++)
            result.TopDuplicates[i].WastedBytes.Should().BeLessThanOrEqualTo(result.TopDuplicates[i - 1].WastedBytes);
        foreach (DuplicateStringSnapshot dup in result.TopDuplicates)
            dup.DominantType.Should().Be("System.String");

        result.VeryLongStrings.Should().OnlyContain(s => s.SizeBytes >= 85_000);

        if (result.TopDuplicateRetentionPaths is not null)
        {
            foreach (DuplicateStringRetentionPath path in result.TopDuplicateRetentionPaths)
                if (path.HasGcRoot)
                    path.RootPath.Should().NotBeNullOrEmpty();
        }

        _output.WriteLine(
            $"TotalStrings={result.TotalStrings} TotalStringMemoryBytes={result.TotalStringMemoryBytes} " +
            $"StringsSampled={result.StringsSampled} SampledUniquePatterns={result.SampledUniquePatterns} " +
            $"DuplicatePatternCount={result.DuplicatePatternCount} DuplicateWastedBytes={result.DuplicateWastedBytes} " +
            $"VeryLongStrings={result.VeryLongStrings.Count} InternedStringCount={result.InternedStringCount} " +
            $"TopStringOwnerTypes={result.TopStringOwnerTypes?.Count ?? 0} " +
            $"TopDuplicateRetentionPaths={result.TopDuplicateRetentionPaths?.Count ?? 0}");
    }
}
