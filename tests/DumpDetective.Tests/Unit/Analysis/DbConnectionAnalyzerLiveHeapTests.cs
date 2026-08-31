using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

// Test-only fixture type mirroring DbConnectionAnalyzer's expected SqlConnection field shape
// (_connectionState int, _connectionString string) — declared under the real provider's
// namespace so DbConnectionAnalyzer.IsCandidateType's namespace-prefix + "Connection"-suffix
// match fires against it, without depending on the actual Microsoft.Data.SqlClient package
// (not referenced by this project — R10, hard-need basis: no new external dependency for this).
namespace Microsoft.Data.SqlClient
{
    internal sealed class FakeSqlConnection
    {
        private readonly int _connectionState;
        private readonly string? _connectionString;

        public FakeSqlConnection(int connectionState, string? connectionString)
        {
            _connectionState = connectionState;
            _connectionString = connectionString;
        }
    }
}

namespace DumpDetective.Tests.Unit.Analysis
{
    /// <summary>
    /// R10 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md): exercises
    /// <see cref="DbConnectionAnalyzer"/>'s real ClrMD field-reading code
    /// (<c>TrySample</c>) against a live self-process heap snapshot instead of only via the
    /// real-dump discrepancy test.
    /// </summary>
    public sealed class DbConnectionAnalyzerLiveHeapTests
    {
        // Kept alive as a static field so the object remains reachable for the heap snapshot.
        private static Microsoft.Data.SqlClient.FakeSqlConnection? s_connection;

        [Fact]
        public void TrySample_ReadsClosedStateAndAnonymisedConnectionString_FromRealHeapObject()
        {
            s_connection = new Microsoft.Data.SqlClient.FakeSqlConnection(
                connectionState: 0, // ConnectionState.Closed
                connectionString: "Server=testserver;Database=testdb;User Id=sa;Password=hunter2;");

            var (dataTarget, heap, methodTable, address) = LiveHeapSnapshotFixture.AttachAndFind("FakeSqlConnection");
            using (dataTarget)
            {
                var analyzer = new DbConnectionAnalyzer();
                var entry = new HeapEntry(address, methodTable, 64);

                DbConnectionSnapshot? snap = ((ITypedResourceInstanceSampler<DbConnectionSnapshot>)analyzer)
                    .TrySample(heap, in entry, "Microsoft.Data.SqlClient.FakeSqlConnection");

                snap.Should().NotBeNull();
                snap!.StateLabel.Should().Be("Closed");
                snap.StateValue.Should().Be(0);
                snap.AnonymisedConnectionString.Should().NotBeNull();
                snap.AnonymisedConnectionString.Should().Contain("Password=***");
                snap.AnonymisedConnectionString.Should().NotContain("hunter2");
            }
        }
    }
}
