using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

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
    /// R10 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md), ported for the Tier-2 retyping
    /// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): exercises
    /// <see cref="DbConnectionAnalyzer"/>'s real field-reading code path against a live
    /// self-process heap snapshot through the real <see cref="DumpObjectFieldQuery"/>. Uses an
    /// Open connection, not the pre-retyping test's Closed one -- AnonymisedConnectionString is
    /// only ever computed for Open connections now (Closed/Broken/Other never surface a
    /// DbConnectionSnapshot at all, so it was never observable there), and the Closed/Open choice
    /// was incidental to what this test actually verifies (real ClrMD anonymisation).
    /// </summary>
    public sealed class DbConnectionAnalyzerLiveHeapTests
    {
        // Kept alive as a static field so the object remains reachable for the heap snapshot.
        private static Microsoft.Data.SqlClient.FakeSqlConnection? s_connection;

        [Fact]
        public async Task AnalyzeAsync_ReadsOpenStateAndAnonymisedConnectionString_FromRealHeapObject()
        {
            s_connection = new Microsoft.Data.SqlClient.FakeSqlConnection(
                connectionState: 1, // ConnectionState.Open
                connectionString: "Server=testserver;Database=testdb;User Id=sa;Password=hunter2;");

            var (dataTarget, heap, methodTable, address) = LiveHeapSnapshotFixture.AttachAndFind("FakeSqlConnection");
            using (dataTarget)
            {
                DbConnectionDomainResult result = await Analyze(heap, address, methodTable, "Microsoft.Data.SqlClient.FakeSqlConnection");

                result.OpenConnections.Should().Be(1);
                DbConnectionSnapshot snap = result.TopOpenConnections.Should().ContainSingle().Subject;
                snap.StateLabel.Should().Be("Open");
                snap.StateValue.Should().Be(1);
                snap.AnonymisedConnectionString.Should().NotBeNull();
                snap.AnonymisedConnectionString.Should().Contain("Password=***");
                snap.AnonymisedConnectionString.Should().NotContain("hunter2");
            }
        }

        private static async Task<DbConnectionDomainResult> Analyze(
            Microsoft.Diagnostics.Runtime.ClrHeap heap, ulong address, ulong methodTable, string typeName)
        {
            DbConnectionAnalyzer analyzer = new();
            HeapObjectRef candidate = new(address, SdkTypeRefFactory.FromName(typeName, methodTable), Size: 64, TypeDisplayName: typeName);
            AnalysisContext context = new()
            {
                Observations = NoOpObservationSink.Instance,
                DumpObjectFields = new DumpObjectFieldQuery(heap),
                HeapSegments = new EmptyHeapSegmentQuery(),
                HeapRootPaths = new EmptyHeapRootPathQuery(),
                HeapObjects = new PrecomputedHeapObjectStream([candidate]),
            };

            await analyzer.AnalyzeAsync(context, CancellationToken.None);
            return (DbConnectionDomainResult)analyzer.LastResult!;
        }

        private sealed class EmptyHeapSegmentQuery : IHeapSegmentQuery
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

        private sealed class EmptyHeapRootPathQuery : IHeapRootPathQuery
        {
            public bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated)
            {
                formattedPath = null;
                searchTruncated = false;
                return false;
            }
        }
    }
}
