using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization tests for the Tier-1 retyping of <see cref="LeakCandidateAnalyzer"/>
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Covers classification
/// priority ordering, scoring, the exact-index gate, and root-chain enrichment (direct-root
/// shortcut and root-path-search fallback) against fake, dictionary-backed capability
/// implementations. The cross-analyzer extraction itself
/// (<see cref="LeakCandidateAnalyzer.BuildSdkContext"/> reading
/// <c>context.CompletedRunResults</c>) is exercised end-to-end only by the real-dump test, since
/// building a full legacy pipeline run here would duplicate that coverage without a fake.
/// </summary>
public sealed class LeakCandidateAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_NoExactIndex_ReturnsEmptyResult()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery(hasExactGenerationData: false);
        typeStats.SetType("MyApp.Big", instanceCount: 1, totalSize: 200 * 1024 * 1024, gen2Count: 1, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TotalCandidates.Should().Be(0);
        result.TopCandidates.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_TypeWithNoSampleAddress_ExcludedFromCandidates()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Orphan", instanceCount: 1, totalSize: 100, gen2Count: 0, sampleAddress: 0);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TotalCandidates.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadLocalNameMatch_ClassifiedAsThreadLocalLeak_EvenIfAlsoStaticRooted()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.ThreadLocalCache", instanceCount: 1, totalSize: 100, gen2Count: 1, sampleAddress: 0x1000);

        var heapRoots = new FakeHeapRootQuery();
        heapRoots.AddStaticRoot(0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats, heapRoots: heapRoots);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.ThreadLocalLeak);
    }

    [Fact]
    public async Task AnalyzeAsync_DependentHandleTarget_ClassifiedAsDependentHandleLeak()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Dependent", instanceCount: 1, totalSize: 100, gen2Count: 1, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats, dependentTargetTypes: new HashSet<string> { "MyApp.Dependent" });

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.DependentHandleLeak);
    }

    [Fact]
    public async Task AnalyzeAsync_PinnedHandleTarget_ClassifiedAsGCHandleRetention()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Pinned", instanceCount: 1, totalSize: 100, gen2Count: 1, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats, pinnedTargetTypes: new HashSet<string> { "MyApp.Pinned" });

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.GCHandleRetention);
    }

    [Fact]
    public async Task AnalyzeAsync_StaticRootedSample_ClassifiedAsStaticRetention()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Cache", instanceCount: 1, totalSize: 100, gen2Count: 1, sampleAddress: 0x1000);

        var heapRoots = new FakeHeapRootQuery();
        heapRoots.AddStaticRoot(0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats, heapRoots: heapRoots);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.StaticRetention);
        result.TopCandidates[0].RootKind.Should().Be("StaticRoot");
    }

    [Fact]
    public async Task AnalyzeAsync_NamedTimerWrapperOverWarningThreshold_ClassifiedAsTimerLeak()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("System.Threading.Timer", instanceCount: 1, totalSize: 100, gen2Count: 1, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats, logicalTimerCount: 150);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.TimerLeak);
    }

    [Fact]
    public async Task AnalyzeAsync_NamedTimerWrapperUnderThreshold_NotClassifiedAsTimerLeak()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("System.Threading.Timer", instanceCount: 1, totalSize: 100, gen2Count: 1, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats, logicalTimerCount: 50);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().NotBe(LeakClass.TimerLeak);
    }

    [Fact]
    public async Task AnalyzeAsync_DelegateType_ClassifiedAsEventLeak()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Handler", instanceCount: 1, totalSize: 100, gen2Count: 1, sampleAddress: 0x1000, isDelegateType: true);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.EventLeak);
    }

    [Fact]
    public async Task AnalyzeAsync_FinalizableWithHighGen2Fraction_ClassifiedAsFinalizerRetention()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Finalizable", instanceCount: 10, totalSize: 1000, gen2Count: 8, sampleAddress: 0x1000, isFinalizableType: true);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.FinalizerRetention);
    }

    [Fact]
    public async Task AnalyzeAsync_ContainerTypeWithHighGen2Fraction_ClassifiedAsCacheLeak()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.WidgetCache", instanceCount: 10, totalSize: 1000, gen2Count: 8, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.CacheLeak);
    }

    [Fact]
    public async Task AnalyzeAsync_NothingMatches_ClassifiedAsUnknown()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.PlainObject", instanceCount: 1, totalSize: 100, gen2Count: 0, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TopCandidates.Should().ContainSingle().Which.Classification.Should().Be(LeakClass.Unknown);
    }

    [Fact]
    public async Task AnalyzeAsync_HighGen2AndLargeSize_ScoresHigherAndSortsFirst()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Small", instanceCount: 1, totalSize: 10, gen2Count: 0, sampleAddress: 0x1000);
        typeStats.SetType("MyApp.BigHotType", instanceCount: 100, totalSize: 200UL * 1024 * 1024, gen2Count: 90, sampleAddress: 0x2000);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TopCandidates[0].TypeName.Should().Be("MyApp.BigHotType");
        result.TopCandidates[0].SuspicionScore.Should().BeGreaterThan(result.TopCandidates[1].SuspicionScore);
    }

    [Fact]
    public async Task AnalyzeAsync_DirectRootMatch_UsesGCRootShortcutForRootChain()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Cache", instanceCount: 1, totalSize: 1000, gen2Count: 1, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache");

        var directRoots = new Dictionary<ulong, DirectRootEntry>
        {
            [0x1000] = new DirectRootEntry(0x1000, "StaticVar", "MyApp.Registry._instance"),
        };

        LeakCandidateDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, directRootsByTargetAddress: directRoots);

        result.TopCandidates.Should().ContainSingle().Which.RootChain
            .Should().Be("StaticVar MyApp.Registry._instance -> MyApp.Cache@0x1000");
    }

    [Fact]
    public async Task AnalyzeAsync_NoDirectRoot_FallsBackToRootPathSearch()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Cache", instanceCount: 1, totalSize: 1000, gen2Count: 1, sampleAddress: 0x1000);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar: MyApp.Registry@0x2000 -> MyApp.Cache@0x1000");

        LeakCandidateDomainResult result = await Analyze(typeStats, rootPaths: rootPaths);

        result.TopCandidates.Should().ContainSingle().Which.RootChain.Should().Be("StaticVar: MyApp.Registry@0x2000 -> MyApp.Cache@0x1000");
    }

    [Fact]
    public async Task AnalyzeAsync_NoDirectRootAndNoPathFound_RootChainStaysNull()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Orphaned", instanceCount: 1, totalSize: 1000, gen2Count: 1, sampleAddress: 0x1000);

        LeakCandidateDomainResult result = await Analyze(typeStats);

        result.TopCandidates.Should().ContainSingle().Which.RootChain.Should().BeNull();
    }

    private static async Task<LeakCandidateDomainResult> Analyze(
        FakeHeapTypeStatisticsQuery typeStatistics,
        FakeHeapRootQuery? heapRoots = null,
        FakeHeapObjectLookup? objectLookup = null,
        FakeHeapRootPathQuery? rootPaths = null,
        IReadOnlySet<string>? pinnedTargetTypes = null,
        IReadOnlySet<string>? dependentTargetTypes = null,
        int logicalTimerCount = 0,
        IReadOnlyDictionary<ulong, DirectRootEntry>? directRootsByTargetAddress = null)
    {
        var analyzer = new LeakCandidateAnalyzer();
        var options = new LeakCandidateAnalyzerOptions(
            new ReferenceChainOptions(),
            pinnedTargetTypes ?? new HashSet<string>(),
            dependentTargetTypes ?? new HashSet<string>(),
            logicalTimerCount,
            directRootsByTargetAddress);

        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new DumpDetective.Sdk.Artifacts.ArtifactId("test-artifact"),
            HeapTypeStatistics = typeStatistics,
            HeapRoots = heapRoots ?? new FakeHeapRootQuery(),
            HeapObjectLookup = objectLookup ?? new FakeHeapObjectLookup(),
            HeapRootPaths = rootPaths ?? new FakeHeapRootPathQuery(),
            AnalyzerOptions = options,
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (LeakCandidateDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeHeapTypeStatisticsQuery(bool hasExactGenerationData = true) : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetType(
            string typeName, long instanceCount, ulong totalSize, long gen2Count, ulong sampleAddress,
            bool isFinalizableType = false, bool isDelegateType = false, int totalFields = 0, int referenceFields = 0) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null),
                InstanceCount: instanceCount,
                TotalSize: totalSize,
                LohCount: 0,
                LohSize: 0,
                Gen0Count: 0,
                Gen1Count: 0,
                Gen2Count: gen2Count,
                Gen2TotalSize: 0,
                IsFinalizableType: isFinalizableType,
                SampleAddress: sampleAddress,
                TotalFields: totalFields,
                ReferenceFields: referenceFields,
                IsDelegateType: isDelegateType);

        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => _stats;
        public bool HasExactGenerationData => hasExactGenerationData;
        public bool HasTypeShapeData => true;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }

    private sealed class FakeHeapRootQuery : IHeapRootQuery
    {
        private readonly List<HeapRootRef> _roots = new();

        public void AddStaticRoot(ulong targetAddress) =>
            _roots.Add(new HeapRootRef { Kind = HeapRootKind.StaticVar, TargetAddress = targetAddress, RootAddress = targetAddress + 0x1000 });

        public IEnumerable<HeapRootRef> EnumerateRoots() => _roots;

        public bool TryResolveStackFrameOwner(ulong rootAddress, out string ownerTypeName, out string methodName)
        {
            ownerTypeName = string.Empty;
            methodName = string.Empty;
            return false;
        }
    }

    private sealed class FakeHeapObjectLookup : IHeapObjectLookup
    {
        private readonly Dictionary<ulong, string> _objects = new();

        public void SetObject(ulong address, string typeName) => _objects[address] = typeName;

        public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
        {
            if (_objects.TryGetValue(address, out string? typeName))
            {
                heapObject = new HeapObjectRef(address, SdkTypeRefFactory.FromName(typeName, null), 0, typeName);
                return true;
            }

            heapObject = default;
            return false;
        }
    }

    private sealed class FakeHeapRootPathQuery : IHeapRootPathQuery
    {
        private readonly Dictionary<ulong, string> _paths = new();

        public void SetPath(ulong address, string formattedPath) => _paths[address] = formattedPath;

        public bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated)
        {
            searchTruncated = false;
            return _paths.TryGetValue(targetAddress, out formattedPath);
        }

        public bool TryFindRootPathHops(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<string>? hopTypeNames, out bool searchTruncated)
        {
            rootKind = null;
            hopTypeNames = null;
            searchTruncated = false;
            return false;
        }

        public bool TryFindRootPathAddresses(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<ulong>? hopAddresses, out bool searchTruncated)
        {
            rootKind = null;
            hopAddresses = null;
            searchTruncated = false;
            return false;
        }
    }
}
