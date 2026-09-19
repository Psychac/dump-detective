using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Types;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="CollectionAnalyzer"/>'s pre-existing coverage
/// (<c>CollectionKindClassificationTests</c>/<c>CollectionResizeRecommendationTests</c>/
/// <c>CollectionElementTypeAggregationTests</c>/<c>CollectionOwnerTypeHintTests</c>) only ever
/// exercised its pure, ClrMD-independent static helpers, unaffected by this retyping and left as-is.
/// This file covers what those didn't: the capability-driven per-instance waste probes (via
/// <see cref="IDumpObjectFieldQuery.EnumerateInstanceFields"/>'s new field-name/kind discovery),
/// generation counting, dominator-backed retained bytes, and the new
/// <see cref="IHeapReverseReferenceQuery.TryGetReverseReferences"/>/
/// <see cref="IHeapRootPathQuery.TryFindRootPath(ulong, RootPathSearchOptions, Func{string, bool}?, out string?, out bool)"/>
/// overloads this analyzer is the first real consumer of — all against fake, dictionary-backed
/// capability implementations, the shape <c>WeakReferenceAnalyzerRetypingCharacterizationTests</c>/
/// <c>TimerLeakAnalyzerRetypingCharacterizationTests</c> already established.
/// </summary>
public sealed class CollectionAnalyzerRetypingCharacterizationTests
{
    private const string ListTypeName = "System.Collections.Generic.List<System.String>";
    private const string DictionaryTypeName = "System.Collections.Generic.Dictionary<System.String,System.Int32>";
    private const string QueueTypeName = "System.Collections.Generic.Queue<System.Int32>";
    private const string SortedSetTypeName = "System.Collections.Generic.SortedSet<System.String>";

    [Fact]
    public async Task AnalyzeAsync_ListWithOverAllocatedBackingArray_ReportsWaste()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 10);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.String[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 100, rank: 1);

        var typeStatistics = new FakeHeapTypeStatisticsQuery();
        typeStatistics.SetArrayShape("System.String[]", elementTypeName: "System.String", elementSizeBytes: 8);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, typeStatistics,
            candidates: [Candidate(0x1000, ListTypeName)]);

        result.TotalCollections.Should().Be(1);
        result.Lists.Should().Be(1);
        result.WastefulCollectionCount.Should().Be(1);
        WastefulCollectionSnapshot waste = result.TopWastefulCollections.Should().ContainSingle().Subject;
        waste.Count.Should().Be(10);
        waste.Capacity.Should().Be(100);
        waste.ElementType.Should().Be("System.String");
        waste.WastedMemory.Should().Be(90 * 8UL);
    }

    [Fact]
    public async Task AnalyzeAsync_DictionaryWithFreeSlots_SubtractsFreeCountFromLiveCount()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_entries", 0x2000);
        fields.SetInt32(0x1000, "_count", 20);
        fields.SetInt32(0x1000, "_freeCount", 5);
        fields.DeclareFields(DictionaryTypeName,
            ("_entries", FieldValueKind.ObjectReference, IsArrayTyped: true),
            ("_count", FieldValueKind.Int32, IsArrayTyped: false),
            ("_freeCount", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "Entry[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 64, rank: 1);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            candidates: [Candidate(0x1000, DictionaryTypeName)]);

        result.Dictionaries.Should().Be(1);
        WastefulCollectionSnapshot waste = result.TopWastefulCollections.Should().ContainSingle().Subject;
        waste.Count.Should().Be(15); // 20 - 5 free slots
    }

    [Fact]
    public async Task AnalyzeAsync_QueueWithHeadOffset_ComputesContiguousFreeSegments()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_array", 0x2000);
        fields.SetInt32(0x1000, "_size", 4);
        fields.SetInt32(0x1000, "_head", 2);
        fields.DeclareFields(QueueTypeName,
            ("_array", FieldValueKind.ObjectReference, IsArrayTyped: true),
            ("_size", FieldValueKind.Int32, IsArrayTyped: false),
            ("_head", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Int32[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 10, rank: 1);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            candidates: [Candidate(0x1000, QueueTypeName)]);

        result.Queues.Should().Be(1);
        WastefulCollectionSnapshot waste = result.TopWastefulCollections.Should().ContainSingle().Subject;
        waste.Head.Should().Be(2);
        waste.FreeSegmentCount.Should().NotBeNull();
        waste.FreeSegmentCount!.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnalyzeAsync_SortedSet_CountedButExcludedFromWasteAnalysis()
    {
        var fields = new FakeDumpObjectFieldQuery();
        CollectionDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeDumpArrayShapeQuery(), new FakeHeapTypeStatisticsQuery(),
            candidates: [Candidate(0x1000, SortedSetTypeName)]);

        result.TotalCollections.Should().Be(1);
        result.SortedSets.Should().Be(1);
        result.WastefulCollectionCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_NoFieldLayoutResolvable_SkipsWasteButStillCountsCollection()
    {
        // No well-known field names declared, and no int32/array-typed field the fallback
        // classification pass could find either -- matches the pre-retyping analyzer's own
        // "probe returns null" behavior when a type's internal shape can't be resolved.
        var fields = new FakeDumpObjectFieldQuery();
        fields.DeclareFields(ListTypeName); // zero fields

        CollectionDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeDumpArrayShapeQuery(), new FakeHeapTypeStatisticsQuery(),
            candidates: [Candidate(0x1000, ListTypeName)]);

        result.TotalCollections.Should().Be(1);
        result.WastefulCollectionCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_FieldLayoutFallback_ClassifiesByDeclaredKindWhenWellKnownNamesMissing()
    {
        // Non-standard field names -- forces the fallback pass to classify "count" via a substring
        // name match and "backing" via its array-typed kind, exactly like GetOrBuildFieldLayout's
        // pre-retyping fallback loop.
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "backing", 0x2000);
        fields.SetInt32(0x1000, "liveCount", 7);
        fields.DeclareFields(ListTypeName,
            ("backing", FieldValueKind.ObjectReference, IsArrayTyped: true),
            ("liveCount", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 50, rank: 1);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            candidates: [Candidate(0x1000, ListTypeName)]);

        WastefulCollectionSnapshot waste = result.TopWastefulCollections.Should().ContainSingle().Subject;
        waste.Count.Should().Be(7);
        waste.Capacity.Should().Be(50);
    }

    [Fact]
    public async Task AnalyzeAsync_UnresolvedArrayElementType_FallsBackToLowConfidenceSizeEstimate()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 2);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "Unknown[]", size: 80);

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 10, rank: 1);

        // No FakeHeapTypeStatisticsQuery entry for "Unknown[]" -- unresolved array shape.
        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            candidates: [Candidate(0x1000, ListTypeName)]);

        WastefulCollectionSnapshot waste = result.TopWastefulCollections.Should().ContainSingle().Subject;
        waste.SizeEstimateConfidence.Should().Be("Low");
        waste.ElementSize.Should().Be(80UL / 10); // itemsObj.Size / capacity fallback
    }

    [Fact]
    public async Task AnalyzeAsync_GenerationBreakdown_BucketsByHeapGenerationTag()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 1);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 5, rank: 1);

        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen2);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(), segments,
            candidates: [Candidate(0x1000, ListTypeName)]);

        result.GenerationBreakdown.Should().ContainSingle(g => g.Kind == CollectionKind.List && g.Gen2Count == 1 && g.Gen0Count == 0);
    }

    [Fact]
    public async Task AnalyzeAsync_TopWasteful_GetsDominatorBackedRetainedBytes()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 1);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 100, rank: 1);

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 4096);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(), dominators: dominators,
            candidates: [Candidate(0x1000, ListTypeName)]);

        result.TopWastefulCollections.Should().ContainSingle().Which.RetainedBytes.Should().Be(4096UL);
    }

    [Fact]
    public async Task AnalyzeAsync_OwnerTypeHint_SingleParent_ReportsDirectly()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 1);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");
        objectLookup.SetObject(0x3000, "MyApp.Cache");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 100, rank: 1);

        var reverseReferences = new FakeHeapReverseReferenceQuery();
        reverseReferences.SetParents(0x1000, truncated: false, 0x3000);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            reverseReferences: reverseReferences, candidates: [Candidate(0x1000, ListTypeName)]);

        result.TopWastefulCollections.Should().ContainSingle().Which.OwnerTypeHint.Should().Be("MyApp.Cache");
    }

    [Fact]
    public async Task AnalyzeAsync_OwnerTypeHint_TruncatedMultipleParents_ReportsAmbiguity()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 1);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");
        objectLookup.SetObject(0x3000, "MyApp.Cache");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 100, rank: 1);

        var reverseReferences = new FakeHeapReverseReferenceQuery();
        reverseReferences.SetParents(0x1000, truncated: true, 0x3000, 0x3001);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            reverseReferences: reverseReferences, candidates: [Candidate(0x1000, ListTypeName)]);

        result.TopWastefulCollections.Should().ContainSingle().Which.OwnerTypeHint.Should().Be("2+ referrers, e.g. MyApp.Cache");
    }

    [Fact]
    public async Task AnalyzeAsync_RootPathFound_ReportsFormattedPath()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 1);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 100, rank: 1);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar: MyApp.Registry@0x4000 -> List@0x1000");

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            rootPaths: rootPaths, candidates: [Candidate(0x1000, ListTypeName)]);

        result.TopWastefulCollections.Should().ContainSingle().Which.RootDescription
            .Should().Be("StaticVar: MyApp.Registry@0x4000 -> List@0x1000");
    }

    [Fact]
    public async Task AnalyzeAsync_RootPathNotFound_ReportsBudgetMessage()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 1);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 100, rank: 1);

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            rootPaths: new FakeHeapRootPathQuery(), candidates: [Candidate(0x1000, ListTypeName)]);

        result.TopWastefulCollections.Should().ContainSingle().Which.RootDescription
            .Should().Be("No root path found (within budget)");
    }

    [Fact]
    public async Task AnalyzeAsync_NoReferenceChainOptions_SkipsRootPathEnrichmentEntirely()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_items", 0x2000);
        fields.SetInt32(0x1000, "_size", 1);
        fields.DeclareFields(ListTypeName, ("_items", FieldValueKind.ObjectReference, IsArrayTyped: true), ("_size", FieldValueKind.Int32, IsArrayTyped: false));

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Object[]");

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetShape(0x2000, length: 100, rank: 1);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "should never be reached");

        CollectionDomainResult result = await Analyze(fields, objectLookup, arrayShapes, new FakeHeapTypeStatisticsQuery(),
            rootPaths: rootPaths, disableReferenceChainOptions: true, candidates: [Candidate(0x1000, ListTypeName)]);

        result.TopWastefulCollections.Should().ContainSingle().Which.RootDescription.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        CollectionDomainResult result = await Analyze(
            new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(), new FakeDumpArrayShapeQuery(), new FakeHeapTypeStatisticsQuery(),
            candidates: []);

        result.TotalCollections.Should().Be(0);
        result.WastefulCollectionCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_NoHeapObjectsStream_ReturnsEmptyResult()
    {
        var analyzer = new CollectionAnalyzer();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new ArtifactId("test"),
            DumpObjectFields = new FakeDumpObjectFieldQuery(),
            HeapObjectLookup = new FakeHeapObjectLookup(),
            DumpArrayShapes = new FakeDumpArrayShapeQuery(),
            HeapTypeStatistics = new FakeHeapTypeStatisticsQuery(),
            HeapObjects = null,
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        var result = (CollectionDomainResult)analyzer.LastResult!;

        result.TotalCollections.Should().Be(0);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 32, TypeDisplayName: typeName);

    private static async Task<CollectionDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields, FakeHeapObjectLookup objectLookup, FakeDumpArrayShapeQuery arrayShapes, FakeHeapTypeStatisticsQuery typeStatistics,
        FakeHeapSegmentQuery? segments = null, FakeHeapDominatorQuery? dominators = null, FakeHeapRootPathQuery? rootPaths = null,
        FakeHeapReverseReferenceQuery? reverseReferences = null, bool disableReferenceChainOptions = false,
        params HeapObjectRef[] candidates)
    {
        CollectionAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new ArtifactId("test"),
            AnalyzerOptions = new CollectionAnalyzerOptions(
                new CollectionAnalysisOptions { WasteThresholdBytes = 0 },
                disableReferenceChainOptions ? null : new ReferenceChainOptions()),
            DumpObjectFields = fields,
            HeapObjectLookup = objectLookup,
            DumpArrayShapes = arrayShapes,
            HeapTypeStatistics = typeStatistics,
            HeapSegments = segments,
            HeapDominators = dominators,
            HeapRootPaths = rootPaths,
            HeapReverseReferences = reverseReferences,
            HeapObjects = candidates.Length == 0 ? null : new PrecomputedHeapObjectStream(candidates),
        };

        // GetOrBuildFieldLayout resolves EnumerateInstanceFields(candidate.Address) -- the fake
        // needs the address-to-declared-type mapping the real dump-side implementation gets for
        // free from the object's own ClrType.
        foreach (HeapObjectRef candidate in candidates)
            fields.AssociateAddress(candidate.Address, candidate.TypeDisplayName);

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (CollectionDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();
        private readonly Dictionary<string, List<InstanceFieldDescriptor>> _fieldsByType = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, string> _typeNameByAddress = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);
        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);

        /// <summary>Associates a candidate's address with the field descriptors its type declares --
        /// <see cref="EnumerateInstanceFields"/> is address-scoped (mirrors the real
        /// implementation, which resolves fields off the object at that address), so this fake needs
        /// an address-to-type mapping the way the real dump-side implementation gets one for free
        /// from the object's own <c>ClrType</c>.</summary>
        public void DeclareFields(string typeName, params (string Name, FieldValueKind Kind, bool IsArrayTyped)[] fields) =>
            _fieldsByType[typeName] = fields.Select(f => new InstanceFieldDescriptor(f.Name, f.Kind, f.IsArrayTyped)).ToList();

        public void AssociateAddress(ulong address, string typeName) => _typeNameByAddress[address] = typeName;

        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) =>
            _typeNameByAddress.TryGetValue(address, out string? typeName) && _fieldsByType.TryGetValue(typeName, out List<InstanceFieldDescriptor>? fields)
                ? fields
                : [];

        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool HasDelegateField(ulong address) => false;

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this analyzer
        }

        public bool TryGetStringValue(ulong address, int maxLength, out string? value) => TryGetStringValue(address, out value);

        public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName)
        {
            typeName = null;
            return false; // not exercised by this analyzer
        }

        public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address) => [];

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false; // not exercised by this analyzer
        }
    }

    private sealed class FakeHeapObjectLookup : IHeapObjectLookup
    {
        private readonly Dictionary<ulong, (string TypeName, ulong Size)> _objects = new();

        // Default well above any capacity used in this file's tests -- the "no HeapTypeStatistics
        // entry" fallback (ArrayObj.Size / capacity) must stay non-zero unless a test deliberately
        // exercises that division (see AnalyzeAsync_UnresolvedArrayElementType_..., which passes an
        // explicit small size).
        public void SetObject(ulong address, string typeName, ulong size = 4096) => _objects[address] = (typeName, size);

        public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
        {
            if (_objects.TryGetValue(address, out (string TypeName, ulong Size) entry))
            {
                heapObject = new HeapObjectRef(address, SdkTypeRefFactory.FromName(entry.TypeName, null), entry.Size, entry.TypeName);
                return true;
            }
            heapObject = default;
            return false;
        }
    }

    private sealed class FakeDumpArrayShapeQuery : IDumpArrayShapeQuery
    {
        private readonly Dictionary<ulong, (int Length, int Rank)> _shapes = new();

        public void SetShape(ulong address, int length, int rank) => _shapes[address] = (length, rank);

        public bool TryGetArrayShape(ulong address, out int length, out int rank)
        {
            if (_shapes.TryGetValue(address, out (int Length, int Rank) shape))
            {
                length = shape.Length;
                rank = shape.Rank;
                return true;
            }
            length = 0;
            rank = 0;
            return false;
        }

        public int? CountNullOrZeroElements(ulong address) => null; // not exercised by this analyzer
        public IReadOnlyList<ulong> EnumerateObjectReferenceElements(ulong address, int maxElements) => []; // not exercised by this analyzer
    }

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetArrayShape(string arrayTypeName, string elementTypeName, int elementSizeBytes) =>
            _stats[arrayTypeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(arrayTypeName, null),
                InstanceCount: 1, TotalSize: 0, LohCount: 0, LohSize: 0,
                Gen0Count: 0, Gen1Count: 0, Gen2Count: 0, Gen2TotalSize: 0, IsFinalizableType: false,
                IsArray: true, ArrayElementTypeName: elementTypeName, ArrayRank: 1,
                ArrayElementIsObjectReference: true, ArrayElementSizeBytes: elementSizeBytes);

        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => _stats;
        public bool HasExactGenerationData => false;
        public bool HasTypeShapeData => false;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }

    private sealed class FakeHeapSegmentQuery : IHeapSegmentQuery
    {
        private readonly Dictionary<ulong, HeapGenerationTag> _generations = new();

        public void SetGeneration(ulong address, HeapGenerationTag tag) => _generations[address] = tag;

        public HeapGenerationTag GetGeneration(ulong address) => _generations.GetValueOrDefault(address, HeapGenerationTag.Unknown);
        public IEnumerable<HeapSegmentRef> EnumerateSegments() => [];
        public bool TryGetSegment(ulong address, out HeapSegmentRef segment) { segment = default; return false; }
        public int DumpPointerSize => 8;
        public bool IsServerGc => false;
        public int LogicalHeapCount => 1;
        public bool CanWalkHeap => true;
        public IEnumerable<HeapObjectRef> EnumerateObjects(HeapSegmentRef segment, bool includeFree = false) => [];
        public IEnumerable<HeapFreeBlockRef> EnumerateLohFreeBlocks() => [];
        public IEnumerable<HeapObjectRef> EnumerateCapturedLargeObjects() => [];
        public bool HasLohSatelliteIndex => false;
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

    private sealed class FakeHeapReverseReferenceQuery : IHeapReverseReferenceQuery
    {
        private readonly Dictionary<ulong, (IReadOnlyList<ulong> Parents, bool Truncated)> _parents = new();

        public void SetParents(ulong address, bool truncated, params ulong[] parents) => _parents[address] = (parents, truncated);

        public IEnumerable<ulong> EnumerateReverseReferences(ulong address) =>
            _parents.TryGetValue(address, out (IReadOnlyList<ulong> Parents, bool Truncated) entry) ? entry.Parents : [];

        public bool TryGetReverseReferences(ulong address, out IReadOnlyList<ulong> parents, out bool truncated)
        {
            if (_parents.TryGetValue(address, out (IReadOnlyList<ulong> Parents, bool Truncated) entry))
            {
                parents = entry.Parents;
                truncated = entry.Truncated;
                return true;
            }
            parents = [];
            truncated = false;
            return false;
        }

        public IEnumerable<(ulong Address, int IncomingCount)> EnumerateIncomingReferenceCounts() => [];
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
