using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 5 validation for docs/cache/19-ObjectAddressLookupIndex.md: the 6 T1 call sites
/// (<c>CollectionAnalyzer</c> x2, <c>LohFragmentationAnalyzer</c>, <c>HangAnalyzer</c> x2,
/// <c>AsyncTaskAnalyzer</c>) swapped <c>heap.GetObject(address).Type</c> for
/// <c>heap.GetTypeByMethodTable(mt)</c> — same <c>ClrType</c> either way, so this is a smoke test
/// (no exceptions, sane result shape) against a real dump rather than a disk-vs-memory oracle: the
/// classification logic each site feeds into is unchanged and already unit-tested, only how the
/// <c>ClrType</c> is reached changed.
/// </summary>
public sealed class Phase5T1FixesEndToEndDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public Phase5T1FixesEndToEndDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task Phase5TouchedAnalyzers_RunEndToEnd_DiskMode_WithoutExceptions()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        string freshDumpPath = dumpPath + ".freshdiskcheck.Phase5T1FixesEndToEndDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        File.WriteAllBytes(freshDumpPath, new byte[4096]);
        try
        {
            HeapAnalysisCache cache = new();
            cache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);
            var options = new AnalysisOptions();

            var analyzers = new IAnalyzer[]
            {
                new CollectionAnalyzer(),
                new LohFragmentationAnalyzer(),
                new HangAnalyzer(),
                new AsyncTaskAnalyzer(),
            };

            foreach (IAnalyzer analyzer in analyzers)
            {
                var context = new AnalysisContext { Runtime = runtime, Cache = cache, AnalysisOptions = options };
                AnalyzerDomainResult result = await analyzer.AnalyzeAsync(context, CancellationToken.None);
                result.Should().NotBeNull();
                _output.WriteLine($"{analyzer.Name}: {result.GetType().Name} produced successfully");
            }

            cache.Dispose();
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
            if (File.Exists(freshDumpPath))
                File.Delete(freshDumpPath);
        }
    }
}
