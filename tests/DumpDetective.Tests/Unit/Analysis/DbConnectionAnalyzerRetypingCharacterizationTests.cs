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
/// proves <see cref="DbConnectionAnalyzer"/> reproduces the pre-retyping analyzer's state
/// classification (including the "unreadable state counts as Unknown" behavior -- distinct from
/// WcfChannelAnalyzer's "unreadable state joins no bucket"), generation-based Gen2/Gen0 open
/// tracking, retained-bytes/root-path enrichment, and pool grouping, against fake, dictionary-backed
/// capability implementations.
/// </summary>
public sealed class DbConnectionAnalyzerRetypingCharacterizationTests
{
    private const string ConnType = "Microsoft.Data.SqlClient.SqlConnection";

    [Fact]
    public async Task AnalyzeAsync_OpenConnection_TracksGeneration2AsLikelyLeaked()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 1); // Open
        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen2);

        DbConnectionDomainResult result = await Analyze(fields, segments, null, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType));

        result.OpenConnections.Should().Be(1);
        result.Gen2OpenConnections.Should().Be(1);
        result.Gen0OpenConnections.Should().Be(0);
        result.TopOpenConnections.Should().ContainSingle().Which.Generation.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_ClosedConnection_CountsAsClosed_NoSnapshotSurfaced()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 0); // Closed

        DbConnectionDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), null, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType));

        result.ClosedConnections.Should().Be(1);
        result.TopOpenConnections.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_BrokenConnection_CountsAsBroken()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 16); // Broken

        DbConnectionDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), null, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType));

        result.BrokenConnections.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_ConnectingState_CountsAsOther()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 2); // Connecting -- valid read, unmapped state

        DbConnectionDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), null, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType));

        result.OtherConnections.Should().Be(1);
        result.UnknownStateConnections.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_UnreadableState_CountsAsUnknown_UnlikeWcfChannelAnalyzersNoBucketBehavior()
    {
        // No "_connectionState"/"_state"/"m_connectionState" field set at all -- this analyzer's
        // pre-retyping OnHeapEntry always incremented a counter (unknown++) in this case, unlike
        // WcfChannelAnalyzer's OnHeapEntry, which left an unreadable-state instance out of every
        // bucket entirely. A genuine, pre-existing difference between the two analyzers.
        var fields = new FakeDumpObjectFieldQuery();

        DbConnectionDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), null, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType));

        result.UnknownStateConnections.Should().Be(1);
        result.ByType.Should().ContainSingle().Which.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_OpenConnection_AnonymisesConnectionString()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 1);
        fields.SetStringField(0x1000, "_connectionString", "Server=srv;Password=hunter2;");

        DbConnectionDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), null, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType));

        string? anonymised = result.TopOpenConnections.Should().ContainSingle().Subject.AnonymisedConnectionString;
        anonymised.Should().Contain("Password=***").And.NotContain("hunter2");
    }

    [Fact]
    public async Task AnalyzeAsync_OpenConnection_EnrichesRetainedBytesWhenDominatorsAvailable()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 1);
        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x1000, 4096);

        DbConnectionDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), dominators, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType));

        result.TopOpenConnections.Should().ContainSingle().Which.RetainedBytes.Should().Be(4096UL);
    }

    [Fact]
    public async Task AnalyzeAsync_Gen2OpenConnection_GetsRootPathEnrichment()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 1);
        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen2);
        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar: MyApp.Cache@0x2000 -> Microsoft.Data.SqlClient.SqlConnection@0x1000");

        DbConnectionDomainResult result = await Analyze(fields, segments, null, rootPaths, Candidate(0x1000, ConnType));

        result.TopOpenConnections.Should().ContainSingle().Which.RootPath
            .Should().Be("StaticVar: MyApp.Cache@0x2000 -> Microsoft.Data.SqlClient.SqlConnection@0x1000");
    }

    [Fact]
    public async Task AnalyzeAsync_Gen0OpenConnection_SkipsRootPathEnrichment()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 1);
        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen0);
        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar: Should@0xNotBeUsed");

        DbConnectionDomainResult result = await Analyze(fields, segments, null, rootPaths, Candidate(0x1000, ConnType));

        result.TopOpenConnections.Should().ContainSingle().Which.RootPath.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_OpenConnectionsSharingConnectionString_GroupIntoOnePool()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_connectionState", 1);
        fields.SetStringField(0x1000, "_connectionString", "Server=srv;Database=db;");
        fields.SetInt32(0x1001, "_connectionState", 1);
        fields.SetStringField(0x1001, "_connectionString", "Server=srv;Database=db;");

        DbConnectionDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), null, new FakeHeapRootPathQuery(),
            Candidate(0x1000, ConnType), Candidate(0x1001, ConnType));

        result.TopPools.Should().ContainSingle().Which.OpenConnections.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        DbConnectionDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), null, new FakeHeapRootPathQuery());

        result.ConnectionsFound.Should().BeFalse();
        result.TotalConnections.Should().Be(0);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<DbConnectionDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields, FakeHeapSegmentQuery segments, IHeapDominatorQuery? dominators, FakeHeapRootPathQuery rootPaths,
        params HeapObjectRef[] candidates)
    {
        DbConnectionAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapSegments = segments,
            HeapDominators = dominators,
            HeapRootPaths = rootPaths,
            HeapObjects = candidates.Length == 0 ? null : new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (DbConnectionDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);
        public void SetStringField(ulong address, string fieldName, string value) => _values[(address, fieldName)] = FieldValue.ForString(value);

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this analyzer -- _connectionString resolves via TryReadField's own String case
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

    private sealed class FakeHeapDominatorQuery : IHeapDominatorQuery
    {
        private readonly Dictionary<ulong, ulong> _retained = new();
        public void SetRetainedSize(ulong address, ulong bytes) => _retained[address] = bytes;

        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) => _retained.TryGetValue(address, out retainedBytes);
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) { retainedBytes = 0; return false; }
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
    }
}
