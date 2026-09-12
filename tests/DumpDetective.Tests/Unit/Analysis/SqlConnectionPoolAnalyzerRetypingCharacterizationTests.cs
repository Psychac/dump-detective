using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-2 pilot (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="SqlConnectionPoolAnalyzer"/> — retyped onto <see cref="IDumpObjectFieldQuery"/>
/// (<c>dump.object-fields</c>) — reproduces the pre-retyping analyzer's exact field-chasing behavior
/// (int/nested-object-reference/string reads, defensive shape checks, missing-field fallbacks)
/// against a fake, dictionary-backed <see cref="IDumpObjectFieldQuery"/> rather than a live or
/// snapshotted heap. Unlike every prior batch's segment/root/handle/thread data, a field *value* has
/// no live-ClrMD-only shape to fabricate — this is the first Tier-1/Tier-2 surface simple enough to
/// fake directly, so this test needs no <c>LiveHeapSnapshotFixture</c>/<c>DataTarget</c> at all.
///
/// Candidates are handed to the analyzer via <see cref="AnalysisContext.HeapObjects"/>
/// (<c>heap.objects</c>) — corrected from the pilot's original design, which overloaded
/// <c>AnalyzerOptions</c> for this.
/// </summary>
public sealed class SqlConnectionPoolAnalyzerRetypingCharacterizationTests
{
    private const ulong PoolAddress = 0x1000;
    private const ulong OptionsAddress = 0x2000;
    private const ulong GroupAddress = 0x3000;
    private const ulong ConnOptionsAddress = 0x4000;
    private const string PoolTypeName = "Microsoft.Data.ProviderBase.DbConnectionPool";

    [Fact]
    public async Task AnalyzeAsync_FullFieldChain_ProducesExactSnapshot()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(PoolAddress, "_totalObjects", 40);
        fields.SetObjectReference(PoolAddress, "_connectionPoolGroupOptions", OptionsAddress);
        fields.SetInt32(OptionsAddress, "_maxPoolSize", 100);
        fields.SetInt32(OptionsAddress, "_minPoolSize", 5);
        fields.SetObjectReference(PoolAddress, "_connectionPoolGroup", GroupAddress);
        fields.SetObjectReference(GroupAddress, "_connectionOptions", ConnOptionsAddress);
        fields.SetString(ConnOptionsAddress, "_usersConnectionString", "Server=myserver;Database=mydb;User Id=sa;Password=hunter2;");

        SqlConnectionPoolDomainResult result = await Analyze(fields, [PoolCandidate(PoolAddress)]);

        result.PoolsFound.Should().BeTrue();
        result.TotalPools.Should().Be(1);
        SqlConnectionPoolSnapshot pool = result.Pools[0];
        pool.Address.Should().Be(PoolAddress);
        pool.CurrentSize.Should().Be(40);
        pool.MaxPoolSize.Should().Be(100);
        pool.MinPoolSize.Should().Be(5);
        pool.AnonymisedConnectionString.Should().NotBeNull();
        pool.AnonymisedConnectionString.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task AnalyzeAsync_TotalObjectsFieldWrongShape_SkipsPool()
    {
        // Defensive shape check preserved from the pre-retyping analyzer: a "_totalObjects" that
        // isn't Int32 means a SqlClient internal layout this analyzer doesn't understand, not a
        // value to report.
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(PoolAddress, "_totalObjects", "not-an-int");

        SqlConnectionPoolDomainResult result = await Analyze(fields, [PoolCandidate(PoolAddress)]);

        result.PoolsFound.Should().BeFalse();
        result.TotalPools.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingSizeLimitFields_FallsBackToNegativeOne()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(PoolAddress, "_totalObjects", 12);
        // No "_connectionPoolGroupOptions" field at all.

        SqlConnectionPoolDomainResult result = await Analyze(fields, [PoolCandidate(PoolAddress)]);

        result.Pools[0].MaxPoolSize.Should().Be(-1);
        result.Pools[0].MinPoolSize.Should().Be(-1);
        result.Pools[0].AnonymisedConnectionString.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        SqlConnectionPoolDomainResult result = await Analyze(new FakeDumpObjectFieldQuery(), []);

        result.PoolsFound.Should().BeFalse();
        result.TotalPools.Should().Be(0);
        result.Pools.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_NoHeapObjectStream_ReturnsEmptyResult()
    {
        // context.HeapObjects itself null (no scan-participant pass ever ran) must degrade the same
        // way as "ran but found nothing" -- both are "no candidates," not an error.
        SqlConnectionPoolAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = new FakeDumpObjectFieldQuery(),
            HeapObjects = null,
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        var result = (SqlConnectionPoolDomainResult)analyzer.LastResult!;

        result.PoolsFound.Should().BeFalse();
    }

    private static HeapObjectRef PoolCandidate(ulong address) => new(
        address, SdkTypeRefFactory.FromName(PoolTypeName, null), Size: 0, TypeDisplayName: PoolTypeName);

    private static async Task<SqlConnectionPoolDomainResult> Analyze(
        FakeDumpObjectFieldQuery fields, IReadOnlyList<HeapObjectRef> candidates)
    {
        SqlConnectionPoolAnalyzer analyzer = new();
        AnalysisContext context = new()
        {
            Observations = NoOpObservationSink.Instance,
            DumpObjectFields = fields,
            HeapObjects = new PrecomputedHeapObjectStream(candidates),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (SqlConnectionPoolDomainResult)analyzer.LastResult!;
    }

    /// <summary>Dictionary-backed fake — (address, fieldName) to <see cref="FieldValue"/>. A missing
    /// entry means "field not found," matching <see cref="IDumpObjectFieldQuery.TryReadField"/>'s own
    /// contract for an object whose type doesn't declare that field.</summary>
    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);
        public void SetString(ulong address, string fieldName, string value) => _values[(address, fieldName)] = FieldValue.ForString(value);
        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this test file's scenarios
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
}
