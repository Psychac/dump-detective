using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="SqlTransactionAnalyzer"/> reproduces the pre-retyping analyzer's Active/Disposed/
/// Other three-way classification, connection-address capture, and per-type aggregation against a
/// fake, dictionary-backed <see cref="IDumpObjectFieldQuery"/>. Unlike <c>SqlCommandAnalyzer</c>,
/// this analyzer surfaces its "Other" (unreadable-state) bucket in the domain result, so that path
/// gets its own coverage here.
/// </summary>
public sealed class SqlTransactionAnalyzerRetypingCharacterizationTests
{
    private const string TransactionType = "Microsoft.Data.SqlClient.SqlTransaction";

    [Fact]
    public async Task AnalyzeAsync_ActiveTransaction_CapturesConnectionAddress()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_connection", 0x9000);

        SqlTransactionDomainResult result = await Analyze(fields, Candidate(0x1000, TransactionType));

        result.ActiveCount.Should().Be(1);
        result.TopActiveTransactions.Should().ContainSingle();
        result.TopActiveTransactions[0].ConnectionAddress.Should().Be(0x9000UL);
    }

    [Fact]
    public async Task AnalyzeAsync_NoConnectionFieldDeclaredAtAll_CountsAsOther()
    {
        // None of "_connection"/"_internalConnection"/"Connection" set on this address at all --
        // matches the pre-retyping analyzer's own stateVal == -1 -> "Other" classification.
        var fields = new FakeDumpObjectFieldQuery();

        SqlTransactionDomainResult result = await Analyze(fields, Candidate(0x1000, TransactionType));

        result.ActiveCount.Should().Be(0);
        result.DisposedCount.Should().Be(0);
        result.OtherCount.Should().Be(1);
        result.ByType[0].OtherCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_DisposedTransaction_ConnectionFieldExistsButNull()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetNull(0x1000, "_connection");

        SqlTransactionDomainResult result = await Analyze(fields, Candidate(0x1000, TransactionType));

        result.DisposedCount.Should().Be(1);
        result.ActiveCount.Should().Be(0);
        result.OtherCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        SqlTransactionDomainResult result = await Analyze(new FakeDumpObjectFieldQuery());

        result.TransactionsFound.Should().BeFalse();
        result.TotalTransactions.Should().Be(0);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<SqlTransactionDomainResult> Analyze(FakeDumpObjectFieldQuery fields, params HeapObjectRef[] candidates)
    {
        SqlTransactionAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapObjects = new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (SqlTransactionDomainResult)analyzer.LastResult!;
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
