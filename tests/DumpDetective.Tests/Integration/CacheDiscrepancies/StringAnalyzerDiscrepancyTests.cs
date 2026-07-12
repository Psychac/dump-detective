using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

public sealed class StringAnalyzerDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [Fact]
    public async Task StringAnalyzer_DiskVsMemoryMode_AgreeOnSameHeap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        AnalysisOptions analysisOptions = new();
        StringAnalyzer analyzer = new();
        HeapAnalysisCache memCache = new();
        memCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null, HeapIndexPrebuildMode.Memory);
        AnalysisContext memContext = new() { Runtime = runtime, Cache = memCache, AnalysisOptions = analysisOptions };
        StringDomainResult memResult = (StringDomainResult)await analyzer.AnalyzeAsync(memContext, CancellationToken.None);
        string freshDumpPath = dumpPath + ".freshdiskcheck";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null, HeapIndexPrebuildMode.Disk);
            AnalysisContext diskContext = new() { Runtime = runtime, Cache = diskCache, AnalysisOptions = analysisOptions };
            StringDomainResult diskResult = (StringDomainResult)await analyzer.AnalyzeAsync(diskContext, CancellationToken.None);
            diskResult.TotalStrings.Should().Be(memResult.TotalStrings);
            diskResult.TotalStringMemoryBytes.Should().Be(memResult.TotalStringMemoryBytes);
            diskResult.UniqueStrings.Should().Be(memResult.UniqueStrings);
            diskResult.DuplicatePatternCount.Should().Be(memResult.DuplicatePatternCount);
            diskResult.DuplicateWastedBytes.Should().Be(memResult.DuplicateWastedBytes);
            diskResult.DuplicationRatio.Should().Be(memResult.DuplicationRatio);
            diskResult.PctOfManagedHeap.Should().Be(memResult.PctOfManagedHeap);
            diskResult.LohStringBytes.Should().Be(memResult.LohStringBytes);
            diskResult.InternedStringCount.Should().Be(memResult.InternedStringCount);
            diskResult.InternedStringBytes.Should().Be(memResult.InternedStringBytes);
            diskResult.Gen2StringCount.Should().Be(memResult.Gen2StringCount);
            diskResult.Gen2StringBytes.Should().Be(memResult.Gen2StringBytes);
            diskResult.DeduplicationSkipped.Should().Be(memResult.DeduplicationSkipped);
            diskResult.StringsSampled.Should().Be(memResult.StringsSampled);
            diskResult.SamplingCoverage.Should().Be(memResult.SamplingCoverage);
            diskResult.SamplingMode.Should().Be(memResult.SamplingMode);
            diskResult.DeduplicationMode.Should().Be(memResult.DeduplicationMode);
            diskResult.MaxStringsToDedup.Should().Be(memResult.MaxStringsToDedup);
            diskResult.DedupSource.Should().Be(memResult.DedupSource);
            diskResult.DedupSkipReason.Should().Be(memResult.DedupSkipReason);
            diskResult.PreviewMaxLength.Should().Be(memResult.PreviewMaxLength);
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
        }
    }
}
