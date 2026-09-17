using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

// Test-only fixture type mirroring SqlCommandAnalyzer's expected SqlCommand field shape
// (_activeConnection object reference) — declared under the real provider's namespace so
// SqlCommandAnalyzer.IsCandidateType's namespace-prefix + "Command"-suffix match fires
// against it, without depending on the actual Microsoft.Data.SqlClient package (not referenced by
// this project — R10, hard-need basis: no new external dependency for this).
namespace Microsoft.Data.SqlClient
{
    // Distinct type per scenario (rather than one type reused across tests) so a static field
    // left alive by an earlier test can never be mistaken for the object under test when both
    // scan the live heap by type-name suffix.
    internal sealed class FakeActiveSqlCommand
    {
        private readonly object? _activeConnection;

        public FakeActiveSqlCommand(object? activeConnection)
        {
            _activeConnection = activeConnection;
        }
    }

    internal sealed class FakeDetachedSqlCommand
    {
        private readonly object? _activeConnection;

        public FakeDetachedSqlCommand(object? activeConnection)
        {
            _activeConnection = activeConnection;
        }
    }
}

namespace DumpDetective.Tests.Unit.Analysis
{
    /// <summary>
    /// R10 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md), ported for the Tier-2 retyping
    /// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): exercises
    /// <see cref="SqlCommandAnalyzer"/>'s real field-reading code path against a live self-process
    /// heap snapshot through the real <see cref="DumpObjectFieldQuery"/> — not the dictionary-backed
    /// fake <c>SqlCommandAnalyzerRetypingCharacterizationTests</c> uses — so this is the one place
    /// short of the real-dump test that proves the Tier-2 hatch's <c>ClrElementType.Object</c> field
    /// chase actually works against real ClrMD data, not just against a controlled fixture.
    /// </summary>
    public sealed class SqlCommandAnalyzerLiveHeapTests
    {
        // Kept alive as static fields so the objects remain reachable for the heap snapshot.
        private static Microsoft.Data.SqlClient.FakeActiveSqlCommand? s_activeCommand;
        private static object? s_activeCommandConnection;
        private static Microsoft.Data.SqlClient.FakeDetachedSqlCommand? s_detachedCommand;

        [Fact]
        public async Task AnalyzeAsync_ReadsActiveState_WhenConnectionFieldReferencesLiveObject()
        {
            s_activeCommandConnection = new object();
            s_activeCommand = new Microsoft.Data.SqlClient.FakeActiveSqlCommand(s_activeCommandConnection);

            var (dataTarget, heap, methodTable, address) = LiveHeapSnapshotFixture.AttachAndFind("FakeActiveSqlCommand");
            using (dataTarget)
            {
                SqlCommandDomainResult result = await Analyze(heap, address, methodTable, "Microsoft.Data.SqlClient.FakeActiveSqlCommand");

                result.ActiveCount.Should().Be(1);
                result.DisposedCount.Should().Be(0);
                result.TopActiveCommands.Should().ContainSingle(s => s.Address == address);
            }
        }

        [Fact]
        public async Task AnalyzeAsync_ReadsDisposedState_WhenConnectionFieldIsNull()
        {
            s_detachedCommand = new Microsoft.Data.SqlClient.FakeDetachedSqlCommand(activeConnection: null);

            var (dataTarget, heap, methodTable, address) = LiveHeapSnapshotFixture.AttachAndFind("FakeDetachedSqlCommand");
            using (dataTarget)
            {
                SqlCommandDomainResult result = await Analyze(heap, address, methodTable, "Microsoft.Data.SqlClient.FakeDetachedSqlCommand");

                result.ActiveCount.Should().Be(0);
                result.DisposedCount.Should().Be(1);
                result.TopActiveCommands.Should().BeEmpty();
            }
        }

        private static async Task<SqlCommandDomainResult> Analyze(
            Microsoft.Diagnostics.Runtime.ClrHeap heap, ulong address, ulong methodTable, string typeName)
        {
            SqlCommandAnalyzer analyzer = new();
            HeapObjectRef candidate = new(address, SdkTypeRefFactory.FromName(typeName, methodTable), Size: 32, TypeDisplayName: typeName);
            DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
                Observations = NoOpObservationSink.Instance,
                ArtifactId = new ArtifactId("test"),
                DumpObjectFields = new DumpObjectFieldQuery(heap),
                HeapObjects = new PrecomputedHeapObjectStream([candidate]),
            };

            await analyzer.AnalyzeAsync(context, CancellationToken.None);
            return (SqlCommandDomainResult)analyzer.LastResult!;
        }
    }
}
