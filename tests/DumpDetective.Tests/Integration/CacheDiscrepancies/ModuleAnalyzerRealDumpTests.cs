using DumpDetective.Analysis.Pipeline;
using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for <see cref="ModuleAnalyzer"/>'s Tier-1+Tier-2 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), covering the parts
/// <c>ModuleAnalyzerTopTypesRealDumpTests</c> doesn't: module inventory, AppDomain analysis, and
/// the AssemblyRef-vs-loaded-version audit (<see cref="DumpDetective.Sdk.Analysis.IRuntimeModuleQuery.GetAssemblyReferences"/>,
/// the one genuinely new dump-only capability this batch added). A real dump always has a real,
/// non-empty module population, so an empty result here would itself be the discrepancy.
/// </summary>
public sealed class ModuleAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ModuleAnalyzerRealDumpTests(ITestOutputHelper output)
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

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using ModuleAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (ModuleDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Module Analysis");

        // A real dump always has a real, non-empty module population.
        result.TotalModules.Should().BeGreaterThan(0);
        result.UniqueModuleNames.Should().BeGreaterThan(0);
        result.UniqueModuleNames.Should().BeLessThanOrEqualTo(result.TotalModules);
        result.TopModulesBySize.Should().NotBeEmpty();

        result.TotalDomains.Should().BeGreaterThan(0);
        result.Domains.Should().NotBeNull();
        result.Domains!.Count.Should().Be(result.TotalDomains);
        foreach (AppDomainSnapshot domain in result.Domains!)
            domain.ModuleCount.Should().BeGreaterThanOrEqualTo(0);

        result.TopModulesByTypeCount.Should().NotBeNull();
        foreach (ModuleTypeCountEntry entry in result.TopModulesByTypeCount!)
            entry.LiveTypeCount.Should().BeLessThanOrEqualTo(entry.TypeCount);
        for (int i = 1; i < result.TopModulesByTypeCount!.Count; i++)
            result.TopModulesByTypeCount[i].TypeCount.Should().BeLessThanOrEqualTo(result.TopModulesByTypeCount[i - 1].TypeCount);

        if (result.CrossDomainModuleLoads is not null)
            foreach (CrossDomainModuleLoad load in result.CrossDomainModuleLoads)
                load.DomainCount.Should().BeGreaterThan(1);

        // AssemblyRef audit -- the AssemblyReferences primitive is the one genuinely new dump-only
        // capability this batch added, so a mismatch (if any) must reference a real loaded module.
        if (result.AssemblyRefVersionMismatches is not null)
        {
            foreach (AssemblyRefVersionMismatch mismatch in result.AssemblyRefVersionMismatches)
            {
                mismatch.RequiringModule.Should().NotBeNullOrEmpty();
                mismatch.RequiredAssemblyName.Should().NotBeNullOrEmpty();
                mismatch.RequiredVersion.Should().NotBeNullOrEmpty();
            }
        }

        _output.WriteLine(
            $"TotalModules={result.TotalModules} UniqueModuleNames={result.UniqueModuleNames} " +
            $"VersionConflictGroups={result.VersionConflictGroups} TotalDomains={result.TotalDomains} " +
            $"TotalDynamicModules={result.TotalDynamicModules} AnonymousModuleCount={result.AnonymousModuleCount} " +
            $"CrossDomainModuleLoads={result.CrossDomainModuleLoads?.Count ?? 0} " +
            $"AssemblyRefVersionMismatches={result.AssemblyRefVersionMismatches?.Count ?? 0} " +
            $"TopModulesByHeapMemory={result.TopModulesByHeapMemory?.Count ?? 0} " +
            $"HeavyTypeDensityModules={result.HeavyTypeDensityModules?.Count ?? 0}");
    }
}
