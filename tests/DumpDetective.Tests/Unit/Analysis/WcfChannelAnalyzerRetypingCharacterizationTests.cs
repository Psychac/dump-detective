using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Enums;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="WcfChannelAnalyzer"/> had no pre-existing per-instance-classification coverage
/// (the pre-retyping test files only covered scan-participant plumbing and the pure static
/// helpers). Proves the state-bucket classification (including the "unreadable state still counts
/// toward Total but no bucket" distinction), the fixed remote-address extraction (both the direct
/// TryGetStringValue path and the Uri-internal-field fallback, under both known field names), and
/// factory counting via <see cref="IHeapTypeStatisticsQuery"/> -- all against fake, dictionary-backed
/// capability implementations.
/// </summary>
public sealed class WcfChannelAnalyzerRetypingCharacterizationTests
{
    private const string ChannelType = "System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel";

    [Fact]
    public async Task AnalyzeAsync_FaultedChannel_ChasesRemoteAddressThroughUriInternalStringField()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_state", 5); // Faulted
        fields.SetObjectReference(0x1000, "_remoteAddress", 0x2000); // EndpointAddress
        fields.SetObjectReference(0x2000, "_uri", 0x3000); // System.Uri -- not directly stringable
        fields.SetReadOnlyToString(0x3000); // TryGetStringValue fails: not a real String object
        fields.SetString(0x3000, "_string", "http://example.com/svc"); // modern-.NET Uri field name

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType));

        result.FaultedChannels.Should().Be(1);
        result.TopFaultedChannels.Should().ContainSingle().Which.RemoteAddress.Should().Be("http://example.com/svc");
    }

    [Fact]
    public async Task AnalyzeAsync_FaultedChannel_FallsBackToNetFrameworkUriFieldName_WhenModernNameAbsent()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_state", 5);
        fields.SetObjectReference(0x1000, "_remoteAddress", 0x2000);
        fields.SetObjectReference(0x2000, "_uri", 0x3000);
        fields.SetReadOnlyToString(0x3000);
        fields.SetString(0x3000, "m_String", "http://legacy.example.com/svc"); // .NET Framework Uri field name

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType));

        result.TopFaultedChannels.Should().ContainSingle().Which.RemoteAddress.Should().Be("http://legacy.example.com/svc");
    }

    [Fact]
    public async Task AnalyzeAsync_FaultedChannel_RemoteAddressResolvesDirectly_WhenFieldIsAlreadyAString()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_state", 5);
        fields.SetObjectReference(0x1000, "_via", 0x2000);
        fields.SetStringField(0x2000, "address", "net.tcp://server/svc"); // lookalike type, field is a bare string

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType));

        result.TopFaultedChannels.Should().ContainSingle().Which.RemoteAddress.Should().Be("net.tcp://server/svc");
    }

    [Fact]
    public async Task AnalyzeAsync_OpenedChannel_DoesNotAttemptRemoteAddressChase()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_state", 2); // Opened -- remote-address fields deliberately left unset

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType));

        result.OpenedChannels.Should().Be(1);
        result.TopFaultedChannels.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_UnreadableState_CountsTowardTotalButNoStateBucket()
    {
        // No "_state"/"state"/"communicationState" field set at all on this address -- matches the
        // pre-retyping analyzer's own "TrySample returns null, snap stays absent from every bucket"
        // behavior, distinct from an out-of-range InvalidState value.
        var fields = new FakeDumpObjectFieldQuery();

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType));

        result.ByType.Should().ContainSingle().Which.TotalCount.Should().Be(1);
        (result.OpeningChannels + result.OpenedChannels + result.FaultedChannels + result.ClosingChannels
            + result.ClosedChannels + result.OtherChannels + result.InvalidStateCount).Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_OutOfRangeState_CountsAsInvalidState_NotOther()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_state", 99);

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType));

        result.InvalidStateCount.Should().Be(1);
        result.OtherChannels.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_CreatedState_CountsAsOther()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_state", 0); // Created -- valid, just uncommon to observe on heap

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType));

        result.OtherChannels.Should().Be(1);
        result.InvalidStateCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_DuplexSessionChannelType_CountsTowardBothOverlappingClassifications()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "_state", 2);

        WcfChannelDomainResult result = await Analyze(fields, new FakeHeapTypeStatisticsQuery(), null,
            Candidate(0x1000, ChannelType)); // "...ClientFramingDuplexSessionChannel"

        result.DuplexChannelCount.Should().Be(1);
        result.SessionChannelCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_FactoryTypes_CountedFromHeapTypeStatistics_IndependentOfChannelCandidates()
    {
        var typeStatistics = new FakeHeapTypeStatisticsQuery();
        typeStatistics.SetInstanceCount("System.ServiceModel.Channels.TcpChannelFactory", 3);

        WcfChannelDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), typeStatistics, null);

        result.WcfPresent.Should().BeTrue();
        result.FactoryCount.Should().Be(3);
        result.TotalChannels.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidatesAndNoFactories_ReturnsEmptyResult()
    {
        WcfChannelDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery(), null);

        result.WcfPresent.Should().BeFalse();
        result.TotalChannels.Should().Be(0);
        result.FactoryCount.Should().Be(0);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<WcfChannelDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields, FakeHeapTypeStatisticsQuery typeStatistics, IHeapDominatorQuery? dominators,
        params HeapObjectRef[] candidates)
    {
        WcfChannelAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapTypeStatistics = typeStatistics,
            HeapDominators = dominators,
            HeapObjects = candidates.Length == 0 ? null : new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (WcfChannelDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();
        private readonly HashSet<ulong> _notDirectlyStringable = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);
        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);

        /// <summary>Sets a genuine string-typed field (e.g. a lookalike type's bare string field).</summary>
        public void SetStringField(ulong address, string fieldName, string value) => _values[(address, fieldName)] = FieldValue.ForString(value);

        /// <summary>Marks an address as "not a real String object" -- TryGetStringValue on it fails,
        /// matching ClrObject.AsString() throwing on a non-String object like System.Uri.</summary>
        public void SetReadOnlyToString(ulong address) => _notDirectlyStringable.Add(address);

        /// <summary>Sets a string field intended to be read via TryReadField (simulating chasing into
        /// System.Uri's internal string-holding field), distinct from TryGetStringValue's direct path.</summary>
        public void SetString(ulong address, string fieldName, string value) => _values[(address, fieldName)] = FieldValue.ForString(value);

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return !_notDirectlyStringable.Contains(address);
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

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetInstanceCount(string typeName, long instanceCount) =>
            _stats[typeName] = new HeapTypeStatistics(SdkTypeRefFactory.FromName(typeName, null), instanceCount, 0, 0, 0, 0, 0, 0, 0, false);

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
