using DumpDetective.Sdk.Identity;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectDataTableLifecyclePattern</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// The finalizer-queue half of the original (DataColumn/DataTable via <c>FinalizableObjectDomainResult</c>)
/// already had full observation coverage — <c>type.finalizable-population</c> (per finalizable type,
/// exhaustive) and <c>finalizer.queue-entry</c> (per queued object, exhaustive) — and needed no new
/// emission. Only the DataRow-on-heap half was unobserved; closed by <c>MemoryAnalyzer</c>'s new
/// bounded, known-vocabulary <c>type.known-pattern-population</c> observation (see its own remarks).
/// </remarks>
internal sealed class DataTableLifecycleInsightRule : ISynthesisRule
{
    private const long DataColumnFinalizerThreshold = 100;
    private const long DataRowHeapThreshold = 10_000;

    // Exact, not a substring: DataRowView, DataRowCollection and compiler-generated types that merely mention DataRow (for example
    // Enumerable+<CastIterator>d__<System.Data.DataRow>) are not rows. The same holds for columns and tables. A strongly typed
    // DataTable subclass (a generated "OrdersDataTable") is therefore not counted; the finalizer observations name only the real type.
    private const string DataRowTypeName = "System.Data.DataRow";
    private const string DataColumnTypeName = "System.Data.DataColumn";
    private const string DataTableTypeName = "System.Data.DataTable";

    public string RuleId => "insight.datatable-lifecycle";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.finalizable-population", "finalizer.queue-entry", "type.known-pattern-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        long dataColumnFinalizer = 0;
        long dataTableFinalizer = 0;
        long dataRowHeap = 0;
        var derivedFrom = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;

            switch (observation.ObservationType)
            {
                case "type.finalizable-population":
                {
                    long gen2Count = (long)observation.Measures["gen2.count"].Value;
                    if (typeName == DataColumnTypeName)
                    {
                        dataColumnFinalizer += gen2Count;
                        derivedFrom.Add(observation.Id);
                    }
                    else if (typeName == DataTableTypeName)
                    {
                        dataTableFinalizer += gen2Count;
                        derivedFrom.Add(observation.Id);
                    }
                    break;
                }
                case "finalizer.queue-entry":
                {
                    if (typeName == DataColumnTypeName)
                    {
                        dataColumnFinalizer++;
                        derivedFrom.Add(observation.Id);
                    }
                    else if (typeName == DataTableTypeName)
                    {
                        dataTableFinalizer++;
                        derivedFrom.Add(observation.Id);
                    }
                    break;
                }
                case "type.known-pattern-population":
                {
                    if (typeName == DataRowTypeName)
                    {
                        dataRowHeap += (long)observation.Measures["instance.count"].Value;
                        derivedFrom.Add(observation.Id);
                    }
                    break;
                }
            }
        }

        // Trigger: DataColumn in finalizer queue AND/OR DataRow on heap in large numbers.
        if (dataColumnFinalizer < DataColumnFinalizerThreshold && dataRowHeap < DataRowHeapThreshold)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        var evidenceParts = new List<string>(3);
        if (dataColumnFinalizer > 0) evidenceParts.Add($"DataColumn ×{dataColumnFinalizer:N0} in finalizer queue");
        if (dataTableFinalizer > 0) evidenceParts.Add($"DataTable ×{dataTableFinalizer:N0} in finalizer queue");
        if (dataRowHeap > 0) evidenceParts.Add($"DataRow ×{dataRowHeap:N0} on heap");

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::datatable-lifecycle",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(dataColumnFinalizer),
                    Reference = dataColumnFinalizer >= DataColumnFinalizerThreshold
                        ? ClaimParts.Budget(ClaimParts.Count(DataColumnFinalizerThreshold), "DataColumn finalizer threshold")
                        : ClaimParts.Budget(ClaimParts.Count(DataRowHeapThreshold), "DataRow heap threshold"),
                    Qualifiers = new AssertionQualifiers
                    {
                        Location = "DataColumn and DataRow objects",
                        Mechanism = "DataColumn has a finalizer and is released late without Dispose",
                    },
                },
                Support = [new EvidenceStatement(derivedFrom, string.Join("; ", evidenceParts) + ".")],
                Counter = Counter.None("A data-heavy service holds DataTables on purpose; the counts alone do not show they should have been disposed."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <DataTable address>",
                        Test = "On a sampled DataTable: if its root is a request frame, the tables are short-lived and are released with the request.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the DataColumn and DataRow counts do not grow, they are a stable working set.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "DataTable lifecycle pattern: large DataRow/DataColumn accumulation",
                Narrative = string.Join("; ", evidenceParts) + ". " +
                            "DataColumn objects have finalizers and do not release promptly without explicit Dispose().",
                Recommendation = "Call DataTable.Dispose() and DataSet.Dispose() when tables are no longer needed. " +
                                 "Avoid sharing DataTable instances across request scopes. " +
                                 "Consider replacing DataTable/DataSet with strongly typed models to eliminate finalizer overhead.",
                Tags = ["datatable", "datarow", "finalizer", "memory-leak", "dispose"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
