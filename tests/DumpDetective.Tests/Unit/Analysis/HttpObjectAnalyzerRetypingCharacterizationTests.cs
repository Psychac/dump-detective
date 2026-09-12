using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="HttpObjectAnalyzer"/> had no pre-existing test coverage at all before this retyping.
/// Proves the field-chase logic for all five sample shapes (HttpClient, HttpWebRequest,
/// ActiveHandlerTrackingEntry, ServicePoint) plus HttpClient generation breakdown and
/// HttpMessageHandler module attribution against fake, dictionary-backed capability
/// implementations — nothing here needs a live or snapshotted heap.
/// </summary>
public sealed class HttpObjectAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_HttpClient_ReadsBaseAddressTimeoutAndGeneration()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_baseAddress", 0x9000);
        fields.SetString(0x9000, "http://example.com/");
        fields.SetInt64(0x1000, "_timeout", TimeSpan.FromSeconds(30).Ticks);

        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen2);

        HttpObjectDomainResult result = await Analyze(fields, segments, new FakeHeapTypeStatisticsQuery(),
            Candidate(0x1000, "System.Net.Http.HttpClient"));

        result.HttpClientCount.Should().Be(1);
        result.HttpClientGen2Count.Should().Be(1);
        result.HttpClientGen0Count.Should().Be(0);
        HttpInstanceSnapshot snap = result.TopHttpInstances.Should().ContainSingle().Subject;
        snap.Uri.Should().Be("http://example.com/");
        snap.TimeoutMilliseconds.Should().Be(30_000);
    }

    [Fact]
    public async Task AnalyzeAsync_HttpWebRequest_Net5Plus_ResponsePendingWhenBegunNotEnded()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_requestUri", 0x9000);
        fields.SetString(0x9000, "http://example.com/api");
        fields.SetBool(0x1000, "_beginGetResponseCalled", true);
        fields.SetBool(0x1000, "_endGetResponseCalled", false);

        HttpObjectDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapTypeStatisticsQuery(),
            Candidate(0x1000, "System.Net.HttpWebRequest"));

        HttpInstanceSnapshot snap = result.TopHttpInstances.Should().ContainSingle().Subject;
        snap.Uri.Should().Be("http://example.com/api");
        snap.ResponsePending.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_HttpWebRequest_NetFramework_FallsBackToSubmittedFieldPair()
    {
        // No _beginGetResponseCalled/_endGetResponseCalled at all -- falls through to
        // m_RequestSubmitted + _HttpResponse, matching the pre-retyping analyzer's own fallback.
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_Uri", 0x9000);
        fields.SetString(0x9000, "http://example.com/legacy");
        fields.SetBool(0x1000, "m_RequestSubmitted", true);
        fields.SetNull(0x1000, "_HttpResponse");

        HttpObjectDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapTypeStatisticsQuery(),
            Candidate(0x1000, "System.Net.HttpWebRequest"));

        HttpInstanceSnapshot snap = result.TopHttpInstances.Should().ContainSingle().Subject;
        snap.Uri.Should().Be("http://example.com/legacy");
        snap.ResponsePending.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_ActiveHandlerTrackingEntry_ReadsClientName()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "<Name>k__BackingField", 0x9000);
        fields.SetString(0x9000, "MyNamedClient");

        HttpObjectDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapTypeStatisticsQuery(),
            Candidate(0x1000, "Microsoft.Extensions.Http.ActiveHandlerTrackingEntry"));

        result.ActiveHandlerTrackingEntryCount.Should().Be(1);
        HttpInstanceSnapshot snap = result.TopHttpInstances.Should().ContainSingle().Subject;
        snap.Category.Should().Be("ActiveHandlerTrackingEntry");
        snap.TypeName.Should().Be("Microsoft.Extensions.Http.ActiveHandlerTrackingEntry");
        snap.ClientName.Should().Be("MyNamedClient");
    }

    [Fact]
    public async Task AnalyzeAsync_ServicePoint_ReadsConnectionLimit()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionLimit", 4);

        HttpObjectDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapTypeStatisticsQuery(),
            Candidate(0x1000, "System.Net.ServicePoint"));

        HttpInstanceSnapshot snap = result.TopHttpInstances.Should().ContainSingle().Subject;
        snap.ConnectionLimit.Should().Be(4);
    }

    [Fact]
    public async Task AnalyzeAsync_HttpMessageHandler_AttributedByOwningModule()
    {
        // Must actually match IsHttpMessageHandler's real rule: "System.Net.Http." prefix AND
        // ends with "Handler" (or contains the "HttpMessageHandler" token) -- a fictional type name
        // outside that namespace, however handler-like it sounds, correctly never classifies as one.
        const string handlerTypeName = "System.Net.Http.SocketsHttpHandler";
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetModuleName(handlerTypeName, "MyApp.Handlers.dll");

        HttpObjectDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), typeStats,
            Candidate(0x1000, handlerTypeName), Candidate(0x1001, handlerTypeName));

        result.HttpMessageHandlerCount.Should().Be(2);
        HttpHandlerModuleSummary module = result.HandlerModules.Should().ContainSingle().Subject;
        module.ModuleName.Should().Be("MyApp.Handlers.dll");
        module.Count.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        HttpObjectDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), new FakeHeapTypeStatisticsQuery());

        result.HttpObjectsFound.Should().BeFalse();
        result.TotalHttpObjects.Should().Be(0);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<HttpObjectDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields, FakeHeapSegmentQuery segments, FakeHeapTypeStatisticsQuery typeStatistics,
        params HeapObjectRef[] candidates)
    {
        HttpObjectAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapSegments = segments,
            HeapTypeStatistics = typeStatistics,
            HeapObjects = candidates.Length == 0 ? null : new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (HttpObjectDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();
        private readonly Dictionary<ulong, string?> _strings = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);
        public void SetInt64(ulong address, string fieldName, long value) => _values[(address, fieldName)] = FieldValue.ForInt64(value);
        public void SetBool(ulong address, string fieldName, bool value) => _values[(address, fieldName)] = FieldValue.ForBool(value);
        public void SetNull(ulong address, string fieldName) => _values[(address, fieldName)] = FieldValue.Null;
        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);
        public void SetString(ulong address, string value) => _strings[address] = value;

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value) =>
            _strings.TryGetValue(address, out value);

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

    private sealed class FakeHeapSegmentQuery : IHeapSegmentQuery
    {
        private readonly Dictionary<ulong, HeapGenerationTag> _generations = new();
        public void SetGeneration(ulong address, HeapGenerationTag tag) => _generations[address] = tag;

        public HeapGenerationTag GetGeneration(ulong address) => _generations.TryGetValue(address, out var tag) ? tag : HeapGenerationTag.Unknown;

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

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetModuleName(string typeName, string moduleName) =>
            _stats[typeName] = new HeapTypeStatistics(SdkTypeRefFactory.FromName(typeName, null), 0, 0, 0, 0, 0, 0, 0, 0, false, ModuleName: moduleName);

        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => _stats;
        public bool HasExactGenerationData => true;
        public bool HasTypeShapeData => false;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }
}
