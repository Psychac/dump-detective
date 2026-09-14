using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization tests for the Tier-1 retyping of <see cref="ReferenceChainAnalyzer"/>
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). No pre-existing per-analyzer
/// unit test coverage existed before this — this fake-backed suite carries essentially all the real
/// behavioral coverage for this batch: top-type selection, root-path retention counting, E-2 exact
/// retained bytes, E-3 root/last-hop field-name resolution, E-6 shared-root grouping, and E-7
/// multi-sample root-consistency scoring.
/// </summary>
public sealed class ReferenceChainAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_NoTypes_ReturnsEmptyResult()
    {
        ReferenceChainDomainResult result = await Analyze(new FakeHeapTypeStatisticsQuery());

        result.AnalyzedSamples.Should().Be(0);
        result.RetainedSamples.Should().Be(0);
        result.TopTypeSampleTraces.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_TypeWithNoSampleAddress_CountsAsNoSampleAddress()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Orphan", instanceCount: 3, totalSize: 300, sampleAddress: 0);

        ReferenceChainDomainResult result = await Analyze(typeStats);

        result.NoSampleAddressCount.Should().Be(1);
        result.AnalyzedSamples.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_SampleWithNoRootPath_NotRetained()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Widget", instanceCount: 1, totalSize: 100, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Widget", size: 100);

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup);

        result.AnalyzedSamples.Should().Be(1);
        result.RetainedSamples.Should().Be(0);
        result.TopTypeSampleTraces.Should().ContainSingle().Which.HasGcRoot.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_SampleWithRootPath_IsRetained_AndRootKindCounted()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Cache", instanceCount: 1, totalSize: 1000, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 1000);
        objectLookup.SetObject(0x2000, "MyApp.Registry", size: 50);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar", [0x2000, 0x1000]);

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, rootPaths: rootPaths);

        result.RetainedSamples.Should().Be(1);
        result.RetainedPercent.Should().Be(100);
        result.RootKindDistribution.Should().ContainSingle(k => k.RootKind == "StaticVar" && k.RetainedTypeCount == 1);
        result.SampleReferenceChains.Should().ContainSingle(c => c.Contains("MyApp.Cache") && c.Contains("StaticVar"));

        ReferenceTypeSampleSnapshot trace = result.TopTypeSampleTraces.Should().ContainSingle().Subject;
        trace.RootAddress.Should().Be(0x2000);
        trace.RootPath.Should().Contain("MyApp.Registry@0x2000").And.Contain("MyApp.Cache@0x1000");
        trace.PathHops.Should().Equal("MyApp.Registry@0x2000", "MyApp.Cache@0x1000");
    }

    [Fact]
    public async Task AnalyzeAsync_ExactDominatorTreeAvailable_UsesExactRetainedBytes()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.BigThing", instanceCount: 1, totalSize: 500, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.BigThing", size: 500);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 123_456);

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, dominators: dominators);

        result.TopTypeSampleTraces.Should().ContainSingle().Which.RetainedBytes.Should().Be(123_456UL);
    }

    [Fact]
    public async Task AnalyzeAsync_StaticRoot_ResolvesFieldNameFromHeapRootQuery()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Cache", instanceCount: 1, totalSize: 1000, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 1000);
        objectLookup.SetObject(0x2000, "MyApp.Registry", size: 50);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar", [0x2000, 0x1000]);

        var heapRoots = new FakeHeapRootQuery();
        heapRoots.AddStaticRoot(targetAddress: 0x2000, rootAddress: 0x3000, ownerType: "MyApp.Registry", fieldName: "_instance");

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, rootPaths: rootPaths, heapRoots: heapRoots);

        result.TopTypeSampleTraces.Should().ContainSingle().Which.RootFieldName.Should().Be("MyApp.Registry._instance");
    }

    [Fact]
    public async Task AnalyzeAsync_StaticRoot_NonDefaultAppDomain_IncludesSuffix()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Cache", instanceCount: 1, totalSize: 1000, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 1000);
        objectLookup.SetObject(0x2000, "MyApp.Registry", size: 50);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar", [0x2000, 0x1000]);

        var heapRoots = new FakeHeapRootQuery();
        heapRoots.AddStaticRoot(targetAddress: 0x2000, rootAddress: 0x3000, ownerType: "MyApp.Registry", fieldName: "_instance", appDomainId: 2);

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, rootPaths: rootPaths, heapRoots: heapRoots);

        result.TopTypeSampleTraces.Should().ContainSingle().Which.RootFieldName.Should().Be("MyApp.Registry._instance [AppDomain#2]");
    }

    [Fact]
    public async Task AnalyzeAsync_StackRoot_ResolvesFrameOwnerViaTryResolveStackFrameOwner()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Local", instanceCount: 1, totalSize: 100, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Local", size: 100);
        objectLookup.SetObject(0x2000, "MyApp.StackSlot", size: 8);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "Stack", [0x2000, 0x1000]);

        var heapRoots = new FakeHeapRootQuery();
        heapRoots.AddStackRoot(targetAddress: 0x2000, rootAddress: 0x4000, ownerType: "MyApp.Worker", methodName: "Run");

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, rootPaths: rootPaths, heapRoots: heapRoots);

        result.TopTypeSampleTraces.Should().ContainSingle().Which.RootFieldName.Should().Be("MyApp.Worker.Run");
    }

    [Fact]
    public async Task AnalyzeAsync_PathWithAtLeastTwoHops_ResolvesLastHopFieldName()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Cache", instanceCount: 1, totalSize: 1000, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 1000);
        objectLookup.SetObject(0x2000, "MyApp.Registry", size: 50);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar", [0x2000, 0x1000]);

        var references = new FakeHeapReferenceQuery();
        references.SetFieldName(0x2000, 0x1000, "_items");

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, rootPaths: rootPaths, references: references);

        result.TopTypeSampleTraces.Should().ContainSingle().Which.LastHopFieldName.Should().Be("_items");
    }

    [Fact]
    public async Task AnalyzeAsync_SinglePathHop_NoLastHopFieldNameLookup()
    {
        // The root points directly at the sample (root == target) -- a one-hop path has no
        // "parent" to resolve a field name from.
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Root", instanceCount: 1, totalSize: 100, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Root", size: 100);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StrongHandle", [0x1000]);

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, rootPaths: rootPaths);

        result.TopTypeSampleTraces.Should().ContainSingle().Which.LastHopFieldName.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_TwoTypesShareSameRootAddress_GroupedAsSharedRoot()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.CacheEntryA", instanceCount: 1, totalSize: 100, sampleAddress: 0x1000);
        typeStats.SetType("MyApp.CacheEntryB", instanceCount: 1, totalSize: 100, sampleAddress: 0x1100);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.CacheEntryA", size: 100);
        objectLookup.SetObject(0x1100, "MyApp.CacheEntryB", size: 100);
        objectLookup.SetObject(0x2000, "MyApp.SharedCache", size: 50);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar", [0x2000, 0x1000]);
        rootPaths.SetPath(0x1100, "StaticVar", [0x2000, 0x1100]);

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, rootPaths: rootPaths);

        ReferenceChainSharedRootGroup group = result.SharedRootGroups.Should().ContainSingle().Subject;
        group.RootAddress.Should().Be(0x2000);
        group.TypeNames.Should().BeEquivalentTo("MyApp.CacheEntryA", "MyApp.CacheEntryB");
    }

    [Fact]
    public async Task AnalyzeAsync_MultiSampleCollection_ScoresRootConsistencyAcrossAdditionalInstances()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Widget", instanceCount: 3, totalSize: 300, sampleAddress: 0x1000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Widget", size: 100);
        objectLookup.SetObject(0x1100, "MyApp.Widget", size: 100);
        objectLookup.SetObject(0x1200, "MyApp.Widget", size: 100);
        objectLookup.SetObject(0x2000, "MyApp.Registry", size: 50);

        var heapObjects = new FakeHeapObjectStream(
        [
            new HeapObjectRef(0x1000, SdkTypeRefFactory.FromName("MyApp.Widget", null), 100, "MyApp.Widget"),
            new HeapObjectRef(0x1100, SdkTypeRefFactory.FromName("MyApp.Widget", null), 100, "MyApp.Widget"),
            new HeapObjectRef(0x1200, SdkTypeRefFactory.FromName("MyApp.Widget", null), 100, "MyApp.Widget"),
        ]);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar", [0x2000, 0x1000]);
        rootPaths.SetPath(0x1100, "StaticVar", [0x2000, 0x1100]);
        // 0x1200 deliberately has no registered path -- one of the three probed instances doesn't
        // resolve to a root, exercising the "N/M" partial-consistency count.

        var options = new ReferenceChainOptions { MultiSampleCount = 3 };

        ReferenceChainDomainResult result = await Analyze(
            typeStats, objectLookup: objectLookup, heapObjects: heapObjects, rootPaths: rootPaths, options: options);

        ReferenceTypeSampleSnapshot trace = result.TopTypeSampleTraces.Should().ContainSingle().Subject;
        trace.SampleCount.Should().Be(3);
        trace.RetainedSampleCount.Should().Be(2);
        trace.DominantSampleRootKind.Should().Be("StaticVar");
        trace.DominantSampleRootKindCount.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_TopCountLimitsCandidatesByTotalSizeDescending()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Small", instanceCount: 1, totalSize: 10, sampleAddress: 0x1000);
        typeStats.SetType("MyApp.Large", instanceCount: 1, totalSize: 1000, sampleAddress: 0x2000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Small", size: 10);
        objectLookup.SetObject(0x2000, "MyApp.Large", size: 1000);

        var options = new ReferenceChainOptions { TopCount = 1 };

        ReferenceChainDomainResult result = await Analyze(typeStats, objectLookup: objectLookup, options: options);

        result.TopTypeSampleTraces.Should().ContainSingle().Which.TypeName.Should().Be("MyApp.Large");
    }

    private static async Task<ReferenceChainDomainResult> Analyze(
        FakeHeapTypeStatisticsQuery typeStatistics,
        FakeHeapObjectLookup? objectLookup = null,
        FakeHeapObjectStream? heapObjects = null,
        FakeHeapReferenceQuery? references = null,
        FakeHeapRootQuery? heapRoots = null,
        FakeHeapRootPathQuery? rootPaths = null,
        FakeHeapDominatorQuery? dominators = null,
        ReferenceChainOptions? options = null)
    {
        var analyzer = new ReferenceChainAnalyzer();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            HeapTypeStatistics = typeStatistics,
            HeapObjectLookup = objectLookup ?? new FakeHeapObjectLookup(),
            HeapObjects = heapObjects ?? new FakeHeapObjectStream([]),
            HeapReferences = references ?? new FakeHeapReferenceQuery(),
            HeapRoots = heapRoots ?? new FakeHeapRootQuery(),
            HeapRootPaths = rootPaths ?? new FakeHeapRootPathQuery(),
            HeapDominators = dominators,
            AnalyzerOptions = options ?? new ReferenceChainOptions(),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (ReferenceChainDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetType(string typeName, long instanceCount, ulong totalSize, ulong sampleAddress) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null),
                InstanceCount: instanceCount,
                TotalSize: totalSize,
                LohCount: 0,
                LohSize: 0,
                Gen0Count: 0,
                Gen1Count: 0,
                Gen2Count: 0,
                Gen2TotalSize: 0,
                IsFinalizableType: false,
                SampleAddress: sampleAddress);

        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => _stats;
        public bool HasExactGenerationData => true;
        public bool HasTypeShapeData => true;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }

    private sealed class FakeHeapObjectLookup : IHeapObjectLookup
    {
        private readonly Dictionary<ulong, (string TypeName, ulong Size)> _objects = new();

        public void SetObject(ulong address, string typeName, ulong size) => _objects[address] = (typeName, size);

        public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
        {
            if (_objects.TryGetValue(address, out (string TypeName, ulong Size) o))
            {
                heapObject = new HeapObjectRef(address, SdkTypeRefFactory.FromName(o.TypeName, null), o.Size, o.TypeName);
                return true;
            }

            heapObject = default;
            return false;
        }
    }

    private sealed class FakeHeapObjectStream(IReadOnlyList<HeapObjectRef> objects) : IHeapObjectStream
    {
        public IEnumerable<HeapObjectRef> EnumerateObjects() => objects;
    }

    private sealed class FakeHeapReferenceQuery : IHeapReferenceQuery
    {
        private readonly Dictionary<(ulong Parent, ulong Child), string> _fieldNames = new();

        public void SetFieldName(ulong parentAddress, ulong childAddress, string fieldName) =>
            _fieldNames[(parentAddress, childAddress)] = fieldName;

        public IEnumerable<ulong> EnumerateReferences(ulong address) => [];

        public bool TryGetReferenceFieldName(ulong parentAddress, ulong childAddress, out string? fieldName) =>
            _fieldNames.TryGetValue((parentAddress, childAddress), out fieldName);
    }

    private sealed class FakeHeapRootQuery : IHeapRootQuery
    {
        private readonly List<HeapRootRef> _roots = new();

        public void AddStaticRoot(ulong targetAddress, ulong rootAddress, string ownerType, string fieldName, int appDomainId = 1) =>
            _roots.Add(new HeapRootRef
            {
                Kind = HeapRootKind.StaticVar,
                TargetAddress = targetAddress,
                RootAddress = rootAddress,
                OwnerTypeName = ownerType,
                FieldName = fieldName,
                AppDomainId = appDomainId,
            });

        public void AddStackRoot(ulong targetAddress, ulong rootAddress, string ownerType, string methodName)
        {
            _roots.Add(new HeapRootRef { Kind = HeapRootKind.Stack, TargetAddress = targetAddress, RootAddress = rootAddress });
            _stackFrameOwners[rootAddress] = (ownerType, methodName);
        }

        private readonly Dictionary<ulong, (string OwnerType, string MethodName)> _stackFrameOwners = new();

        public IEnumerable<HeapRootRef> EnumerateRoots() => _roots;

        public bool TryResolveStackFrameOwner(ulong rootAddress, out string ownerTypeName, out string methodName)
        {
            if (_stackFrameOwners.TryGetValue(rootAddress, out (string OwnerType, string MethodName) owner))
            {
                ownerTypeName = owner.OwnerType;
                methodName = owner.MethodName;
                return true;
            }

            ownerTypeName = string.Empty;
            methodName = string.Empty;
            return false;
        }
    }

    private sealed class FakeHeapRootPathQuery : IHeapRootPathQuery
    {
        private readonly Dictionary<ulong, (string RootKind, IReadOnlyList<ulong> HopAddresses)> _paths = new();

        public void SetPath(ulong targetAddress, string rootKind, IReadOnlyList<ulong> hopAddresses) =>
            _paths[targetAddress] = (rootKind, hopAddresses);

        public bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated)
        {
            formattedPath = null;
            searchTruncated = false;
            return false;
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
            searchTruncated = false;
            if (_paths.TryGetValue(targetAddress, out (string RootKind, IReadOnlyList<ulong> HopAddresses) entry))
            {
                rootKind = entry.RootKind;
                hopAddresses = entry.HopAddresses;
                return true;
            }

            rootKind = null;
            hopAddresses = null;
            return false;
        }
    }

    private sealed class FakeHeapDominatorQuery : IHeapDominatorQuery
    {
        private readonly Dictionary<ulong, ulong> _retained = new();
        public void SetRetainedSize(ulong address, ulong bytes) => _retained[address] = bytes;

        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) => _retained.TryGetValue(address, out retainedBytes);
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress) => [];
        public bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes) { retainedBytes = 0; return false; }
    }
}
