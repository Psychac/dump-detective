using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Memory
{
    /// <summary>
    /// Phase 1 retyping pilot (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// the first analyzer retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>,
    /// sourcing everything through <see cref="IHeapTypeStatisticsQuery"/> (the <c>heap.types</c>
    /// capability) instead of a raw <c>ClrHeap</c>/<c>IHeapAnalysisCache</c>. Runs through the
    /// existing pipeline via <see cref="GCGenerationAnalyzerLegacyAdapter"/>, which also wraps
    /// <c>Core.Abstractions.IAnalyzer</c> for <c>BenchmarkSuite1</c>.
    /// </summary>
    [AnalyzerModule(key: "gc-generation", displayName: "GC Generation Analysis", order: 110, tags: ["gc"])]
    [RequiresCapability(CapabilityVocabulary.HeapTypes)]
    public sealed class GCGenerationAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
    {
        public string Name => "GC Generation Analysis";
        public string Category => "GC";
        public IReadOnlyCollection<string> Tags => new[] { "gc", "generations", "memory", "performance" };
        public int Order => 10;
        public bool IsThreadSafe => false;

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
            GCGenerationAnalysisOptions options = context.AnalyzerOptions as GCGenerationAnalysisOptions ?? new GCGenerationAnalysisOptions();

            var result = Analyze(statsQuery, options, context.Progress);
            LastResult = result;

            await EmitObservationAsync(context, result, cancellationToken);
        }

        /// <summary>
        /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): emits a
        /// single whole-heap <c>gc.generation-composition</c> observation — the exact canonical
        /// example named in observation-and-correlation-model.md § 2a. Deliberately <b>not</b> one
        /// observation per <see cref="GCGenerationDomainResult.PerTypeGenerationProfiles"/> entry:
        /// that list carries every distinct type on the heap (the same population the Vision doc
        /// cites at 131,568 unique names on the reference dump), which is exactly the "observations
        /// are conclusions, not per-object records" volume risk
        /// observation-and-correlation-model.md § 7 flags as unvalidated — left for a deliberate,
        /// separately-measured pass rather than emitted here on a guess. No entity subject: this is
        /// a whole-process scalar composition with no natural per-type anchor (see
        /// ReportSystemVision.md § 8's same observation about whole-process scalars).
        /// <para>
        /// Phase 5 step 2 adds: the count-based whole-run scalars (<c>gen0.count</c>/<c>gen2.count</c>/
        /// <c>poh.count</c>/<c>total.count</c>) alongside the existing byte-based ones;
        /// <c>loh.percent</c>/<c>gen2.percent</c> (count/byte-based percentages
        /// <c>GCGenerationFindingGenerator</c> reads directly — genuinely distinct from
        /// <c>loh.fraction</c>/<c>gen2.fraction</c> above despite the similar names: those are
        /// byte-fraction-of-total-managed-bytes, these are the domain result's own differently-scoped
        /// percentages); the three configurable thresholds (avoiding the same hard-coded-default
        /// mistake <c>GCHandleSynthesisRule</c>'s design fixed); <c>has-gen2-bytes</c> (the same
        /// exact-Gen2-bytes-availability flag the finding generator re-derives by scanning
        /// <c>PerTypeGenerationProfiles</c> itself); and the finalizable-Gen2 cross-reference totals.
        /// <c>FallbackMode</c>/<c>GenBytesAreApproximate</c> need no new measures — they're already
        /// exactly <see cref="Provenance.Fidelity"/>/<see cref="Observation.Confidence"/> below.
        /// </para>
        /// <para>
        /// Also emits two new small, bounded per-type observations —
        /// <c>gc.generation-top-allocator</c>/<c>gc.generation-top-accumulator</c> — carrying exactly
        /// the (≤3 each) types <see cref="SelectTopTypes"/> already picks from the full,
        /// <b>not observed</b> <c>PerTypeGenerationProfiles</c> population, framework-namespace-
        /// deprioritized the same way the old finding generator's own (now-ported)
        /// <c>BuildTopTypeEvidence</c> did. Doing the selection here, not in the synthesis rule, is
        /// what keeps the per-type observation count at 6, not 131,568 — the rule only ever sees the
        /// already-decided winners, never the full population.
        /// </para>
        /// </summary>
        private static async ValueTask EmitObservationAsync(AnalysisContext context, GCGenerationDomainResult result, CancellationToken cancellationToken)
        {
            ulong totalManagedBytes = result.Gen0Bytes + result.Gen1Bytes + result.Gen2Bytes + result.LohBytes + result.PohBytes;
            double gen2Fraction = totalManagedBytes == 0 ? 0.0 : result.Gen2Bytes / (double)totalManagedBytes;
            double lohFraction = totalManagedBytes == 0 ? 0.0 : result.LohBytes / (double)totalManagedBytes;

            // gen0.percent/gen1.percent/poh.percent mirror gen2.percent's own formula
            // (objects * 100.0 / total, 0 when TotalObjects is 0) — added Phase 5 step 4 for
            // GCGenerationTrendComparer's headline metrics (Bucket D of
            // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md).
            double gen0Pct = result.TotalObjects == 0 ? 0.0 : result.Gen0Objects * 100.0 / result.TotalObjects;
            double gen1Pct = result.TotalObjects == 0 ? 0.0 : result.Gen1Objects * 100.0 / result.TotalObjects;
            double pohPct = result.TotalObjects == 0 ? 0.0 : result.PohObjects * 100.0 / result.TotalObjects;

            IReadOnlyList<TypeGenerationProfile> profiles = result.PerTypeGenerationProfiles ?? [];
            bool anyGen2Bytes = false;
            for (int i = 0; i < profiles.Count; i++)
            {
                if (profiles[i].Gen2Bytes > 0)
                {
                    anyGen2Bytes = true;
                    break;
                }
            }

            var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "gc.generation-composition",
                Subjects = [],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["gen0.bytes"] = new Measure(result.Gen0Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen1.bytes"] = new Measure(result.Gen1Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.bytes"] = new Measure(result.Gen2Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["loh.bytes"] = new Measure(result.LohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["poh.bytes"] = new Measure(result.PohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.fraction"] = new Measure(gen2Fraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["loh.fraction"] = new Measure(lohFraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["gen0.count"] = new Measure(result.Gen0Objects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen1.count"] = new Measure(result.Gen1Objects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(result.Gen2Objects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.count"] = new Measure(result.LohObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["poh.count"] = new Measure(result.PohObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total.count"] = new Measure(result.TotalObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.percent"] = new Measure(result.LohPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["gen0.percent"] = new Measure(gen0Pct, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["gen1.percent"] = new Measure(gen1Pct, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["gen2.percent"] = new Measure(result.Gen2Pct, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["poh.percent"] = new Measure(pohPct, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["loh-pressure.threshold"] = new Measure(result.LohThresholdPercent, MeasureUnit.Percent, MeasureSemantics.Absolute),
                    ["gen0-pressure.threshold"] = new Measure(result.Gen0PressureThresholdPercent, MeasureUnit.Percent, MeasureSemantics.Absolute),
                    ["poh-pressure.threshold"] = new Measure(result.PohThresholdPercent, MeasureUnit.Percent, MeasureSemantics.Absolute),
                    ["has-gen2-bytes"] = new Measure(anyGen2Bytes ? 1 : 0, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["finalizable-gen2.count"] = new Measure(result.FinalizableGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["finalizable-gen2.bytes"] = new Measure(result.FinalizableGen2Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "GC Generation Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = result.FallbackMode ? FidelityLevel.Degraded : FidelityLevel.Full,
                },
                Confidence = result.GenBytesAreApproximate ? 0.5 : 1.0,
            }, cancellationToken);

            foreach (TypeGenerationProfile type in SelectTopTypes(profiles, p => p.Gen0Count, p => p.Gen0Count))
            {
                (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(type.TypeName);
                await context.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "gc.generation-top-allocator",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["gen0.count"] = new Measure(type.Gen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = context.ArtifactId!.Value,
                        AnalyzerKey = "GC Generation Analysis",
                        CapabilitiesUsed = capabilitiesUsed,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }

            IReadOnlyList<TypeGenerationProfile> topAccumulators = anyGen2Bytes
                ? SelectTopTypes(profiles, p => (long)Math.Min(p.Gen2Bytes, long.MaxValue), p => (long)Math.Min(p.Gen2Bytes, long.MaxValue))
                : SelectTopTypes(profiles, p => p.Gen2Count, p => p.Gen2Count);

            foreach (TypeGenerationProfile type in topAccumulators)
            {
                (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(type.TypeName);
                await context.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "gc.generation-top-accumulator",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["gen2.bytes"] = new Measure(type.Gen2Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["gen2.count"] = new Measure(type.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = context.ArtifactId!.Value,
                        AnalyzerKey = "GC Generation Analysis",
                        CapabilitiesUsed = capabilitiesUsed,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }
        }

        /// <summary>
        /// Ported from <c>GCGenerationFindingGenerator.BuildTopTypeEvidence</c>'s selection half (not
        /// its text-formatting half, which stays in the rule): picks up to <paramref name="maxCount"/>
        /// types with a non-zero <paramref name="hasSignal"/>, ranked descending by
        /// <paramref name="rankKey"/>, preferring application-namespace types over framework ones
        /// (<see cref="IsFrameworkType"/>) when both compete for the remaining slots. Runs here, not
        /// in the synthesis rule, specifically so the full <c>PerTypeGenerationProfiles</c> population
        /// never has to leave this analyzer as observations.
        /// </summary>
        private static List<TypeGenerationProfile> SelectTopTypes(
            IReadOnlyList<TypeGenerationProfile> profiles,
            Func<TypeGenerationProfile, long> hasSignal,
            Func<TypeGenerationProfile, long> rankKey,
            int maxCount = 3)
        {
            var candidates = new List<TypeGenerationProfile>(profiles.Count);
            for (int i = 0; i < profiles.Count; i++)
                if (hasSignal(profiles[i]) > 0)
                    candidates.Add(profiles[i]);

            candidates.Sort((a, b) => rankKey(b).CompareTo(rankKey(a)));

            var selected = new List<TypeGenerationProfile>(maxCount);
            for (int i = 0; i < candidates.Count && selected.Count < maxCount; i++)
                if (!IsFrameworkType(candidates[i].TypeName))
                    selected.Add(candidates[i]);
            for (int i = 0; i < candidates.Count && selected.Count < maxCount; i++)
                if (IsFrameworkType(candidates[i].TypeName))
                    selected.Add(candidates[i]);

            return selected;
        }

        /// <summary>Ported from <c>GCGenerationFindingGenerator.IsFrameworkType</c> — see
        /// <see cref="SelectTopTypes"/>.</summary>
        private static bool IsFrameworkType(string typeName) =>
            typeName.StartsWith("System.", StringComparison.Ordinal) ||
            typeName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            typeName.StartsWith("Windows.", StringComparison.Ordinal) ||
            typeName.StartsWith("Mono.", StringComparison.Ordinal) ||
            typeName is "System" or "Microsoft" or "Windows";

        private static GCGenerationDomainResult Analyze(
            IHeapTypeStatisticsQuery statsQuery,
            GCGenerationAnalysisOptions options,
            IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, statsQuery.HasExactGenerationData ? "reading type aggregates" : "reading type statistics (fallback)"));

            IReadOnlyDictionary<string, HeapTypeStatistics> stats = statsQuery.GetTypeStatistics();

            return statsQuery.HasExactGenerationData
                ? BuildFromIndex(statsQuery, stats, options)
                : BuildFromTypeStatistics(stats, options);
        }

        // ── Fast path: exact per-generation type statistics ────────────────────────

        private static GCGenerationDomainResult BuildFromIndex(
            IHeapTypeStatisticsQuery statsQuery,
            IReadOnlyDictionary<string, HeapTypeStatistics> stats,
            GCGenerationAnalysisOptions options)
        {
            ulong lohBytes = 0;
            long totalObjects = 0, lohObjects = 0;
            long gen0Objects = 0, gen1Objects = 0, gen2Objects = 0;
            bool anyGen2Bytes = false;

            var lohCandidates = new List<(string Name, HeapTypeStatistics Entry)>(stats.Count);
            var genCandidates = new List<(string Name, HeapTypeStatistics Entry)>(stats.Count);

            foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
            {
                HeapTypeStatistics e = kv.Value;
                gen0Objects += e.Gen0Count;
                gen1Objects += e.Gen1Count;
                gen2Objects += e.Gen2Count;
                lohBytes += e.LohSize;
                lohObjects += e.LohCount;
                totalObjects += e.InstanceCount;

                if (e.Gen2TotalSize > 0)
                    anyGen2Bytes = true;

                if (e.LohCount > 0)
                    lohCandidates.Add((kv.Key, e));

                genCandidates.Add((kv.Key, e));
            }

            long accountedGen = gen0Objects + gen1Objects + gen2Objects;

            // Exact gen bytes from segment metadata.
            (ulong gen0Bytes, ulong gen1Bytes, ulong gen2Bytes) = statsQuery.GetExactGenerationByteTotals();

            ulong totalManagedBytes = gen0Bytes + gen1Bytes + gen2Bytes + lohBytes;
            double lohPct = totalManagedBytes == 0 ? 0.0 : lohBytes * 100.0 / totalManagedBytes;
            double gen2Pct = totalObjects == 0 ? 0.0 : gen2Objects * 100.0 / totalObjects;

            // Top LOH types, ranked by LOH size.
            lohCandidates.Sort(static (a, b) => b.Entry.LohSize.CompareTo(a.Entry.LohSize));
            var topLohTypes = new List<TypeSnapshot>(lohCandidates.Count);
            foreach ((string name, HeapTypeStatistics e) in lohCandidates)
                topLohTypes.Add(new TypeSnapshot(name, (int)Math.Min(int.MaxValue, e.LohCount), e.LohSize, e.LohSize));

            // Per-type generation profiles, ranked by exact Gen2 bytes so memory-heavy accumulators
            // surface ahead of small high-count types. Falls back to instance count when no entry
            // carries Gen2 bytes (heap indices written before Gen2TotalSize existed).
            List<TypeGenerationProfile> profiles = [];
            long finalizableGen2Count = 0;
            ulong finalizableGen2Bytes = 0;
            if (accountedGen > 0)
            {
                if (anyGen2Bytes)
                    genCandidates.Sort(static (a, b) => b.Entry.Gen2TotalSize.CompareTo(a.Entry.Gen2TotalSize));
                else
                    genCandidates.Sort(static (a, b) => b.Entry.InstanceCount.CompareTo(a.Entry.InstanceCount));

                profiles = new List<TypeGenerationProfile>(genCandidates.Count);
                foreach ((string name, HeapTypeStatistics e) in genCandidates)
                {
                    profiles.Add(new TypeGenerationProfile(
                        name, e.Gen0Count, e.Gen1Count, e.Gen2Count,
                        (int)Math.Min(int.MaxValue, e.LohCount),
                        e.TotalSize,
                        e.Gen2TotalSize,
                        e.IsFinalizableType));

                    if (e.IsFinalizableType)
                    {
                        finalizableGen2Count += e.Gen2Count;
                        finalizableGen2Bytes += e.Gen2TotalSize;
                    }
                }
            }

            // POH detection for .NET 5+ — always zero today; see AnalyzerHelpers.ComputePohMetrics'
            // own remarks (POH objects aren't in TypeAggregates, detection needs runtime-level
            // segment inspection that hasn't been built). Inlined rather than routed through the
            // capability surface since it never varies with the input.
            const ulong pohBytes = 0;
            const long pohObjects = 0;

            return new GCGenerationDomainResult(
                gen0Bytes,
                (int)Math.Min(int.MaxValue, gen0Objects),
                gen1Bytes,
                (int)Math.Min(int.MaxValue, gen1Objects),
                gen2Bytes,
                (int)Math.Min(int.MaxValue, gen2Objects),
                lohBytes,
                lohPct,
                (int)Math.Min(int.MaxValue, totalObjects),
                (int)Math.Min(int.MaxValue, lohObjects),
                topLohTypes,
                PohBytes: pohBytes,
                PohObjects: pohObjects,
                Gen2Pct: gen2Pct,
                PerTypeGenerationProfiles: profiles,
                GenBytesAreApproximate: false,
                FallbackMode: false,
                LohThresholdPercent: options.LohThresholdPercent,
                Gen0PressureThresholdPercent: options.Gen0PressureThresholdPercent,
                PohThresholdPercent: options.PohThresholdPercent,
                FinalizableGen2Count: finalizableGen2Count,
                FinalizableGen2Bytes: finalizableGen2Bytes);
        }

        // ── Slow / fallback path (no per-generation index) ─────────────────────────

        private static GCGenerationDomainResult BuildFromTypeStatistics(
            IReadOnlyDictionary<string, HeapTypeStatistics> typeStats,
            GCGenerationAnalysisOptions options)
        {
            ulong lohBytes = 0;
            int totalObjects = 0, lohObjects = 0, gen2Objects = 0;
            ulong gen2Bytes = 0;

            foreach (HeapTypeStatistics stat in typeStats.Values)
            {
                lohBytes += stat.LohSize;
                totalObjects += (int)stat.InstanceCount;
                lohObjects += (int)stat.LohCount;
                int nonLoh = Math.Max(0, (int)stat.InstanceCount - (int)stat.LohCount);
                ulong nonLohBytes = stat.TotalSize >= stat.LohSize ? stat.TotalSize - stat.LohSize : 0;
                gen2Objects += nonLoh;
                gen2Bytes += nonLohBytes;
            }

            ulong totalManagedBytes = gen2Bytes + lohBytes;
            double lohPct = totalManagedBytes == 0 ? 0.0 : lohBytes * 100.0 / totalManagedBytes;
            double gen2Pct = totalObjects == 0 ? 0.0 : gen2Objects * 100.0 / totalObjects;

            // No LINQ in hot paths — explicit sort + loop.
            var lohList = new List<(string Name, HeapTypeStatistics Stat)>(capacity: 32);
            foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStats)
                if (kv.Value.LohCount > 0) lohList.Add((kv.Key, kv.Value));
            lohList.Sort(static (a, b) => b.Stat.LohSize.CompareTo(a.Stat.LohSize));
            var topLohTypes = new List<TypeSnapshot>(lohList.Count);
            foreach ((string name, HeapTypeStatistics stat) in lohList)
                topLohTypes.Add(new TypeSnapshot(name, (int)stat.LohCount, stat.LohSize, stat.LohSize));

            return new GCGenerationDomainResult(
                Gen0Bytes: 0, Gen0Objects: 0,
                Gen1Bytes: 0, Gen1Objects: 0,
                gen2Bytes, gen2Objects,
                lohBytes, lohPct,
                totalObjects, lohObjects,
                topLohTypes,
                PohBytes: 0,
                PohObjects: 0,
                Gen2Pct: gen2Pct,
                PerTypeGenerationProfiles: [],
                GenBytesAreApproximate: true,
                FallbackMode: true,
                LohThresholdPercent: options.LohThresholdPercent,
                Gen0PressureThresholdPercent: options.Gen0PressureThresholdPercent,
                PohThresholdPercent: options.PohThresholdPercent);
        }

        public void Dispose() { }
    }
}
