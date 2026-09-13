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
/// Characterization tests for the Tier-1+Tier-2 retyping of <see cref="StaticRootLeakDetector"/>
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). No pre-existing per-analyzer
/// unit test coverage existed before this, and the real-dump gate can't exercise real positive data
/// either — this analyzer's static-root detection is broken upstream (see the analyzer's own
/// remarks) and always returns zero roots on a real dump. This fake-backed suite therefore carries
/// essentially all the real behavioral coverage for this batch: significant-root filtering, the
/// per-type/namespace retained-set breakdown, collection/event-handler (instance and static field)
/// detection, Gen2-or-larger fraction, root-path evidence, and the no-dominator-tree fallback.
/// </summary>
public sealed class StaticRootLeakDetectorRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_NoStaticRoots_ReturnsEmptyResult()
    {
        StaticRootDomainResult result = await Analyze(new FakeHeapRootQuery(), new FakeHeapDominatorQuery(),
            new FakeHeapObjectLookup(), new FakeHeapSegmentQuery(), new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(),
            new FakeHeapTypeStatisticsQuery());

        result.RootCount.Should().Be(0);
        result.TopRootsByRetainedBytes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_NonStaticRoot_Ignored()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddRoot(HeapRootKind.Stack, targetAddress: 0x1000, rootAddress: 0x2000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Widget", size: 100);

        StaticRootDomainResult result = await Analyze(roots, new FakeHeapDominatorQuery(), objectLookup,
            new FakeHeapSegmentQuery(), new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.TopRootsByRetainedBytes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_SignificantStaticRoot_ExceedsByteThreshold_CountsAsSignificant()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 5_000_000);

        var options = new StaticRootLeakAnalysisOptions { SignificantMemoryThresholdBytes = 1_000_000, SignificantObjectCountThreshold = int.MaxValue };

        StaticRootDomainResult result = await Analyze(roots, dominators, objectLookup, new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery(), options);

        result.RootCount.Should().Be(1);
        result.TotalRetainedBytes.Should().Be(5_000_000UL);
    }

    [Fact]
    public async Task AnalyzeAsync_RootBelowBothThresholds_NotCountedAsSignificant()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 100);

        var options = new StaticRootLeakAnalysisOptions { SignificantMemoryThresholdBytes = 1_000_000, SignificantObjectCountThreshold = 100 };

        StaticRootDomainResult result = await Analyze(roots, dominators, objectLookup, new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery(), options);

        result.RootCount.Should().Be(0);
        // Still reported in the full top-roots list -- significance only gates RootCount/TotalRetainedBytes.
        result.TopRootsByRetainedBytes.Should().ContainSingle();
    }

    [Fact]
    public async Task AnalyzeAsync_RootDescription_UsesOwnerTypeAndFieldName()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);

        StaticRootDomainResult result = await Analyze(roots, new FakeHeapDominatorQuery(), objectLookup,
            new FakeHeapSegmentQuery(), new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.TopRootsByRetainedBytes.Should().ContainSingle().Which.RootDescription.Should().Be("MyApp.Cache._instance");
    }

    [Fact]
    public async Task AnalyzeAsync_NonDefaultAppDomain_SuffixesRootDescriptionAndSetsAssemblyLoadContextInfo()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance", appDomainId: 2);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);

        StaticRootDomainResult result = await Analyze(roots, new FakeHeapDominatorQuery(), objectLookup,
            new FakeHeapSegmentQuery(), new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        StaticRootSnapshot snapshot = result.TopRootsByRetainedBytes.Should().ContainSingle().Subject;
        snapshot.RootDescription.Should().Contain("[AppDomain#2]");
        snapshot.AssemblyLoadContextInfo.Should().Be("AppDomain#2");
    }

    [Fact]
    public async Task AnalyzeAsync_RetainedSet_BuildsPerTypeAndNamespaceBreakdown()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);
        objectLookup.SetObject(0x1001, "MyApp.Widgets.Widget", size: 200);
        objectLookup.SetObject(0x1002, "MyApp.Widgets.Widget", size: 200);
        objectLookup.SetObject(0x1003, "MyApp.Gadgets.Gadget", size: 50);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 550);
        dominators.SetRetainedObjects(0x1000, [0x1000, 0x1001, 0x1002, 0x1003], objectLookup);

        StaticRootDomainResult result = await Analyze(roots, dominators, objectLookup, new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        StaticRootSnapshot snapshot = result.TopRootsByRetainedBytes.Should().ContainSingle().Subject;
        snapshot.ObjectsKeptAlive.Should().Be(4);
        snapshot.TopRetainedTypes.Should().Contain(t => t.TypeName == "MyApp.Widgets.Widget" && t.Count == 2 && t.TotalSize == 400);
        snapshot.TopRetainedNamespaces.Should().Contain(n => n.Namespace == "MyApp.Widgets" && n.TotalSize == 400);
    }

    [Fact]
    public async Task AnalyzeAsync_RetainedSetContainsCollectionType_SetsContainsCollections()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_map");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);
        objectLookup.SetObject(0x1001, "System.Collections.Generic.Dictionary`2", size: 200);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 300);
        dominators.SetRetainedObjects(0x1000, [0x1000, 0x1001], objectLookup);

        StaticRootDomainResult result = await Analyze(roots, dominators, objectLookup, new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.TopRootsByRetainedBytes.Should().ContainSingle().Which.ContainsCollections.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_RetainedSetContainsDelegateField_SetsContainsEventHandlers()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Publisher", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Publisher", size: 100);
        objectLookup.SetObject(0x1001, "MyApp.Subscriber", size: 50);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 150);
        dominators.SetRetainedObjects(0x1000, [0x1000, 0x1001], objectLookup);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetHasDelegateField(0x1000); // e.g. a static-event-backing delegate field

        StaticRootDomainResult result = await Analyze(roots, dominators, objectLookup, new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), fields, new FakeHeapTypeStatisticsQuery());

        result.TopRootsByRetainedBytes.Should().ContainSingle().Which.ContainsEventHandlers.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_AllRetainedObjectsGen2OrLarger_FractionIsOne()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 100);
        dominators.SetRetainedObjects(0x1000, [0x1000], objectLookup);

        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen2);

        StaticRootDomainResult result = await Analyze(roots, dominators, objectLookup, segments,
            new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.TopRootsByRetainedBytes.Should().ContainSingle().Which.Gen2OrLohRetainedFraction.Should().Be(1.0);
    }

    [Fact]
    public async Task AnalyzeAsync_NoDominatorTree_FallsBackToShallowSizeOnlyResult()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);

        StaticRootDomainResult result = await Analyze(roots, dominators: null, objectLookup, new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        StaticRootSnapshot snapshot = result.TopRootsByRetainedBytes.Should().ContainSingle().Subject;
        snapshot.ObjectsKeptAlive.Should().Be(1);
        snapshot.TotalMemoryImpact.Should().Be(100UL);
    }

    [Fact]
    public async Task AnalyzeAsync_TopRoots_GetRootPathEvidence()
    {
        var roots = new FakeHeapRootQuery();
        roots.AddStaticRoot(targetAddress: 0x1000, rootAddress: 0x2000, ownerType: "MyApp.Cache", fieldName: "_instance");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x1000, "MyApp.Cache", size: 100);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "Static: MyApp.Cache._instance -> MyApp.Cache@0x1000");

        StaticRootDomainResult result = await Analyze(roots, new FakeHeapDominatorQuery(), objectLookup,
            new FakeHeapSegmentQuery(), rootPaths, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        StaticRootSnapshot snapshot = result.TopRootsByRetainedBytes.Should().ContainSingle().Subject;
        snapshot.Evidence.Should().NotBeNull();
        snapshot.Evidence!.SampleRootPath.Should().Contain("Static:");
    }

    private static async Task<StaticRootDomainResult> Analyze(
        FakeHeapRootQuery roots, FakeHeapDominatorQuery? dominators, FakeHeapObjectLookup objectLookup, FakeHeapSegmentQuery segments,
        FakeHeapRootPathQuery rootPaths, FakeDumpObjectFieldQuery fields, FakeHeapTypeStatisticsQuery typeStatistics,
        StaticRootLeakAnalysisOptions? options = null)
    {
        StaticRootLeakDetector analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            HeapRoots = roots,
            HeapDominators = dominators,
            HeapObjectLookup = objectLookup,
            HeapSegments = segments,
            HeapRootPaths = rootPaths,
            DumpObjectFields = fields,
            HeapTypeStatistics = typeStatistics,
            AnalyzerOptions = options ?? new StaticRootLeakAnalysisOptions(),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (StaticRootDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeHeapRootQuery : IHeapRootQuery
    {
        private readonly List<HeapRootRef> _roots = new();

        public void AddRoot(HeapRootKind kind, ulong targetAddress, ulong rootAddress) =>
            _roots.Add(new HeapRootRef { Kind = kind, TargetAddress = targetAddress, RootAddress = rootAddress });

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

        public IEnumerable<HeapRootRef> EnumerateRoots() => _roots;

        public bool TryResolveStackFrameOwner(ulong rootAddress, out string ownerTypeName, out string methodName)
        {
            ownerTypeName = string.Empty;
            methodName = string.Empty;
            return false;
        }
    }

    private sealed class FakeHeapDominatorQuery : IHeapDominatorQuery
    {
        private readonly Dictionary<ulong, ulong> _retained = new();
        private readonly Dictionary<ulong, List<HeapObjectRef>> _retainedSets = new();

        public void SetRetainedSize(ulong address, ulong bytes) => _retained[address] = bytes;

        /// <summary>Resolves each address via <paramref name="objectLookup"/> at set-up time,
        /// mirroring the real <c>HeapDominatorQuery.EnumerateRetainedObjects</c>'s own use of
        /// <see cref="IHeapObjectLookup"/> to turn raw addresses into <see cref="HeapObjectRef"/>s.</summary>
        public void SetRetainedObjects(ulong rootAddress, IReadOnlyList<ulong> addresses, FakeHeapObjectLookup objectLookup)
        {
            var resolved = new List<HeapObjectRef>(addresses.Count);
            foreach (ulong address in addresses)
                if (objectLookup.TryGetObject(address, out HeapObjectRef obj))
                    resolved.Add(obj);
            _retainedSets[rootAddress] = resolved;
        }

        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) => _retained.TryGetValue(address, out retainedBytes);
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) { retainedBytes = 0; return false; }

        public IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress) =>
            _retainedSets.TryGetValue(rootAddress, out List<HeapObjectRef>? objects) ? objects : [];

        public bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes) { retainedBytes = 0; return false; }
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

    private sealed class FakeHeapSegmentQuery : IHeapSegmentQuery
    {
        private readonly Dictionary<ulong, HeapGenerationTag> _generations = new();

        public void SetGeneration(ulong address, HeapGenerationTag tag) => _generations[address] = tag;

        public IEnumerable<HeapSegmentRef> EnumerateSegments() => [];
        public bool TryGetSegment(ulong address, out HeapSegmentRef segment) { segment = default; return false; }
        public int DumpPointerSize => 8;
        public bool IsServerGc => false;
        public int LogicalHeapCount => 1;
        public bool CanWalkHeap => true;
        public IEnumerable<HeapObjectRef> EnumerateObjects(HeapSegmentRef segment, bool includeFree = false) => [];
        public HeapGenerationTag GetGeneration(ulong address) => _generations.TryGetValue(address, out HeapGenerationTag tag) ? tag : HeapGenerationTag.Gen0;
        public IEnumerable<HeapFreeBlockRef> EnumerateLohFreeBlocks() => [];
        public IEnumerable<HeapObjectRef> EnumerateCapturedLargeObjects() => [];
        public bool HasLohSatelliteIndex => false;
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

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        private readonly HashSet<ulong> _delegateFieldOwners = new();

        public void SetHasDelegateField(ulong address) => _delegateFieldOwners.Add(address);

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) { value = FieldValue.Null; return false; }
        public bool TryGetStringValue(ulong address, out string? value) { value = null; return false; }
        public bool TryGetStringValue(ulong address, int maxLength, out string? value) { value = null; return false; }
        public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName) { typeName = null; return false; }
        public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address) => [];
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false;
        }

        public bool HasDelegateField(ulong address) => _delegateFieldOwners.Contains(address);
    }

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => new Dictionary<string, HeapTypeStatistics>();
        public bool HasExactGenerationData => true;
        public bool HasTypeShapeData => true;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }
}
