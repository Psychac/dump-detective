using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Analyzers.EventLeak;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// <see cref="PublisherRegistry.Build"/>/<see cref="PublisherRegistry.TryGetDescriptors"/>
/// correctness against a real heap (Phase 3, design §3). Gated on a benchmark dump since
/// <see cref="PublisherRegistry.Build"/> requires a real <see cref="ClrHeap"/> module walk —
/// there is no in-memory fake for <c>ClrModule.EnumerateTypeDefToMethodTableMap</c>.
/// </summary>
public sealed class PublisherRegistryTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP") ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void Build_ProducesSameDescriptorsAsFreshBuild_Deterministic()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        PublisherRegistry first = PublisherRegistry.Build(heap, cache);
        PublisherRegistry second = PublisherRegistry.Build(heap, cache);

        int matched = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null) continue;

            bool firstHas = first.TryGetDescriptors(obj.Type.MethodTable, out EventFieldDescriptor[]? firstDescriptors);
            bool secondHas = second.TryGetDescriptors(obj.Type.MethodTable, out EventFieldDescriptor[]? secondDescriptors);

            firstHas.Should().Be(secondHas, "the module walk is deterministic and must produce the same descriptor presence across independent builds");

            if (firstHas)
            {
                secondDescriptors!.Length.Should().Be(firstDescriptors!.Length);
                matched++;
                if (matched >= 50) break;
            }
        }
    }

    [DiscrepancyFact]
    public void Build_DelegateOffsets_AreSharedAcrossRegistryAndSameForBothBuilds()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        PublisherRegistry first = PublisherRegistry.Build(heap, cache);
        PublisherRegistry second = PublisherRegistry.Build(heap, cache);

        second.DelegateTargetOffset.Should().Be(first.DelegateTargetOffset);
        second.DelegateInvocationListOffset.Should().Be(first.DelegateInvocationListOffset);
        second.DelegateInvocationCountOffset.Should().Be(first.DelegateInvocationCountOffset);

        first.DelegateTargetOffset.Should().BeGreaterThan(0, "the delegate _target field must be discovered at a non-zero offset");
        first.DelegateInvocationListOffset.Should().BeGreaterThan(0, "the delegate _invocationList field must be discovered at a non-zero offset");
    }

    [DiscrepancyFact]
    public void TryGetDescriptors_UnknownMethodTable_ReturnsFalse()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        PublisherRegistry registry = PublisherRegistry.Build(heap, cache);

        registry.TryGetDescriptors(0, out EventFieldDescriptor[]? descriptors).Should().BeFalse();
        descriptors.Should().BeNull();
    }

    [DiscrepancyFact]
    public void EventNames_SharedInstance_CachesAcrossRepeatedLookups()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        PublisherRegistry registry = PublisherRegistry.Build(heap, cache);

        ClrType? anyType = null;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null) continue;
            anyType = obj.Type;
            break;
        }
        anyType.Should().NotBeNull();

        HashSet<string> first = registry.EventNames.GetEventNames(anyType!);
        HashSet<string> second = registry.EventNames.GetEventNames(anyType!);

        second.Should().BeSameAs(first, "EventNameResolver must cache per-type results instead of recomputing the add/remove-pair walk on every call");
    }

    /// <summary>
    /// Phase 4 regression (design §6): statics must leave <see cref="EventLeakFastScanner"/>'s
    /// hot path entirely so <c>EventLeakAnalyzer.SweepRegistryStatics</c> is the only place a
    /// static event field is ever accumulated. Before Phase 4, the hot path
    /// (<c>SweepModuleStaticFields</c>'s independent module walk) and the post-scan sweep both
    /// processed static fields, and the old dedup set (<c>processedStaticMTs</c>) was accepted
    /// but never consulted — so a type with both live instances and a static event field was
    /// double-counted. This test proves the fix structurally: running the fast scanner alone,
    /// with no static sweep afterward, must produce zero static groups — there is nothing left
    /// for a sweep to double up against.
    /// </summary>
    [DiscrepancyFact]
    public void FastScanner_Scan_Alone_ProducesNoStaticGroups()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        PublisherRegistry registry = PublisherRegistry.Build(heap, cache);
        registry.StaticPublisherMTs.Should().NotBeEmpty("the reference dump must contain at least one static event publisher for this regression to be meaningful");

        var scanner = new EventLeakFastScanner(heap, registry);
        var groupAcc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), EventLeakAnalyzer.GroupAccumulator>();
        var rootHints = new Dictionary<ulong, string>();
        var options = new EventLeakOptions();
        int eventsScanned = 0;
        int publisherInstances = 0;
        var leakingMTs = new HashSet<ulong>();

        scanner.Scan(cache.EnumerateIndexedEntries(), groupAcc, rootHints, options, leakingMTs,
            ref eventsScanned, ref publisherInstances);

        groupAcc.Keys.Should().NotContain(key => key.IsStatic,
            "static descriptors are skipped entirely by EventLeakFastScanner (design §6) — only SweepRegistryStatics may add static groups, so a bare scan must produce none");
    }

    /// <summary>
    /// P2-1 regression (docs/analysis/phase1/eventleak-analyzer-audit.md): Pass 2 used to gate on
    /// <c>cache is not null</c> alone when deciding how to populate <c>liveMts</c>. A real
    /// <see cref="HeapAnalysisCache"/> can exist without a disk index ever having been built on
    /// it — <see cref="HeapAnalysisCache.EnumerateIndexedEntriesAsTuples"/> silently yields
    /// nothing in that case rather than throwing, so the old check produced an empty
    /// <c>liveMts</c> set and zero instance-field descriptors. The fix mirrors
    /// <c>EventLeakAnalyzer.FindEventLeaks</c>'s own check (<c>cache is HeapAnalysisCache hc
    /// &amp;&amp; hc.TryGetHeapIndex(out _)</c>) so Pass 2 falls back to a raw heap walk instead.
    /// </summary>
    [DiscrepancyFact]
    public void Build_CacheWithoutPrebuiltIndex_StillDiscoversInstanceDescriptors()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;
        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        // Deliberately do NOT call cache.PrebuildHeapIndex — this cache exists (is not null) but
        // has no disk-backed index, which is exactly the condition the old "cache is not null"
        // check got wrong.
        HeapAnalysisCache unindexedCache = new();
        PublisherRegistry unindexedRegistry = PublisherRegistry.Build(heap, unindexedCache);

        HeapAnalysisCache indexedCache = new();
        indexedCache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        PublisherRegistry indexedRegistry = PublisherRegistry.Build(heap, indexedCache);

        int matchedInstanceDescriptor = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null) continue;

            bool unindexedHas = unindexedRegistry.TryGetDescriptors(obj.Type.MethodTable, out EventFieldDescriptor[]? unindexedDescriptors);
            bool indexedHas = indexedRegistry.TryGetDescriptors(obj.Type.MethodTable, out EventFieldDescriptor[]? indexedDescriptors);

            unindexedHas.Should().Be(indexedHas,
                "Pass 2's live-instance walk must find the same candidate MTs whether or not a disk index happens to exist");

            if (unindexedHas && !indexedDescriptors!.All(d => d.IsStatic))
            {
                matchedInstanceDescriptor++;
                if (matchedInstanceDescriptor >= 20) break;
            }
        }

        matchedInstanceDescriptor.Should().BeGreaterThan(0,
            "the reference dump must contain at least one live instance-field publisher for this regression to be meaningful — a return to the old bug would silently make this zero");
    }
}
