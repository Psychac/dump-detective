using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Types
{
    /// <summary>
    /// Phase-2 analyzer covering §20.1 (boxed value type inventory) and §20.2
    /// (value type shape issues: struct padding waste, oversized value types).
    ///
    /// Boxing detection: scans <c>IHeapTypeStatisticsQuery</c>'s per-type aggregates. Value types
    /// (<see cref="HeapTypeStatistics.IsValueType"/> true) that appear in the heap index are by
    /// definition boxed instances. Enums are classified separately.
    ///
    /// Struct padding: for each value type, compares <see cref="HeapTypeStatistics.StaticSize"/>
    /// against <see cref="HeapTypeStatistics.DeclaredFieldByteSize"/> to surface padding waste.
    /// </summary>
    /// <remarks>
    /// Tier-1 correction (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// originally grep-classified as Tier-1+Tier-2, corrected on inspection — same story as
    /// <c>ObjectShapeAnalyzer</c> before it. This analyzer never reads a field *value* off a live
    /// object, only per-type structural metadata (<c>IsEnum</c>/<c>StaticSize</c>/<c>BaseType.Name</c>/
    /// interface set/declared field sizes), all coarse facts resolved once per distinct type — exactly
    /// the shape <see cref="IHeapTypeStatisticsQuery"/> already exists for. Extended that interface
    /// with <c>IsEnum</c>/<c>StaticSize</c>/<c>BaseTypeName</c>/<c>HasIEquatableInterface</c>/
    /// <c>DeclaredFieldByteSize</c> rather than adding a new capability. Inherits the same
    /// method-table-name-collision gap every other consumer of <c>GetTypeStatistics()</c> already
    /// documents (two distinct method tables resolving to the same display name collapse to one
    /// dictionary entry) — not new here, just inherited from the shared surface.
    /// </remarks>
    [AnalyzerModule(key: "boxing", displayName: "Boxing Analysis", order: 400, tags: ["types", "perf"])]
    [RequiresCapability(CapabilityVocabulary.HeapTypes)]
    [OptionalCapability(CapabilityVocabulary.TraceAllocSamples)]
    public sealed class BoxingAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
    {
        public string Name => "Boxing Analysis";
        public string Category => "Memory";

        public AnalyzerDomainResult? LastResult
        {
            get => _lastResult;
            // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
            // longer exists) so every LastResult consumer — pipeline or direct caller — sees
            // them populated the same way.
            private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
        }
        private AnalyzerDomainResult? _lastResult;

        public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IHeapTypeStatisticsQuery statsQuery = context.HeapTypeStatistics
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");
            BoxingAnalysisOptions options = context.AnalyzerOptions as BoxingAnalysisOptions ?? new BoxingAnalysisOptions();

            var result = Analyze(statsQuery, options, context.Progress, cancellationToken);
            LastResult = result;

            await EmitObservationsAsync(context, result, cancellationToken);
        }

        /// <summary>
        /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
        /// <c>type.boxing</c> observation per <see cref="BoxingDomainResult.TopBoxedTypes"/> entry —
        /// bounded to value types actually found boxed on the heap, a small fraction of the whole
        /// type population (unlike <c>ObjectShapeAnalyzer</c>'s per-type lists, which cover every
        /// type). <c>IsEnum</c>/<c>HasRefFields</c>/<c>HasIEquatable</c> are structural facts, not
        /// judgments, so they're fine as raw 0/1 measures.
        /// </summary>
        private static async ValueTask EmitObservationsAsync(AnalysisContext context, BoxingDomainResult result, CancellationToken cancellationToken)
        {
            var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };

            foreach (BoxedTypeEntry entry in result.TopBoxedTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(entry.ValueTypeName);

                await context.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "type.boxing",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["instance.count"] = new Measure(entry.BoxCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["shallow.bytes"] = new Measure(entry.TotalBoxBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["gen0.count"] = new Measure(entry.Gen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["gen2.count"] = new Measure(entry.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["gen2.fraction"] = new Measure(entry.Gen2Fraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                        ["is-enum"] = new Measure(entry.IsEnum ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["has-reference-fields"] = new Measure(entry.HasReferenceFields ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["has-iequatable"] = new Measure(entry.HasIEquatable ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = context.ArtifactId!.Value,
                        AnalyzerKey = "Boxing Analysis",
                        CapabilitiesUsed = capabilitiesUsed,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }
        }

        private static BoxingDomainResult Analyze(
            IHeapTypeStatisticsQuery statsQuery,
            BoxingAnalysisOptions options,
            IProgress<AnalyzerProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            if (!statsQuery.HasExactGenerationData)
                return Empty();

            IReadOnlyDictionary<string, HeapTypeStatistics> stats = statsQuery.GetTypeStatistics();

            // ── Boxing inventory ──────────────────────────────────────────────
            var boxedByTypeName = new Dictionary<string, (int Count, ulong Bytes, bool IsEnum, bool HasRefFields, long Gen0Count, long Gen2Count, bool HasIEquatable)>(
                StringComparer.Ordinal);

            long totalBoxedObjects = 0;
            ulong totalBoxedBytes = 0;
            long boxedEnumCount = 0;
            ulong boxedEnumBytes = 0;
            long nullableCount = 0;
            ulong nullableBytes = 0;
            long oversizedCount = 0;
            long totalGen2BoxedCount = 0;

            // Struct padding candidates: collect during the same pass
            var paddingCandidates = new List<(string TypeName, int StructSize, int FieldBytes, int Count, int WastedBytes)>(64);

            // Oversized value type candidates: aggregated by type name during the same pass
            var oversizedByTypeName = new Dictionary<string, (int StaticSize, int Count)>(StringComparer.Ordinal);

            int typesScanned = 0;
            int totalTypes = stats.Count;
            const int ProgressReportInterval = 128;

            foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
            {
                // Mid-loop cancellation check and progress reporting (every 128 types) -- unlike the
                // pre-retyping analyzer, this loop does no per-type ClrMD lookup of its own (already
                // resolved once by IHeapTypeStatisticsQuery), so its cost scales even more cheaply
                // with distinct type count than before.
                if ((typesScanned % ProgressReportInterval) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new(typesScanned, "scanning boxed type metadata", $"{typesScanned}/{totalTypes} types"));
                }
                typesScanned++;

                HeapTypeStatistics e = kv.Value;

                // ── Boxing detection ──────────────────────────────────────────
                // Value types can only appear on the managed heap as boxed instances.
                // In ClrMD, IsValueType is true for both unboxed layout and heap-resident boxed forms.
                // Alternatively, the spec's condition: BaseTypeName is "System.ValueType"/"System.Enum"
                // also catches the same set reliably.
                bool isBoxed = e.IsValueType
                    || string.Equals(e.BaseTypeName, "System.ValueType", StringComparison.Ordinal)
                    || string.Equals(e.BaseTypeName, "System.Enum", StringComparison.Ordinal);

                if (!isBoxed) continue;

                string typeName = kv.Key;
                int count = SafeInstanceCount(e.InstanceCount);
                ulong bytes = e.TotalSize;
                bool isEnum = e.IsEnum;
                bool isNullable = IsNullableTypeName(typeName);

                totalBoxedObjects += count;
                totalBoxedBytes += bytes;

                if (isEnum)
                {
                    boxedEnumCount += count;
                    boxedEnumBytes += bytes;
                }

                if (isNullable)
                {
                    nullableCount += count;
                    nullableBytes += bytes;
                }

                // Oversized value types — StaticSize reflects the value layout size
                if (e.StaticSize > options.OversizedThresholdBytes)
                {
                    oversizedCount += count;
                    if (oversizedByTypeName.TryGetValue(typeName, out var existingOversized))
                        oversizedByTypeName[typeName] = (existingOversized.StaticSize, existingOversized.Count + count);
                    else
                        oversizedByTypeName[typeName] = (e.StaticSize, count);
                }

                bool hasRefFields = e.ReferenceFields > 0;

                // Per-MT GC generation distribution (already captured by the Phase 1 index) —
                // distinguishes transient (Gen0, likely short-lived boxing churn) from retained
                // (Gen2, survived collections — the boxing that actually matters for a leak/GC-pressure diagnosis).
                totalGen2BoxedCount += e.Gen2Count;

                bool hasIEquatable = e.HasIEquatableInterface;

                if (boxedByTypeName.TryGetValue(typeName, out var existing))
                    boxedByTypeName[typeName] = (existing.Count + count, existing.Bytes + bytes, isEnum, existing.HasRefFields || hasRefFields,
                        existing.Gen0Count + e.Gen0Count, existing.Gen2Count + e.Gen2Count, existing.HasIEquatable || hasIEquatable);
                else
                    boxedByTypeName[typeName] = (count, bytes, isEnum, hasRefFields, e.Gen0Count, e.Gen2Count, hasIEquatable);

                // ── Struct padding ────────────────────────────────────────────
                // Compute per-type only (not per-instance). Skip enums and very small structs.
                if (!isEnum && e.StaticSize > 4)
                {
                    int fieldBytes = e.DeclaredFieldByteSize;
                    int structSize = e.StaticSize;
                    int wasted = ComputePaddingWaste(structSize, fieldBytes);
                    if (wasted > 0)
                        paddingCandidates.Add((typeName, structSize, fieldBytes, count, wasted));
                }
            }

            // ── Build top boxed types ─────────────────────────────────────────
            var typeList = new List<(string Name, int Count, ulong Bytes, bool IsEnum, bool HasRefFields, long Gen0Count, long Gen2Count, bool HasIEquatable)>(boxedByTypeName.Count);
            foreach (KeyValuePair<string, (int Count, ulong Bytes, bool IsEnum, bool HasRefFields, long Gen0Count, long Gen2Count, bool HasIEquatable)> kv in boxedByTypeName)
                typeList.Add((kv.Key, kv.Value.Count, kv.Value.Bytes, kv.Value.IsEnum, kv.Value.HasRefFields, kv.Value.Gen0Count, kv.Value.Gen2Count, kv.Value.HasIEquatable));

            typeList.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

            var boxedTypes = new List<BoxedTypeEntry>(typeList.Count);
            foreach (var t in typeList)
            {
                double gen2Fraction = t.Count > 0 ? (double)t.Gen2Count / t.Count : 0.0;
                boxedTypes.Add(new BoxedTypeEntry(t.Name, t.Count, t.Bytes, t.IsEnum, t.HasRefFields, t.Gen0Count, t.Gen2Count, gen2Fraction, t.HasIEquatable));
            }

            // ── Build padding waste types, ranked by waste ──────────────────────
            paddingCandidates.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));

            var paddingWaste = new List<StructPaddingEntry>(paddingCandidates.Count);
            foreach (var c in paddingCandidates)
            {
                double ratio = c.StructSize > 0 ? (double)c.WastedBytes / c.StructSize : 0.0;
                paddingWaste.Add(new StructPaddingEntry(
                    TypeName: c.TypeName,
                    TotalFieldBytes: c.FieldBytes,
                    StructSize: c.StructSize,
                    WastedPaddingBytes: c.WastedBytes,
                    WasteRatio: ratio));
            }

            ulong aggregatePaddingWaste = 0;
            foreach (var c in paddingCandidates)
            {
                aggregatePaddingWaste += (ulong)(c.WastedBytes * c.Count);
            }

            // ── Build oversized types, ranked by instance count ─────────────────
            var oversizedList = new List<OversizedTypeEntry>(oversizedByTypeName.Count);
            foreach (KeyValuePair<string, (int StaticSize, int Count)> kv2 in oversizedByTypeName)
                oversizedList.Add(new OversizedTypeEntry(kv2.Key, kv2.Value.StaticSize, kv2.Value.Count));

            oversizedList.Sort(static (a, b) => b.Count.CompareTo(a.Count));

            double avgBoxedInstanceBytes = totalBoxedObjects > 0 ? (double)totalBoxedBytes / totalBoxedObjects : 0.0;

            return new BoxingDomainResult(
                TotalBoxedObjects: totalBoxedObjects,
                TotalBoxedBytes: totalBoxedBytes,
                TopBoxedTypes: boxedTypes,
                BoxedEnumCount: boxedEnumCount,
                BoxedEnumBytes: boxedEnumBytes,
                NullableBoxedCount: nullableCount,
                NullableBoxedBytes: nullableBytes,
                OversizedValueTypeInstanceCount: oversizedCount,
                TopOversizedTypes: oversizedList,
                TopPaddingWasteTypes: paddingWaste,
                AggregatePaddingWasteBytes: aggregatePaddingWaste,
                AvgBoxedInstanceBytes: avgBoxedInstanceBytes,
                TotalGen2BoxedCount: totalGen2BoxedCount);
        }

        private static BoxingDomainResult Empty() => new(0, 0, [], 0, 0, 0, 0, 0, [], [], 0, 0.0);

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Clamps a <c>long</c> instance count to <c>int.MaxValue</c> for display/table purposes
        /// without overflowing (heap type aggregates use <c>long</c> counters for very large heaps).
        /// </summary>
        internal static int SafeInstanceCount(long count) => (int)Math.Min(count, int.MaxValue);

        /// <summary>Returns true if <paramref name="typeName"/> is a boxed <c>System.Nullable&lt;T&gt;</c>.</summary>
        internal static bool IsNullableTypeName(string typeName) =>
            typeName.StartsWith("System.Nullable<", StringComparison.Ordinal);

        /// <summary>
        /// Computes struct padding waste (in bytes) given the struct's declared size and the sum of
        /// its field sizes. Returns 0 when there is no measurable waste (e.g. fields unavailable).
        /// </summary>
        internal static int ComputePaddingWaste(int structSize, int fieldBytes) =>
            fieldBytes > 0 && structSize > fieldBytes ? structSize - fieldBytes : 0;

        public void Dispose() { }
    }
}
