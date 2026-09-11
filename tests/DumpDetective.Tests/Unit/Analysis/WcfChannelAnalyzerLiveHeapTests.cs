using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

// Test-only fixture types matching WcfChannelAnalyzer.IsCandidateType's namespace-prefix +
// "Channel"-contains-token match, without depending on the actual System.ServiceModel package
// (not referenced by this project -- R10, hard-need basis: no new external dependency for this).
namespace System.ServiceModel
{
    internal sealed class FakeFaultedChannel
    {
        private readonly int _state;
        private readonly object? _remoteAddress;

        public FakeFaultedChannel(int state, object? remoteAddress)
        {
            _state = state;
            _remoteAddress = remoteAddress;
        }
    }

    internal sealed class FakeEndpointAddress
    {
        private readonly Uri? _uri;

        public FakeEndpointAddress(Uri? uri) => _uri = uri;
    }
}

namespace DumpDetective.Tests.Unit.Analysis
{
    /// <summary>
    /// Ported for the Tier-2 retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// this file's pre-retyping coverage of <c>MergePartial</c>'s new-key-from-worker branch is now
    /// pure generic base-class behavior (<c>TypedResourceCandidateStreamingLegacyAdapter</c>),
    /// already covered once by <c>SqlConnectionPoolAnalyzerHeapIndexScanTests</c>. What only a real
    /// live heap can prove: that the remote-address fix -- chasing into <c>System.Uri</c>'s internal
    /// string-holding field instead of the pre-retyping analyzer's broken <c>ClrObject.ToString()</c>
    /// fallback -- actually resolves a real, live <see cref="Uri"/> object, not just a
    /// dictionary-backed fake. This test process runs on modern .NET, so it exercises the "_string"
    /// field name (<c>System.Private.Uri.dll</c>); the .NET Framework "m_String" name is verified by
    /// decompiling the real assembly (see <see cref="WcfChannelAnalyzer"/>'s remarks) and exercised
    /// for real by <c>WcfChannelAnalyzerRealDumpTests</c> against the .NET Framework reference dump.
    /// </summary>
    public sealed class WcfChannelAnalyzerLiveHeapTests
    {
        private static System.ServiceModel.FakeFaultedChannel? s_faultedChannel;
        private static System.ServiceModel.FakeEndpointAddress? s_endpointAddress;
        private static Uri? s_uri;

        [Fact]
        public async Task AnalyzeAsync_FaultedChannel_ResolvesRemoteAddressThroughRealSystemUri()
        {
            s_uri = new Uri("http://example.com/MyService.svc");
            s_endpointAddress = new System.ServiceModel.FakeEndpointAddress(s_uri);
            s_faultedChannel = new System.ServiceModel.FakeFaultedChannel(state: 5, remoteAddress: s_endpointAddress);

            var (dataTarget, heap, methodTable, address) = LiveHeapSnapshotFixture.AttachAndFind("FakeFaultedChannel");
            using (dataTarget)
            {
                WcfChannelDomainResult result = await Analyze(heap, address, methodTable, "System.ServiceModel.FakeFaultedChannel");

                result.FaultedChannels.Should().Be(1);
                result.TopFaultedChannels.Should().ContainSingle(s => s.Address == address && s.RemoteAddress == "http://example.com/MyService.svc");
            }
        }

        private static async Task<WcfChannelDomainResult> Analyze(
            Microsoft.Diagnostics.Runtime.ClrHeap heap, ulong address, ulong methodTable, string typeName)
        {
            WcfChannelAnalyzer analyzer = new();
            HeapObjectRef candidate = new(address, SdkTypeRefFactory.FromName(typeName, methodTable), Size: 32, TypeDisplayName: typeName);
            AnalysisContext context = new()
            {
                Observations = NoOpObservationSink.Instance,
                DumpObjectFields = new DumpObjectFieldQuery(heap),
                HeapTypeStatistics = new EmptyHeapTypeStatisticsQuery(),
                HeapObjects = new PrecomputedHeapObjectStream([candidate]),
            };

            await analyzer.AnalyzeAsync(context, CancellationToken.None);
            return (WcfChannelDomainResult)analyzer.LastResult!;
        }

        private sealed class EmptyHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
        {
            public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() =>
                new Dictionary<string, HeapTypeStatistics>(StringComparer.Ordinal);
            public bool HasExactGenerationData => false;
            public bool HasTypeShapeData => false;
            public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
            public long? ExactObjectCount => null;
            public ulong GetTotalIndexedBytes() => 0;
            public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
            public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
        }
    }
}
