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
/// <see cref="TimerLeakAnalyzer"/> had no pre-existing test coverage at all before this retyping.
/// Proves per-category classification, the interval histogram's UInt32-based period reading (both
/// the modern "_period" and .NET Framework "m_period" field names, and the uint.MaxValue infinite
/// sentinel), callback-owner resolution through the delegate's "_target" field, and root-path
/// evidence enrichment — all against fake, dictionary-backed capability implementations.
/// </summary>
public sealed class TimerLeakAnalyzerRetypingCharacterizationTests
{
    [Theory]
    [InlineData("System.Threading.Timer")]
    [InlineData("System.Timers.Timer")]
    [InlineData("System.Threading.TimerHolder")]
    [InlineData("System.Threading.PeriodicTimer")]
    public async Task AnalyzeAsync_NamedTimerWrapperTypes_ClassifiedCorrectly(string typeName)
    {
        TimerLeakDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, typeName));

        result.TotalTimers.Should().Be(1);
        result.ByType.Should().ContainSingle().Which.TypeName.Should().Be(typeName);
    }

    [Fact]
    public async Task AnalyzeAsync_OtherTimerNamespaceToken_ClassifiedAsOtherTimer()
    {
        TimerLeakDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.SomeVendorTimerWrapper"));

        result.OtherTimerCount.Should().Be(1);
    }

    [Theory]
    [InlineData("System.Threading.TimerQueue")]
    [InlineData("System.Threading.TimerThread")]
    public void IsCandidateType_ExcludesClrInternalTimerMachineryTypes(string typeName)
    {
        TimerLeakAnalyzer.IsCandidateType(typeName).Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_ReadsPeriod_LessThan100MsBucket()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUInt32(0x1000, "_period", 50);

        TimerLeakDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.IntervalHistogram.Should().ContainSingle(b => b.Bucket == "< 100 ms" && b.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_ReadsPeriod_Between100MsAnd1sBucket()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUInt32(0x1000, "_period", 500);

        TimerLeakDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.IntervalHistogram.Should().ContainSingle(b => b.Bucket == "100 ms – 1 s" && b.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_ReadsPeriod_MoreThan1sBucket()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUInt32(0x1000, "_period", 5_000);

        TimerLeakDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.IntervalHistogram.Should().ContainSingle(b => b.Bucket == "> 1 s" && b.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_UIntMaxValuePeriod_ClassifiedAsInfinite()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUInt32(0x1000, "_period", uint.MaxValue);

        TimerLeakDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.IntervalHistogram.Should().ContainSingle(b => b.Bucket == "Infinite" && b.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_UnreadablePeriod_ClassifiedAsInfinite()
    {
        // No "_period"/"m_period" field set at all -- matches the pre-retyping analyzer's own
        // "read failure treated the same as the infinite sentinel" behavior.
        TimerLeakDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.IntervalHistogram.Should().ContainSingle(b => b.Bucket == "Infinite" && b.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_FallsBackToNetFrameworkPeriodFieldName()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUInt32(0x1000, "m_period", 50); // .NET Framework field name, "_period" absent

        TimerLeakDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.IntervalHistogram.Should().ContainSingle(b => b.Bucket == "< 100 ms" && b.Count == 1);
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_ResolvesCallbackOwnerThroughDelegateTarget()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUInt32(0x1000, "_period", 1_000);
        fields.SetObjectReference(0x1000, "_timerCallback", 0x2000); // TimerCallback delegate
        fields.SetObjectReference(0x2000, "_target", 0x3000); // bound instance

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x3000, "MyApp.Services.HeartbeatService");

        TimerLeakDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), objectLookup, new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.ByType.Should().ContainSingle().Which.Samples.Should().ContainSingle()
            .Which.CallbackOwnerType.Should().Be("MyApp.Services.HeartbeatService");
    }

    [Fact]
    public async Task AnalyzeAsync_TimerQueueTimer_StaticCallback_HasNullCallbackOwner()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUInt32(0x1000, "_period", 1_000);
        fields.SetObjectReference(0x1000, "_timerCallback", 0x2000);
        fields.SetNull(0x2000, "_target"); // static method callback -- no bound instance

        TimerLeakDomainResult result = await Analyze(fields, new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.TimerQueueTimer"));

        result.ByType.Should().ContainSingle().Which.Samples.Should().ContainSingle()
            .Which.CallbackOwnerType.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_EachTimerType_GetsRootPathEvidence()
    {
        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar: MyApp.Registry@0x2000 -> System.Threading.Timer@0x1000");

        TimerLeakDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), rootPaths,
            Candidate(0x1000, "System.Threading.Timer"));

        result.ByType.Should().ContainSingle().Which.Evidence!.SampleRootPath
            .Should().Be("StaticVar: MyApp.Registry@0x2000 -> System.Threading.Timer@0x1000");
    }

    [Fact]
    public async Task AnalyzeAsync_MultipleTypes_TotalTimersSumsAcrossCategories()
    {
        TimerLeakDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery(),
            Candidate(0x1000, "System.Threading.Timer"), Candidate(0x1001, "System.Timers.Timer"), Candidate(0x1002, "System.Timers.Timer"));

        result.TotalTimers.Should().Be(3);
        result.ThreadingTimerCount.Should().Be(1);
        result.TimersTimerCount.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        TimerLeakDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapSegmentQuery(), new FakeHeapObjectLookup(), new FakeHeapRootPathQuery());

        result.TimersFound.Should().BeFalse();
        result.TotalTimers.Should().Be(0);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<TimerLeakDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields, FakeHeapSegmentQuery segments, FakeHeapObjectLookup objectLookup, FakeHeapRootPathQuery rootPaths,
        params HeapObjectRef[] candidates)
    {
        TimerLeakAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapSegments = segments,
            HeapObjectLookup = objectLookup,
            HeapRootPaths = rootPaths,
            HeapObjects = candidates.Length == 0 ? null : new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (TimerLeakDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();

        public void SetUInt32(ulong address, string fieldName, uint value) => _values[(address, fieldName)] = FieldValue.ForUInt32(value);
        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);
        public void SetNull(ulong address, string fieldName) => _values[(address, fieldName)] = FieldValue.Null;

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this analyzer
        }

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

    private sealed class FakeHeapObjectLookup : IHeapObjectLookup
    {
        private readonly Dictionary<ulong, string> _typeNames = new();
        public void SetObject(ulong address, string typeName) => _typeNames[address] = typeName;

        public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
        {
            if (_typeNames.TryGetValue(address, out string? typeName))
            {
                heapObject = new HeapObjectRef(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);
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
    }
}
