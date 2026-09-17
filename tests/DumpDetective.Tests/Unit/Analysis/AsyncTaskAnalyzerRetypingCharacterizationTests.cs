using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Async;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization tests for the Tier-2 wave retyping of <see cref="AsyncTaskAnalyzer"/> —
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. Covers Task/TaskCompletionSource/
/// IValueTaskSource classification, orphan detection, continuation-chain BFS (including
/// List&lt;object&gt; multi-continuation fan-out), and faulted-task exception extraction, all against
/// fake, dictionary-backed capability implementations.
/// </summary>
public sealed class AsyncTaskAnalyzerRetypingCharacterizationTests
{
    private const ulong MaskCompleted = 0x1000000;
    private const ulong MaskFaulted = 0x200000;
    private const ulong MaskCanceled = 0x400000;

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        AsyncTaskDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(),
            new FakeHeapReferenceQuery(), new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(),
            new FakeHeapTypeStatisticsQuery());

        result.TotalTasks.Should().Be(0);
        result.TotalTaskCompletionSources.Should().Be(0);
        result.TotalValueTaskSources.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_PendingTaskWithNoContinuationField_ClassifiedAsPending_NotOrphaned()
    {
        var fields = new FakeDumpObjectFieldQuery();
        // No m_stateFlags/m_continuationObject fields set at all -- matches a type on which
        // neither field could be resolved (defensive edge case, not the common path).
        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskType("MyTask", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats, Candidate(0x1000, "MyTask"));

        result.TotalTasks.Should().Be(1);
        result.PendingTasks.Should().Be(1);
        result.OrphanedTasks.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_OrphanedTask_NullContinuationField_CountsAsOrphan()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetNull(0x1000, "m_continuationObject"); // field exists, value is null
        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskType("MyTask", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats, Candidate(0x1000, "MyTask"));

        result.OrphanedTasks.Should().Be(1);
        result.TopOrphanedTasks.Should().ContainSingle().Which.Address.Should().Be(0x1000);
    }

    [Fact]
    public async Task AnalyzeAsync_CompletedTask_NeverCountedAsOrphan_EvenWithoutContinuation()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "m_stateFlags", (int)MaskCompleted);
        fields.SetNull(0x1000, "m_continuationObject");
        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskType("MyTask", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats, Candidate(0x1000, "MyTask"));

        result.CompletedTasks.Should().Be(1);
        result.OrphanedTasks.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_FaultedTask_ExtractsExceptionFromContingentProperties()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "m_stateFlags", (int)MaskFaulted);
        fields.SetObjectReference(0x1000, "m_contingentProperties", 0x2000);
        fields.SetObjectReference(0x2000, "m_exceptionsHolder", 0x3000); // prioritized field on ExceptionRelatedFields
        fields.SetString(0x3000, "_message", "boom");

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Threading.Tasks.Task+ContingentProperties", size: 40);
        objectLookup.SetObject(0x3000, "System.InvalidOperationException", size: 80);

        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskType("MyFaultedTask", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, objectLookup, new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats, Candidate(0x1000, "MyFaultedTask"));

        result.FaultedTasks.Should().Be(1);
        result.FaultedTaskExceptionHistograms.Should().ContainSingle()
            .Which.ExceptionTypes.Should().ContainSingle(e => e.Name == "System.InvalidOperationException");
    }

    [Fact]
    public async Task AnalyzeAsync_SingleContinuation_ComputesDepthOne()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "m_continuationObject", 0x2000);
        // Continuation node has no further continuation -- depth stops at 1.
        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Runtime.CompilerServices.AsyncMethodBuilderCore+MoveNextRunner", size: 32);

        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskType("MyTask", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, objectLookup, new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats, Candidate(0x1000, "MyTask"));

        result.MaxContinuationDepth.Should().Be(1);
        result.TotalTaskContinuations.Should().Be(1);
        result.TopDeepestChains.Should().ContainSingle()
            .Which.ChainTypes.Should().Equal("MyTask", "System.Runtime.CompilerServices.AsyncMethodBuilderCore+MoveNextRunner");
    }

    [Fact]
    public async Task AnalyzeAsync_MultiContinuationList_ExploresAllBranches_TracksFanOut()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "m_continuationObject", 0x2000);
        fields.SetObjectReference(0x2000, "_items", 0x2100); // List<object>'s backing array
        fields.SetInt32(0x2000, "_size", 2);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.Collections.Generic.List<System.Object>", size: 24);
        objectLookup.SetObject(0x3000, "ContinuationA", size: 16);
        objectLookup.SetObject(0x4000, "ContinuationB", size: 16);

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetObjectReferenceElements(0x2100, [0x3000, 0x4000]);

        // One branch has a further continuation, making it the "winning" (deepest) branch.
        fields.SetObjectReference(0x4000, "m_continuationObject", 0x5000);
        objectLookup.SetObject(0x5000, "ContinuationC", size: 16);

        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskType("MyTask", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, objectLookup, new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), arrayShapes, stats, Candidate(0x1000, "MyTask"));

        result.MultiContinuationNodeCount.Should().Be(1);
        result.MaxContinuationFanOut.Should().Be(2);
        result.MaxContinuationDepth.Should().Be(2); // ContinuationB -> ContinuationC
        result.TopContinuationFanoutTypes.Should().Contain(e => e.Name == "ContinuationA" && e.Count == 1);
        result.TopContinuationFanoutTypes.Should().Contain(e => e.Name == "ContinuationB" && e.Count == 1);
        result.TopContinuationTypes.Should().Contain(e => e.Name == "ContinuationB" && e.Count == 1);
        result.TopContinuationTypes.Should().Contain(e => e.Name == "ContinuationC" && e.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_UnresolvedTaskCompletionSource_NonTerminalInnerTask_CountsAsUnresolved()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "m_task", 0x2000);
        fields.SetInt32(0x2000, "m_stateFlags", 0); // non-terminal

        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskCompletionSourceType("System.Threading.Tasks.TaskCompletionSource`1", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats,
            Candidate(0x1000, "System.Threading.Tasks.TaskCompletionSource`1"));

        result.TotalTaskCompletionSources.Should().Be(1);
        result.UnresolvedTaskCompletionSources.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_TerminalTaskCompletionSource_NotCountedAsUnresolved()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "m_task", 0x2000);
        fields.SetInt32(0x2000, "m_stateFlags", (int)MaskCompleted);

        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetTaskCompletionSourceType("System.Threading.Tasks.TaskCompletionSource`1", instanceCount: 1);

        AsyncTaskDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats,
            Candidate(0x1000, "System.Threading.Tasks.TaskCompletionSource`1"));

        result.TotalTaskCompletionSources.Should().Be(1);
        result.UnresolvedTaskCompletionSources.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_PendingValueTaskSource_IncompleteCoreField_CountsAsPending()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetNestedBool(0x1000, "_core", "_completed", false);

        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetValueTaskSourceType("MyVts", instanceCount: 1, coreFieldName: "_core");

        AsyncTaskDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats, Candidate(0x1000, "MyVts"));

        result.TotalValueTaskSources.Should().Be(1);
        result.PendingValueTaskSources.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_CompletedValueTaskSource_NotCountedAsPending()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetNestedBool(0x1000, "_core", "_completed", true);

        var stats = new FakeHeapTypeStatisticsQuery();
        stats.SetValueTaskSourceType("MyVts", instanceCount: 1, coreFieldName: "_core");

        AsyncTaskDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), new FakeHeapReferenceQuery(),
            new FakeHeapSegmentQuery(), new FakeDumpArrayShapeQuery(), stats, Candidate(0x1000, "MyVts"));

        result.TotalValueTaskSources.Should().Be(1);
        result.PendingValueTaskSources.Should().Be(0);
    }

    // ── Test scaffolding ──────────────────────────────────────────────────────

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<AsyncTaskDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields,
        FakeHeapObjectLookup objectLookup,
        FakeHeapReferenceQuery references,
        FakeHeapSegmentQuery segments,
        FakeDumpArrayShapeQuery arrayShapes,
        FakeHeapTypeStatisticsQuery typeStatistics,
        params HeapObjectRef[] candidates)
    {
        var analyzer = new AsyncTaskAnalyzer();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new ArtifactId("test"),
            DumpObjectFields = fields,
            HeapObjectLookup = objectLookup,
            HeapReferences = references,
            HeapSegments = segments,
            DumpArrayShapes = arrayShapes,
            HeapTypeStatistics = typeStatistics,
            HeapObjects = new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (AsyncTaskDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool HasDelegateField(ulong address) => false;

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();
        private readonly Dictionary<(ulong Address, string OuterField, string InnerField), FieldValue> _nestedValues = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);
        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);
        public void SetString(ulong address, string fieldName, string value) => _values[(address, fieldName)] = FieldValue.ForString(value);
        public void SetNull(ulong address, string fieldName) => _values[(address, fieldName)] = FieldValue.Null;
        public void SetNestedBool(ulong address, string outerFieldName, string innerFieldName, bool value) =>
            _nestedValues[(address, outerFieldName, innerFieldName)] = FieldValue.ForBool(value);

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

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value) =>
            _nestedValues.TryGetValue((address, outerFieldName, innerFieldName), out value);
    }

    private sealed class FakeHeapObjectLookup : IHeapObjectLookup
    {
        private readonly Dictionary<ulong, (string TypeName, ulong Size)> _objects = new();
        public void SetObject(ulong address, string typeName, ulong size = 24) => _objects[address] = (typeName, size);

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

    private sealed class FakeHeapReferenceQuery : IHeapReferenceQuery
    {
        private readonly Dictionary<ulong, List<ulong>> _references = new();
        public void SetReferences(ulong address, params ulong[] targets) => _references[address] = [.. targets];

        public IEnumerable<ulong> EnumerateReferences(ulong address) =>
            _references.TryGetValue(address, out List<ulong>? targets) ? targets : [];

        public bool TryGetReferenceFieldName(ulong parentAddress, ulong childAddress, out string? fieldName)
        {
            fieldName = null;
            return false;
        }
    }

    private sealed class FakeHeapSegmentQuery : IHeapSegmentQuery
    {
        public HeapGenerationTag GetGeneration(ulong address) => HeapGenerationTag.Unknown;
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

    private sealed class FakeDumpArrayShapeQuery : IDumpArrayShapeQuery
    {
        private readonly Dictionary<ulong, ulong[]> _elements = new();
        public void SetObjectReferenceElements(ulong address, ulong[] elements) => _elements[address] = elements;

        public bool TryGetArrayShape(ulong address, out int length, out int rank)
        {
            length = 0;
            rank = 0;
            return false; // not exercised by this analyzer
        }

        public int? CountNullOrZeroElements(ulong address) => null; // not exercised by this analyzer

        public IReadOnlyList<ulong> EnumerateObjectReferenceElements(ulong address, int maxElements) =>
            _elements.TryGetValue(address, out ulong[]? elements) ? elements[..Math.Min(elements.Length, maxElements)] : [];
    }

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetTaskType(string typeName, long instanceCount) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null), instanceCount, 0, 0, 0, 0, 0, 0, 0, false,
                IsTaskType: true);

        public void SetTaskCompletionSourceType(string typeName, long instanceCount) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null), instanceCount, 0, 0, 0, 0, 0, 0, 0, false,
                IsTaskCompletionSourceType: true);

        public void SetValueTaskSourceType(string typeName, long instanceCount, string coreFieldName) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null), instanceCount, 0, 0, 0, 0, 0, 0, 0, false,
                ImplementsValueTaskSource: true, ValueTaskSourceCoreFieldName: coreFieldName);

        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => _stats;
        public bool HasExactGenerationData => true;
        public bool HasTypeShapeData => true;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }
}
