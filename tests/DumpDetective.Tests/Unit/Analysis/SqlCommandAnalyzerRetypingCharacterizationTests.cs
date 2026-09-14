using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-2 wave's second/third real consumers
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): proves
/// <see cref="SqlCommandAnalyzer"/> reproduces the pre-retyping analyzer's per-type
/// active/disposed/total aggregation and sort order against a fake, dictionary-backed
/// <see cref="IDumpObjectFieldQuery"/> — the multi-candidate aggregation logic the live-heap test
/// (single candidate) and the real-dump test (whatever the reference dump happens to contain) can't
/// exercise deterministically on their own.
/// </summary>
public sealed class SqlCommandAnalyzerRetypingCharacterizationTests
{
    private const string CommandTypeA = "Microsoft.Data.SqlClient.SqlCommand";
    private const string CommandTypeB = "Npgsql.NpgsqlCommand";

    [Fact]
    public async Task AnalyzeAsync_MixedActiveAndDisposedAcrossTwoTypes_AggregatesPerType()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_activeConnection", 0x9000); // Active
        fields.SetNull(0x1001, "_activeConnection");                    // Disposed (field exists, null)
        fields.SetObjectReference(0x2000, "Connection", 0x9001);        // Active, second type, third field name tried

        SqlCommandDomainResult result = await Analyze(fields,
            Candidate(0x1000, CommandTypeA), Candidate(0x1001, CommandTypeA), Candidate(0x2000, CommandTypeB));

        result.CommandsFound.Should().BeTrue();
        result.TotalCommands.Should().Be(3);
        result.ActiveCount.Should().Be(2);
        result.DisposedCount.Should().Be(1);
        result.TopActiveCommands.Should().HaveCount(2);

        result.ByType.Should().HaveCount(2);
        SqlCommandTypeSummary typeASummary = result.ByType.Single(t => t.TypeName == CommandTypeA);
        typeASummary.TotalCount.Should().Be(2);
        typeASummary.ActiveCount.Should().Be(1);
        typeASummary.DisposedCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_ByType_SortedByTotalCountDescending()
    {
        var fields = new FakeDumpObjectFieldQuery();

        SqlCommandDomainResult result = await Analyze(fields,
            Candidate(0x1, CommandTypeB), Candidate(0x2, CommandTypeA), Candidate(0x3, CommandTypeA), Candidate(0x4, CommandTypeA));

        result.ByType[0].TypeName.Should().Be(CommandTypeA);
        result.ByType[0].TotalCount.Should().Be(3);
        result.ByType[1].TypeName.Should().Be(CommandTypeB);
    }

    [Fact]
    public async Task AnalyzeAsync_ConnectionFieldNamesTriedInOrder_FirstDeclaredFieldWins()
    {
        // "_activeConnection" not declared on this candidate's type at all -- falls through to
        // "_connection", matching the pre-retyping analyzer's own field-name fallback order.
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_connection", 0x9000);

        SqlCommandDomainResult result = await Analyze(fields, Candidate(0x1000, CommandTypeA));

        result.ActiveCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        SqlCommandDomainResult result = await Analyze(new FakeDumpObjectFieldQuery());

        result.CommandsFound.Should().BeFalse();
        result.TotalCommands.Should().Be(0);
        result.ByType.Should().BeEmpty();
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<SqlCommandDomainResult> Analyze(FakeDumpObjectFieldQuery fields, params HeapObjectRef[] candidates)
    {
        SqlCommandAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapObjects = new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (SqlCommandDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool HasDelegateField(ulong address) => false;

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();

        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);
        public void SetNull(ulong address, string fieldName) => _values[(address, fieldName)] = FieldValue.Null;

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this test file's scenarios
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
}
