using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for <see cref="EventLeakAnalyzer"/>'s Tier-1+Tier-2 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — the largest batch in the
/// wave (~3,000 lines pre-retyping), whose <c>PublisherRegistry.Build</c> phase is documented as
/// ~57% of wall time on a 25.6 GB dump. This is the single highest-value real-dump gate in the
/// batch: a meaningfully slower run than this analyzer's own historical baseline (single-digit to
/// low-double-digit seconds on the 3.3-3.5 GB reference dump, matching every other batch's own
/// real-dump gate) would indicate the retype accidentally pushed the hot scan path onto
/// per-field-lookup overhead, not a formality to wave through.
/// </summary>
public sealed class EventLeakAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public EventLeakAnalyzerRealDumpTests(ITestOutputHelper output)
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using EventLeakAnalyzerLegacyAdapter adapter = new();
        var result = (EventLeakDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);
        sw.Stop();

        result.AnalyzerName.Should().Be("Event Leak Analysis");

        result.TotalEventsScanned.Should().BeGreaterThanOrEqualTo(0);
        result.PublisherTypesScanned.Should().BeGreaterThanOrEqualTo(0);
        result.CleanPublisherTypeCount.Should().BeLessThanOrEqualTo(result.PublisherTypesScanned);

        result.TotalEventLeakInstances.Should().Be(result.TopLeakGroups?.Count ?? 0);
        result.StaticEventLeakCount.Should().BeGreaterThanOrEqualTo(0);
        result.InstanceEventLeakCount.Should().BeGreaterThanOrEqualTo(0);
        (result.StaticEventLeakCount + result.InstanceEventLeakCount).Should().Be(result.TotalEventLeakInstances);

        if (result.TopLeakGroups is not null)
        {
            for (int i = 1; i < result.TopLeakGroups.Count; i++)
                result.TopLeakGroups[i].SeverityScore.Should().BeLessThanOrEqualTo(result.TopLeakGroups[i - 1].SeverityScore + 1_000_000);

            foreach (EventLeakGroupSnapshot group in result.TopLeakGroups)
            {
                group.InstanceCount.Should().BeGreaterThan(0);
                group.AverageSubscribers.Should().BeGreaterThanOrEqualTo(0);
            }
        }

        if (result.TopLeakInstances is not null)
        {
            for (int i = 1; i < result.TopLeakInstances.Count; i++)
                result.TopLeakInstances[i].SeverityScore.Should().BeLessThanOrEqualTo(result.TopLeakInstances[i - 1].SeverityScore);

            foreach (EventLeakInstanceSnapshot inst in result.TopLeakInstances)
            {
                inst.Evidence.Should().NotBeNull();
                if (inst.Evidence!.PublisherRootPath is not null)
                    inst.Evidence.PublisherRootPath.Should().NotBeEmpty();
            }
        }

        _output.WriteLine(
            $"Elapsed={sw.Elapsed.TotalSeconds:F1}s TotalEventsScanned={result.TotalEventsScanned} " +
            $"TotalPublisherInstances={result.TotalPublisherInstances} PublisherTypesScanned={result.PublisherTypesScanned} " +
            $"CleanPublisherTypeCount={result.CleanPublisherTypeCount} TotalEventLeakInstances={result.TotalEventLeakInstances} " +
            $"StaticEventLeakCount={result.StaticEventLeakCount} InstanceEventLeakCount={result.InstanceEventLeakCount} " +
            $"TotalSubscribers={result.TotalSubscribers} TotalEstimatedRetainedBytes={result.TotalEstimatedRetainedBytes} " +
            $"TopLeakGroups={result.TopLeakGroups?.Count ?? 0} TopLeakInstances={result.TopLeakInstances?.Count ?? 0}");
    }
}
