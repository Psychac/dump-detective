using DumpDetective.Analysis.Analyzers.Async;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization tests for the Tier-1+Tier-2 retyping of <see cref="AsyncStateMachineAnalyzer"/>
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — no pre-existing
/// per-analyzer test coverage existed before this. Proves: the suspend-state histogram builds from
/// every streamed instance (not just one sample per type, replacing the deleted second heap pass);
/// captured-closure-byte reads stay bounded to the first streamed instance per type; <c>IsAsyncVoid</c>
/// detection via <see cref="IDumpObjectFieldQuery.TryGetFieldTypeName"/>; and the
/// <c>!IsAsyncStateMachineType</c>/no-candidates empty-result gate.
/// </summary>
public sealed class AsyncStateMachineAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        AsyncStateMachineDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(), new FakeHeapTypeStatisticsQuery());

        result.TotalStateMachines.Should().Be(0);
        result.TopStateMachineTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_CandidateTypeNotFlaggedAsyncStateMachine_ExcludedFromResult()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetNonStateMachineType("System.String", instanceCount: 5);

        AsyncStateMachineDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, "System.String"));

        result.TotalStateMachines.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_SingleType_ParsesMethodNameAndDeclaringTypeFromCompilerGeneratedName()
    {
        const string typeName = "MyApp.Service+<DoWorkAsync>d__3";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeName, instanceCount: 1, totalSize: 64);

        AsyncStateMachineDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, typeName));

        StateMachineTypeProfile profile = result.TopStateMachineTypes.Should().ContainSingle().Subject;
        profile.OriginatingMethod.Should().Be("DoWorkAsync");
        profile.DeclaringType.Should().Be("MyApp.Service");
    }

    [Fact]
    public async Task AnalyzeAsync_MultipleInstancesOfOneType_BuildsHistogramAcrossEveryStreamedInstance()
    {
        const string typeName = "MyApp.Service+<DoWorkAsync>d__3";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeName, instanceCount: 3, totalSize: 192);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "<>1__state", 2);
        fields.SetInt32(0x1001, "<>1__state", 2);
        fields.SetInt32(0x1002, "<>1__state", 5);

        AsyncStateMachineDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, typeName), Candidate(0x1001, typeName), Candidate(0x1002, typeName));

        StateMachineTypeProfile profile = result.TopStateMachineTypes.Should().ContainSingle().Subject;
        profile.DominantState.Should().Be(2);
        profile.StateDistribution.Should().BeEquivalentTo([(2, 2), (5, 1)]);
    }

    [Fact]
    public async Task AnalyzeAsync_UnreadableStateField_YieldsDefaultDominantStateAndEmptyDistribution()
    {
        const string typeName = "MyApp.Service+<DoWorkAsync>d__3";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeName, instanceCount: 1, totalSize: 64);

        AsyncStateMachineDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, typeName));

        StateMachineTypeProfile profile = result.TopStateMachineTypes.Should().ContainSingle().Subject;
        profile.DominantState.Should().Be(0);
        profile.StateDistribution.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_CapturedClosureReads_OnlyHappenOnFirstStreamedInstancePerType()
    {
        const string typeName = "MyApp.Service+<DoWorkAsync>d__3";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeName, instanceCount: 2, totalSize: 128);

        var fields = new FakeDumpObjectFieldQuery();
        // First instance (the sample): one captured reference field.
        fields.SetReferenceField(0x1000, "_capturedService", 0x9000);
        // Second instance: a different reference field -- must never be read, since capture-byte
        // reads are bounded to the first streamed instance per type only.
        fields.SetReferenceField(0x1001, "_shouldNeverBeRead", 0x9999);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x9000, "MyApp.Services.HeartbeatService", size: 2_000_000);
        objectLookup.SetObject(0x9999, "MyApp.Services.OtherService", size: 2_000_000);

        AsyncStateMachineDomainResult result = await Analyze(fields, objectLookup, typeStats,
            Candidate(0x1000, typeName), Candidate(0x1001, typeName));

        HighCaptureStateMachine capture = result.TopByCapturedSize.Should().ContainSingle().Subject;
        capture.Address.Should().Be(0x1000);
        capture.TotalCapturedRefBytes.Should().Be(2_000_000);
        capture.LargeCaptures.Should().ContainSingle().Which.Should().Contain("_capturedService");
    }

    [Fact]
    public async Task AnalyzeAsync_CapturedFieldBelowLargeCaptureThreshold_CountsBytesButOmittedFromLargeCaptures()
    {
        const string typeName = "MyApp.Service+<DoWorkAsync>d__3";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeName, instanceCount: 1, totalSize: 64);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetReferenceField(0x1000, "_smallField", 0x9000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x9000, "System.Object", size: 16); // well under the 1 MB default threshold

        AsyncStateMachineDomainResult result = await Analyze(fields, objectLookup, typeStats,
            Candidate(0x1000, typeName));

        HighCaptureStateMachine capture = result.TopByCapturedSize.Should().ContainSingle().Subject;
        capture.TotalCapturedRefBytes.Should().Be(16);
        capture.LargeCaptures.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_AsyncVoidBuilderField_SetsIsAsyncVoid()
    {
        const string typeName = "MyApp.Service+<FireAndForgetAsync>d__7";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeName, instanceCount: 1, totalSize: 64);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetFieldTypeName(0x1000, "<>t__builder", "System.Runtime.CompilerServices.AsyncVoidMethodBuilder");

        AsyncStateMachineDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, typeName));

        result.TopStateMachineTypes.Should().ContainSingle().Which.IsAsyncVoid.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_AsyncTaskBuilderField_IsAsyncVoidFalse()
    {
        const string typeName = "MyApp.Service+<DoWorkAsync>d__3";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeName, instanceCount: 1, totalSize: 64);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetFieldTypeName(0x1000, "<>t__builder", "System.Runtime.CompilerServices.AsyncTaskMethodBuilder");

        AsyncStateMachineDomainResult result = await Analyze(fields, new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, typeName));

        result.TopStateMachineTypes.Should().ContainSingle().Which.IsAsyncVoid.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_TwoTypesSameOriginatingMethod_MergedInSuspendedMethodMap()
    {
        const string typeA = "MyApp.Service+<DoWorkAsync>d__3";
        const string typeB = "MyApp.Service+<DoWorkAsync>d__3[[System.String, mscorlib]]";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType(typeA, instanceCount: 2, totalSize: 100);
        typeStats.SetAsyncStateMachineType(typeB, instanceCount: 3, totalSize: 150);

        AsyncStateMachineDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, typeA), Candidate(0x2000, typeB));

        SuspendedMethodEntry entry = result.SuspendedMethodMap.Should().ContainSingle().Subject;
        entry.DeclaringType.Should().Be("MyApp.Service");
        entry.MethodName.Should().Be("DoWorkAsync");
        entry.SuspendedCount.Should().Be(5);
        entry.TotalBytes.Should().Be(250UL);
    }

    [Fact]
    public async Task AnalyzeAsync_TotalsSumEveryFlaggedType_IndependentOfPerInstanceStreaming()
    {
        // A type flagged IsAsyncStateMachineType in HeapTypeStatistics but never actually streamed
        // (e.g. it had zero live instances reach the shared scan) still contributes to the grand
        // totals -- matches the pre-retyping analyzer's own Step 2 behavior, computed directly from
        // TypeAggregates before any per-candidate name parsing/filtering.
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetAsyncStateMachineType("MyApp.Service+<StreamedAsync>d__1", instanceCount: 2, totalSize: 100, gen2Count: 1);
        typeStats.SetAsyncStateMachineType("MyApp.Service+<NeverStreamedAsync>d__2", instanceCount: 4, totalSize: 400, gen2Count: 4);

        AsyncStateMachineDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapObjectLookup(), typeStats,
            Candidate(0x1000, "MyApp.Service+<StreamedAsync>d__1"));

        result.TotalStateMachines.Should().Be(6);
        result.TotalStateMachineBytes.Should().Be(500UL);
        result.TotalGen2Count.Should().Be(5);
        result.TopStateMachineTypes.Should().ContainSingle(); // only the streamed type gets a per-type profile
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 64, TypeDisplayName: typeName);

    private static async Task<AsyncStateMachineDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields, FakeHeapObjectLookup objectLookup, FakeHeapTypeStatisticsQuery typeStatistics,
        params HeapObjectRef[] candidates)
    {
        AsyncStateMachineAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapObjectLookup = objectLookup,
            HeapTypeStatistics = typeStatistics,
            HeapObjects = candidates.Length == 0 ? null : new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (AsyncStateMachineDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool HasDelegateField(ulong address) => false;

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();
        private readonly Dictionary<(ulong Address, string FieldName), string> _fieldTypeNames = new();
        private readonly Dictionary<ulong, List<(string Name, ulong TargetAddress)>> _referenceFields = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);

        public void SetFieldTypeName(ulong address, string fieldName, string typeName) => _fieldTypeNames[(address, fieldName)] = typeName;

        public void SetReferenceField(ulong address, string fieldName, ulong targetAddress)
        {
            if (!_referenceFields.TryGetValue(address, out List<(string, ulong)>? list))
            {
                list = new List<(string, ulong)>();
                _referenceFields[address] = list;
            }
            list.Add((fieldName, targetAddress));
        }

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this analyzer
        }

        public bool TryGetStringValue(ulong address, int maxLength, out string? value) => TryGetStringValue(address, out value);

        public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName) =>
            _fieldTypeNames.TryGetValue((address, fieldName), out typeName!);

        public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address) =>
            _referenceFields.TryGetValue(address, out List<(string, ulong)>? list) ? list : [];

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false; // not exercised by this analyzer
        }
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

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetAsyncStateMachineType(string typeName, long instanceCount, ulong totalSize, long gen2Count = 0, int referenceFields = 0) =>
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
                IsFinalizableType: false,
                ReferenceFields: referenceFields,
                IsAsyncStateMachineType: true);

        public void SetNonStateMachineType(string typeName, long instanceCount) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null), instanceCount, 0, 0, 0, 0, 0, 0, 0, false, IsAsyncStateMachineType: false);

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
