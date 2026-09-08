using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Validates P3-2 of docs/analysis/phase1/module-analyzer-audit.md: heavy modules
/// (>= HeavyModuleWarningThresholdBytes) carry a bounded, correctly-ordered top-10 type
/// breakdown resolved against the real dump's ClrMD type names.
/// </summary>
public sealed class ModuleAnalyzerTopTypesRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ModuleAnalyzerTopTypesRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task HeavyModules_HaveBoundedCorrectlyOrderedTopTypes()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        // Low threshold guarantees at least one heavy module on any real-world dump, keeping
        // the test deterministic instead of depending on this specific dump's module sizes.
        var options = new AnalysisOptions
        {
            ModuleAnalysis = new ModuleAnalysisOptions { HeavyModuleWarningThresholdBytes = 1UL * 1024 * 1024 }
        };

        var context = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = options
        };

        using ModuleAnalyzer analyzer = new();
        var result = (ModuleDomainResult)await analyzer.AnalyzeAsync(context, CancellationToken.None);

        result.TopModulesByHeapMemory.Should().NotBeNull();
        var heavyModules = result.TopModulesByHeapMemory!
            .Where(m => m.TotalBytes >= result.HeavyModuleWarningThresholdBytes)
            .ToList();

        heavyModules.Should().NotBeEmpty("the low threshold should qualify at least one module on a real dump");

        foreach (ModuleHeapStats module in heavyModules)
        {
            module.TopTypes.Should().NotBeNull($"heavy module {module.ModuleName} should carry a type breakdown");
            module.TopTypes!.Count.Should().BeLessThanOrEqualTo(10);
            module.TopTypes.Should().NotBeEmpty();

            for (int i = 0; i < module.TopTypes.Count; i++)
            {
                module.TopTypes[i].TypeName.Should().NotBeNullOrEmpty();
                module.TopTypes[i].TotalBytes.Should().BeGreaterThan(0);
                if (i > 0)
                    module.TopTypes[i].TotalBytes.Should().BeLessThanOrEqualTo(module.TopTypes[i - 1].TotalBytes);
            }

            ulong topTypesBytes = 0;
            foreach (ModuleTypeUsage usage in module.TopTypes)
                topTypesBytes += usage.TotalBytes;
            topTypesBytes.Should().BeLessThanOrEqualTo(module.TotalBytes);

            _output.WriteLine($"{module.ModuleName}: {module.TopTypes.Count} top types, top type = {module.TopTypes[0].TypeName} ({module.TopTypes[0].TotalBytes:N0} bytes)");
        }

        // Non-heavy modules must not carry a breakdown — keeps the feature bounded to the heavy subset.
        foreach (ModuleHeapStats module in result.TopModulesByHeapMemory!)
        {
            if (module.TotalBytes < result.HeavyModuleWarningThresholdBytes)
                module.TopTypes.Should().BeNull();
        }
    }
}
