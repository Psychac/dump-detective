using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

// Test-only fixture type mirroring SqlCommandAnalyzer's expected SqlCommand field shape
// (_activeConnection object reference) — declared under the real provider's namespace so
// SqlCommandAnalyzer.IsCandidateType's namespace-prefix + "Command"-suffix match fires against
// it, without depending on the actual Microsoft.Data.SqlClient package (not referenced by this
// project — R10, hard-need basis: no new external dependency for this).
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
    /// R10 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md): exercises
    /// <see cref="SqlCommandAnalyzer"/>'s real ClrMD field-reading code
    /// (<c>TrySample</c>) against a live self-process heap snapshot instead of only via the
    /// real-dump discrepancy test.
    /// </summary>
    public sealed class SqlCommandAnalyzerLiveHeapTests
    {
        // Kept alive as static fields so the objects remain reachable for the heap snapshot.
        private static Microsoft.Data.SqlClient.FakeActiveSqlCommand? s_activeCommand;
        private static object? s_activeCommandConnection;
        private static Microsoft.Data.SqlClient.FakeDetachedSqlCommand? s_detachedCommand;

        [Fact]
        public void TrySample_ReadsActiveState_WhenConnectionFieldReferencesLiveObject()
        {
            s_activeCommandConnection = new object();
            s_activeCommand = new Microsoft.Data.SqlClient.FakeActiveSqlCommand(s_activeCommandConnection);

            var (dataTarget, heap, methodTable, address) = LiveHeapSnapshotFixture.AttachAndFind("FakeActiveSqlCommand");
            using (dataTarget)
            {
                var analyzer = new SqlCommandAnalyzer();
                var entry = new HeapEntry(address, methodTable, 32);

                SqlCommandSnapshot? snap = ((ITypedResourceInstanceSampler<SqlCommandSnapshot>)analyzer)
                    .TrySample(heap, in entry, "Microsoft.Data.SqlClient.FakeActiveSqlCommand");

                snap.Should().NotBeNull();
                snap!.StateLabel.Should().Be("Active");
                snap.StateValue.Should().Be(1);
            }
        }

        [Fact]
        public void TrySample_ReadsDisposedState_WhenConnectionFieldIsNull()
        {
            s_detachedCommand = new Microsoft.Data.SqlClient.FakeDetachedSqlCommand(activeConnection: null);

            var (dataTarget, heap, methodTable, address) = LiveHeapSnapshotFixture.AttachAndFind("FakeDetachedSqlCommand");
            using (dataTarget)
            {
                var analyzer = new SqlCommandAnalyzer();
                var entry = new HeapEntry(address, methodTable, 32);

                SqlCommandSnapshot? snap = ((ITypedResourceInstanceSampler<SqlCommandSnapshot>)analyzer)
                    .TrySample(heap, in entry, "Microsoft.Data.SqlClient.FakeDetachedSqlCommand");

                snap.Should().NotBeNull();
                snap!.StateLabel.Should().Be("Disposed");
                snap.StateValue.Should().Be(0);
            }
        }
    }
}
