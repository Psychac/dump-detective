using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Types;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using HeapSegmentKind = DumpDetective.Sdk.Analysis.HeapSegmentKind;
using RegionGenerationKind = DumpDetective.Sdk.Analysis.RegionGenerationKind;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization tests for the Tier-1+Tier-2 retyping of <see cref="StringAnalyzer"/>
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Proves: scalar aggregate
/// stats come from <see cref="IHeapTypeStatisticsQuery"/> regardless of whether a candidate stream
/// ran; dedup fingerprinting/duplicate detection/length-distribution/very-long-string capture all
/// run off the streamed <c>System.String</c> candidates via <see cref="IDumpObjectFieldQuery.TryGetStringValue(ulong,int,out string?)"/>;
/// FOH/interned detection via <see cref="IHeapSegmentQuery"/>; P1-3 owner-type bytes pass through
/// unchanged from the adapter-precomputed <see cref="StringAnalyzerOptions.OwnerTypeBytes"/>; and the
/// no-candidate-stream empty-result gate for dedup-derived fields.
/// </summary>
public sealed class StringAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_NoCandidateStream_ReturnsScalarStatsButNoDedupData()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetStringType(instanceCount: 100, totalSize: 5000, gen0: 60, gen1: 30, gen2: 10, gen2Bytes: 500, lohSize: 0);

        StringDomainResult result = await Analyze(typeStats, new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), candidates: null, ownerTypeBytes: null);

        result.TotalStrings.Should().Be(100);
        result.TotalStringMemoryBytes.Should().Be(5000UL);
        result.Gen0StringCount.Should().Be(60);
        result.Gen1StringCount.Should().Be(30);
        result.Gen2StringCount.Should().Be(10);
        result.Gen2StringBytes.Should().Be(500UL);
        result.StringsSampled.Should().Be(0);
        result.DedupSource.Should().BeNull();
        result.TopDuplicates.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_DuplicateStrings_DetectedAndRankedByWastedBytes()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetStringType(instanceCount: 3, totalSize: 300, gen0: 0, gen1: 0, gen2: 0, gen2Bytes: 0, lohSize: 0);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(0x1000, "duplicate-value");
        fields.SetString(0x1001, "duplicate-value");
        fields.SetString(0x1002, "duplicate-value");

        var options = new StringAnalysisOptions { MinDuplicateStringCount = 2, MinDuplicateCharLength = 1 };

        StringDomainResult result = await Analyze(typeStats, fields, new FakeHeapSegmentQuery(), new FakeHeapRootPathQuery(),
            candidates: [Candidate(0x1000, 100), Candidate(0x1001, 100), Candidate(0x1002, 100)], ownerTypeBytes: null,
            options: options);

        result.StringsSampled.Should().Be(3);
        result.DedupSource.Should().Be("IndexScan");
        DuplicateStringSnapshot dup = result.TopDuplicates.Should().ContainSingle().Subject;
        dup.Preview.Should().Be("duplicate-value");
        dup.Count.Should().Be(3);
        dup.DominantType.Should().Be("System.String");
        dup.SampleAddresses.Should().Contain(0x1000);
    }

    [Fact]
    public async Task AnalyzeAsync_DistinctStrings_NotReportedAsDuplicates()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetStringType(instanceCount: 2, totalSize: 200, gen0: 0, gen1: 0, gen2: 0, gen2Bytes: 0, lohSize: 0);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(0x1000, "value-one");
        fields.SetString(0x1001, "value-two");

        StringDomainResult result = await Analyze(typeStats, fields, new FakeHeapSegmentQuery(), new FakeHeapRootPathQuery(),
            candidates: [Candidate(0x1000, 100), Candidate(0x1001, 100)], ownerTypeBytes: null);

        result.TopDuplicates.Should().BeEmpty();
        result.SampledUniquePatterns.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_ObjectBeyondVeryLongThreshold_CapturedRegardlessOfDuplicateStatus()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetStringType(instanceCount: 1, totalSize: 100_000, gen0: 0, gen1: 0, gen2: 0, gen2Bytes: 0, lohSize: 100_000);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(0x1000, new string('a', 100));

        var options = new StringAnalysisOptions { VeryLongStringThresholdBytes = 1000 };

        StringDomainResult result = await Analyze(typeStats, fields, new FakeHeapSegmentQuery(), new FakeHeapRootPathQuery(),
            candidates: [Candidate(0x1000, 100_000)], ownerTypeBytes: null, options: options);

        LongStringEntry entry = result.VeryLongStrings.Should().ContainSingle().Subject;
        entry.Address.Should().Be(0x1000);
        entry.SizeBytes.Should().Be(100_000UL);
        entry.TypeName.Should().Be("System.String");
    }

    [Fact]
    public async Task AnalyzeAsync_CandidateInFrozenSegment_CountedAsInterned()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetStringType(instanceCount: 1, totalSize: 100, gen0: 0, gen1: 0, gen2: 0, gen2Bytes: 0, lohSize: 0);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(0x1000, "interned");

        var segments = new FakeHeapSegmentQuery();
        segments.AddFrozenRange(0x1000, 0x2000);

        StringDomainResult result = await Analyze(typeStats, fields, segments, new FakeHeapRootPathQuery(),
            candidates: [Candidate(0x1000, 100)], ownerTypeBytes: null);

        result.InternedStringCount.Should().Be(1);
        result.InternedStringBytes.Should().Be(100UL);
    }

    [Fact]
    public async Task AnalyzeAsync_OwnerTypeBytes_PassesThroughFromAdapterPrecomputedAggregate()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetStringType(instanceCount: 0, totalSize: 0, gen0: 0, gen1: 0, gen2: 0, gen2Bytes: 0, lohSize: 0);

        IReadOnlyList<(string TypeName, ulong TotalBytes)> ownerTypeBytes =
            [("MyApp.Widget", 5_000_000UL), ("MyApp.Gadget", 1_000UL)];

        StringDomainResult result = await Analyze(typeStats, new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(),
            new FakeHeapRootPathQuery(), candidates: null, ownerTypeBytes: ownerTypeBytes);

        result.TopStringOwnerTypes.Should().BeEquivalentTo(ownerTypeBytes, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task AnalyzeAsync_TopDuplicates_GetRootPathSearch()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetStringType(instanceCount: 2, totalSize: 200, gen0: 0, gen1: 0, gen2: 0, gen2Bytes: 0, lohSize: 0);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(0x1000, "leaked-value");
        fields.SetString(0x1001, "leaked-value");

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "Static: MyApp.Cache.Instance -> System.String[]@0x2000 -> System.String@0x1000");

        var options = new StringAnalysisOptions { MinDuplicateStringCount = 2, MinDuplicateCharLength = 1, RetentionPathSampleCount = 5 };

        StringDomainResult result = await Analyze(typeStats, fields, new FakeHeapSegmentQuery(), rootPaths,
            candidates: [Candidate(0x1000, 100), Candidate(0x1001, 100)], ownerTypeBytes: null, options: options);

        DuplicateStringRetentionPath path = result.TopDuplicateRetentionPaths.Should().ContainSingle().Subject;
        path.HasGcRoot.Should().BeTrue();
        path.RootPath.Should().Contain("Static:");
    }

    private static HeapObjectRef Candidate(ulong address, ulong size) =>
        new(address, SdkTypeRefFactory.FromName("System.String", null), size, "System.String");

    private static async Task<StringDomainResult> Analyze(
        FakeHeapTypeStatisticsQuery typeStats, FakeDumpObjectFieldQuery fields, FakeHeapSegmentQuery segments,
        FakeHeapRootPathQuery rootPaths, HeapObjectRef[]? candidates,
        IReadOnlyList<(string TypeName, ulong TotalBytes)>? ownerTypeBytes, StringAnalysisOptions? options = null)
    {
        StringAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            HeapTypeStatistics = typeStats,
            DumpObjectFields = fields,
            HeapSegments = segments,
            HeapRootPaths = rootPaths,
            HeapObjects = candidates is null ? null : new PrecomputedHeapObjectStream(candidates),
            AnalyzerOptions = new StringAnalyzerOptions(options ?? new StringAnalysisOptions(), ownerTypeBytes),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (StringDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        private readonly Dictionary<ulong, string> _strings = new();

        public void SetString(ulong address, string value) => _strings[address] = value;

        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool HasDelegateField(ulong address) => false;

        public bool TryReadField(ulong address, string fieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false;
        }

        public bool TryGetStringValue(ulong address, out string? value) => _strings.TryGetValue(address, out value);

        public bool TryGetStringValue(ulong address, int maxLength, out string? value)
        {
            if (!_strings.TryGetValue(address, out string? full))
            {
                value = null;
                return false;
            }
            value = full.Length > maxLength ? full[..maxLength] : full;
            return true;
        }

        public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName)
        {
            typeName = null;
            return false;
        }

        public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address) => [];

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false;
        }
    }

    private sealed class FakeHeapSegmentQuery : IHeapSegmentQuery
    {
        private readonly List<HeapSegmentRef> _segments = new();

        public void AddFrozenRange(ulong start, ulong end) => _segments.Add(new HeapSegmentRef
        {
            Start = start,
            End = end,
            Generation = -1,
            Address = start,
            Kind = HeapSegmentKind.Frozen,
            RegionKind = RegionGenerationKind.Frozen,
            CommittedBytes = end - start,
            ReservedBytes = end - start,
            LogicalHeapIndex = 0,
            IsEphemeral = false,
            Gen0Bytes = 0,
            Gen1Bytes = 0,
            Gen2Bytes = 0,
        });

        public IEnumerable<HeapSegmentRef> EnumerateSegments() => _segments;
        public bool TryGetSegment(ulong address, out HeapSegmentRef segment) { segment = default; return false; }
        public int DumpPointerSize => 8;
        public bool IsServerGc => false;
        public int LogicalHeapCount => 1;
        public bool CanWalkHeap => true;
        public IEnumerable<HeapObjectRef> EnumerateObjects(HeapSegmentRef segment, bool includeFree = false) => [];
        public HeapGenerationTag GetGeneration(ulong address) => HeapGenerationTag.Unknown;
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

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetStringType(long instanceCount, ulong totalSize, long gen0, long gen1, long gen2, ulong gen2Bytes, ulong lohSize) =>
            _stats["System.String"] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName("System.String", null),
                InstanceCount: instanceCount,
                TotalSize: totalSize,
                LohCount: 0,
                LohSize: lohSize,
                Gen0Count: gen0,
                Gen1Count: gen1,
                Gen2Count: gen2,
                Gen2TotalSize: gen2Bytes,
                IsFinalizableType: false,
                IsStringType: true);

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
