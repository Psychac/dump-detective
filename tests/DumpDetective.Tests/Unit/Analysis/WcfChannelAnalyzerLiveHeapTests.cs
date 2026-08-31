using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

// Test-only fixture type matching WcfChannelAnalyzer.IsCandidateType's namespace-prefix +
// "Channel"-contains-token match, without depending on the actual System.ServiceModel package
// (not referenced by this project — R10, hard-need basis: no new external dependency for this).
namespace System.ServiceModel
{
    internal sealed class FakeChannel
    {
    }
}

namespace DumpDetective.Tests.Unit.Analysis
{
    /// <summary>
    /// P3-3 (docs/analysis/phase1/wcf-channel-analyzer-audit.md): confirms — against a real
    /// <see cref="ClrHeap"/> rather than reflection-seeded state — that
    /// <c>MergePartial</c>'s new-key-from-worker branch is unreachable in production. Every
    /// worker calls the real <c>BeforeHeapIndexScan</c> against the same heap/cache that the
    /// primary instance used, so their pre-seeded <c>_typeStats</c> key sets are always identical
    /// before any entry is ever scanned.
    /// </summary>
    public sealed class WcfChannelAnalyzerLiveHeapTests
    {
        private static System.ServiceModel.FakeChannel? s_channel;

        [Fact]
        public void MergePartial_NeverNeedsNewKeyBranch_WhenPreSeededFromSameRealHeapScan()
        {
            s_channel = new System.ServiceModel.FakeChannel();

            DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using (dataTarget)
            {
                ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

                var context = new AnalysisContext
                {
                    Runtime = runtime,
                    Cache = new HeapAnalysisCache(),
                };

                WcfChannelAnalyzer primary = new();
                var worker = (WcfChannelAnalyzer)((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

                primary.BeforeHeapIndexScan(context);
                worker.BeforeHeapIndexScan(context);

                var primaryKeys = GetTypeStats(primary).Keys.ToHashSet();
                var workerKeys = GetTypeStats(worker).Keys.ToHashSet();

                primaryKeys.Should().NotBeEmpty();
                GetTypeStats(primary).Values.Should().Contain(v => v.Name.Contains("FakeChannel"));

                // Realistic pre-seeding: both instances discovered candidates from the same
                // heap/cache, so their key sets are identical before MergePartial ever runs.
                workerKeys.Should().BeEquivalentTo(primaryKeys);

                ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

                // The merge introduced no new keys — the "add key from worker" branch was not exercised.
                GetTypeStats(primary).Keys.Should().BeEquivalentTo(primaryKeys);
            }
        }

        private static Dictionary<ulong, (string Name, int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, int InvalidState, ulong Bytes)>
            GetTypeStats(WcfChannelAnalyzer analyzer) =>
            (Dictionary<ulong, (string, int, int, int, int, int, int, int, int, ulong)>)typeof(WcfChannelAnalyzer)
                .GetField("_typeStats", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(analyzer)!;
    }
}
