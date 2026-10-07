using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.Runtime
{
    /// <summary>
    /// Tier-1+Tier-2 (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Reads
    /// exception candidates from <see cref="AnalysisContext.HeapObjects"/> — populated by
    /// <see cref="CrashAnalyzerLegacyAdapter"/>'s heap-index-scan participant role, which owns every
    /// structural ClrType-level fact (is-exception, is-<c>AggregateException</c>) this analyzer has
    /// no capability surface to ask for itself.
    /// </summary>
    /// <remarks>
    /// Two real capability gaps closed: <see cref="IHeapSegmentQuery.GetGeneration"/> (per-object
    /// generation, replacing the pre-retyping analyzer's own <c>seg.GetGeneration(address)</c>/
    /// <c>HeapEntry.Generation</c> reads) and the thread-exception correlation this analyzer's
    /// active-candidate tracking needs — closed by adding <see cref="RuntimeThreadRef.ExceptionAddress"/>
    /// (<see cref="IRuntimeThreadQuery"/> already carried <c>HasCurrentException</c>/
    /// <c>ExceptionTypeName</c>/<c>ExceptionMessage</c> from <c>ThreadAnalyzer</c>'s retyping, but
    /// none of those can join a thread's live exception back to the specific heap-scanned
    /// <see cref="ExceptionInstance"/> by address).
    ///
    /// <c>Message</c>/<c>HResult</c>/<c>InnerException</c> are read via raw
    /// <c>_message</c>/<c>_HResult</c>/<c>_innerException</c> field names through
    /// <see cref="IDumpObjectFieldQuery.TryReadField"/> rather than <c>ClrObject.AsException()</c>'s
    /// convenience properties — confirmed, by decompiling the installed ClrMD package
    /// (v4.0.732401), to be exactly equivalent: <c>ClrException.Message</c>/<c>.HResult</c>/
    /// <c>.Inner</c> all resolve the same field by name (with a hardcoded-offset fallback for
    /// stripped metadata this analyzer doesn't need) and read it directly, no extra defaulting
    /// logic in between. <c>ClrException.StackTrace</c> is genuinely different — it walks the
    /// exception's internal binary <c>_stackTrace</c> trace-info array, not a field ClrMD exposes as
    /// a plain value — so <see cref="ExtractExceptionStackTrace"/> calls the new
    /// <see cref="IDumpObjectFieldQuery.EnumerateExceptionStackTrace"/> Tier-2 primitive for that
    /// tier specifically, restoring the pre-retyping analyzer's exact three-tier fallback
    /// (<c>_stackTraceString</c> text → binary trace-array unwind → <c>_remoteStackTraceString</c>).
    /// </remarks>
    [AnalyzerModule(key: "crash", displayName: "Crash Analysis", order: 170, tags: ["exceptions"])]
    [RequiresCapability(
        CapabilityVocabulary.DumpObjectFields,
        CapabilityVocabulary.HeapObjects,
        CapabilityVocabulary.HeapSegments,
        CapabilityVocabulary.RuntimeThreads)]
    [OptionalCapability(CapabilityVocabulary.TraceExceptionEvents)]
    public sealed class CrashAnalyzer :
        IAnalyzer, IProducesAnalyzerDomainResult, IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext
    {
        private ClrHeap? _heap;
        private readonly Dictionary<ulong, bool> _isExceptionByMt = new();
        private readonly Dictionary<ulong, bool> _isAggregateByMt = new();
        private readonly List<Sdk.Analysis.HeapObjectRef> _scanCandidates = new();
        private readonly HashSet<ulong> _aggregateAddresses = new();
        private bool _scanSucceeded = true;

        public string Name => "Crash Analysis";
        public string Category => "Crash";

        public AnalyzerDomainResult? LastResult
        {
            get => _lastResult;
            // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
            // longer exists) so every LastResult consumer — pipeline or direct caller — sees
            // them populated the same way.
            private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
        }
        private AnalyzerDomainResult? _lastResult;

        void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _heap = context.Heap;

        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
        {
            if (entry.Address == 0 || entry.MethodTable == 0)
                return;

            if (!_isExceptionByMt.TryGetValue(entry.MethodTable, out bool isException))
            {
                ClrType? type = _heap!.GetTypeByMethodTable(entry.MethodTable);
                isException = type?.IsException == true;
                _isExceptionByMt[entry.MethodTable] = isException;
                if (isException)
                    _isAggregateByMt[entry.MethodTable] = IsAggregateExceptionType(type);
            }

            if (!isException)
                return;

            string name = _heap!.GetTypeByMethodTable(entry.MethodTable)?.Name ?? $"MT:0x{entry.MethodTable:x}";
            _scanCandidates.Add(new Sdk.Analysis.HeapObjectRef(entry.Address, SdkTypeRefFactory.FromName(name, entry.MethodTable), entry.Size, name));

            if (_isAggregateByMt.TryGetValue(entry.MethodTable, out bool isAggregate) && isAggregate)
                _aggregateAddresses.Add(entry.Address);
        }

        // Walks the base-type chain so AggregateException subclasses (rare, but legal) are still
        // unwrapped, not just the sealed-in-practice BCL type.
        private static bool IsAggregateExceptionType(ClrType? type)
        {
            while (type != null)
            {
                if (type.Name == "System.AggregateException")
                    return true;
                type = type.BaseType;
            }
            return false;
        }

        IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new CrashAnalyzer();

        void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
        {
            foreach (IHeapIndexScanParticipant p in partials)
            {
                if (p is not CrashAnalyzer other)
                    continue;

                _scanCandidates.AddRange(other._scanCandidates);
                foreach (ulong address in other._aggregateAddresses)
                    _aggregateAddresses.Add(address);
                if (!other._scanSucceeded)
                    _scanSucceeded = false;
            }
        }

        void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanSucceeded = succeeded;

        Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
            AnalysisContextBuilder.Build(
                context,
                new CrashAnalyzerOptions(new CrashAnalysisOptions(), _scanSucceeded ? _aggregateAddresses : []),
                heapObjectsOverride: new PrecomputedHeapObjectStream(_scanSucceeded ? _scanCandidates : []));

        public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IHeapObjectStream candidates = context.HeapObjects
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
            IDumpObjectFieldQuery fields = context.DumpObjectFields
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpObjectFields}' capability.");
            IDumpArrayShapeQuery arrayShapes = context.DumpArrayShapes
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpArrayElements}' capability.");
            IHeapObjectLookup objectLookup = context.HeapObjectLookup
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
            IHeapSegmentQuery segments = context.HeapSegments
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
            IRuntimeThreadQuery threads = context.RuntimeThreads
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreads}' capability.");

            CrashAnalyzerOptions crashOptions = context.AnalyzerOptions as CrashAnalyzerOptions
                ?? new CrashAnalyzerOptions(new CrashAnalysisOptions(), new HashSet<ulong>());

            ExceptionAnalysis analysis = await Analyze(candidates, fields, arrayShapes, objectLookup, segments, threads, crashOptions, context, cancellationToken);
            var result = (CrashDomainResult)BuildDomainResult(analysis, context.HeapRootPaths, crashOptions.Options);
            LastResult = result;

            // Both candidate observation types are emitted from the final CrashThreadCandidateSnapshot
            // list: TopUserFrameModule (CrashModuleHotspotInsightRule) and the original-stack-trace
            // tier (CrashSynthesisRule's confidence) are only resolved in BuildCrashThreadSnapshotsImpl.
            await EmitCrashThreadCandidateObservationsAsync(context, result, cancellationToken);
            await EmitCrashThreadCandidateModuleObservationsAsync(context, result, cancellationToken);
        }

        // Emitted from the final snapshots rather than inline in Analyze: each candidate's
        // original-stack-trace tier is only resolved in BuildCrashThreadSnapshotsImpl, and
        // CrashSynthesisRule's confidence and caveats read it. Snapshot order is the candidates'
        // own descending-ActiveExceptionCount order, which the rule trusts.
        // The exception type stays Subjects[0], where the synthesis rules read it; the thread is a second subject so the
        // thread's own page can find the exception.
        private static ThreadRef ThreadSubject(uint osThreadId) => new() { OsThreadId = osThreadId, Fidelity = MatchFidelity.Exact };

        private static async ValueTask EmitCrashThreadCandidateObservationsAsync(
            AnalysisContext context, CrashDomainResult result, CancellationToken cancellationToken)
        {
            var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.RuntimeThreads };
            foreach (CrashThreadCandidateSnapshot candidate in result.TopCrashThreadCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(candidate.PrimaryExceptionType);
                await context.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "runtime.crash-thread-candidate",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }, ThreadSubject(candidate.OSThreadId)],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["active-exception.count"] = new Measure(candidate.ActiveExceptionCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = context.ArtifactId!.Value,
                        AnalyzerKey = "Crash Analysis",
                        CapabilitiesUsed = observationCapabilities,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                    Evidence = [new EvidenceRef
                    {
                        Artifact = context.ArtifactId!.Value,
                        Locator = "original-stack-trace-confidence",
                        Description = candidate.OriginalStackTraceConfidence.ToString(),
                    }],
                }, cancellationToken);
            }
        }

        private static async ValueTask EmitCrashThreadCandidateModuleObservationsAsync(
            AnalysisContext context, CrashDomainResult result, CancellationToken cancellationToken)
        {
            foreach (CrashThreadCandidateSnapshot candidate in result.TopCrashThreadCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(candidate.TopUserFrameModule))
                    continue;

                await context.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "runtime.crash-thread-candidate-module",
                    Subjects = [new ModuleRef { SimpleName = candidate.TopUserFrameModule, Fidelity = MatchFidelity.Exact }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["active-exception.count"] = new Measure(candidate.ActiveExceptionCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = context.ArtifactId!.Value,
                        AnalyzerKey = "Crash Analysis",
                        CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.RuntimeThreads },
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }
        }

        /// <summary>
        /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
        /// <c>runtime.active-exception</c> observation per candidate whose address matches a
        /// thread's live current-exception — inline right by <c>list.Add(instance)</c>, where
        /// <c>candidate.Type</c> is still in scope. Only the active subset, not every scanned
        /// exception candidate (which — unlike GCRootAnalyzer's uncapped root list — really can run
        /// into the thousands under a genuine exception storm; <c>ExceptionsByType</c> is already
        /// per-type-capped by <c>options.MaxExceptionsPerType</c>, and even that population is left
        /// out of this pass).
        /// Phase 5 step 2 adds: a whole-run <c>runtime.crash-population</c> observation
        /// (<c>TotalExceptions</c>/<c>ActiveExceptions</c>/unique-type count — exhaustive scalars,
        /// emitted unconditionally including the zero-exception case). Per-candidate
        /// <c>runtime.crash-thread-candidate</c> observations are emitted later, from the final
        /// snapshots — see <see cref="EmitCrashThreadCandidateObservationsAsync"/>.
        /// </summary>
        private async ValueTask<ExceptionAnalysis> Analyze(
            IHeapObjectStream candidates, IDumpObjectFieldQuery fields, IDumpArrayShapeQuery arrayShapes,
            IHeapObjectLookup objectLookup, IHeapSegmentQuery segments, IRuntimeThreadQuery threads,
            CrashAnalyzerOptions crashOptions, AnalysisContext observationContext, CancellationToken cancellationToken)
        {
            CrashAnalysisOptions options = crashOptions.Options;
            IReadOnlySet<ulong> aggregateAddresses = crashOptions.AggregateExceptionAddresses;

            Dictionary<ulong, ActiveExceptionContext> activeExceptions = BuildActiveExceptionLookup(threads);
            var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.RuntimeThreads };

            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>(StringComparer.Ordinal);
            var exceptionTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var activeExceptionTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var exceptionGen0Counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var exceptionGen1Counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var exceptionGen2Counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var exceptionLohCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var exceptionHeapSizeByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var aggregateInnerExceptionTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var crashThreadCandidates = new Dictionary<uint, CrashThreadCandidate>();
            int totalExceptions = 0;
            int activeExceptionsCount = 0;
            int aggregateExceptionCount = 0;

            foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string key = candidate.TypeDisplayName.Length > 0 ? candidate.TypeDisplayName : StringConstants.UnknownType;
                totalExceptions++;
                Increment(exceptionTypeCounts, key);
                AddSize(exceptionHeapSizeByType, key, candidate.Size);

                // AggregateException unwrapping: computed unconditionally (not gated by
                // MaxExceptionsPerType) so AggregateInnerExceptionTypeCounts is an exact total, not a
                // sample — same rationale as the Gen0/Gen1/Gen2/LOH counts below.
                List<string>? aggregateInnerTypes = null;
                if (aggregateAddresses.Contains(candidate.Address))
                {
                    aggregateInnerTypes = ExtractAggregateInnerExceptionTypes(fields, arrayShapes, objectLookup, candidate.Address);
                    if (aggregateInnerTypes != null)
                    {
                        aggregateExceptionCount++;
                        for (int i = 0; i < aggregateInnerTypes.Count; i++)
                            Increment(aggregateInnerExceptionTypeCounts, aggregateInnerTypes[i]);
                    }
                }

                int generation = ToGenerationOrdinal(segments.GetGeneration(candidate.Address));
                switch (generation)
                {
                    case 0: Increment(exceptionGen0Counts, key); break;
                    case 1: Increment(exceptionGen1Counts, key); break;
                    case 2: Increment(exceptionGen2Counts, key); break;
                    default: Increment(exceptionLohCounts, key); break;
                }

                bool isActive = activeExceptions.TryGetValue(candidate.Address, out ActiveExceptionContext? activeContext);
                if (isActive)
                {
                    activeExceptionsCount++;
                    Increment(activeExceptionTypeCounts, key);

                    if (!crashThreadCandidates.TryGetValue(activeContext!.ThreadId, out CrashThreadCandidate? candidateThread))
                    {
                        candidateThread = new CrashThreadCandidate
                        {
                            ThreadId = activeContext.ThreadId,
                            OSThreadId = activeContext.OSThreadId,
                            CurrentThreadStack = activeContext.CurrentThreadStack,
                            PrimaryExceptionType = key,
                        };
                        crashThreadCandidates[activeContext.ThreadId] = candidateThread;
                    }
                    candidateThread.ActiveExceptionCount++;
                }

                if (!exceptionsByType.TryGetValue(key, out List<ExceptionInstance>? list))
                {
                    list = new List<ExceptionInstance>(capacity: options.MaxExceptionsPerType);
                    exceptionsByType[key] = list;
                }

                if (list.Count < options.MaxExceptionsPerType || isActive)
                {
                    ExceptionInstance instance = ExtractExceptionInfo(fields, objectLookup, candidate.Address, key, activeContext);
                    instance.Generation = generation;
                    if (aggregateInnerTypes != null)
                        instance.AggregateInnerExceptionTypes = CapForDisplay(aggregateInnerTypes);

                    if (isActive)
                    {
                        CrashThreadCandidate candidateThread = crashThreadCandidates[activeContext!.ThreadId];
                        if (instance.OriginalStackTrace.Count > 0)
                        {
                            candidateThread.OriginalExceptionStack = instance.OriginalStackTrace;
                            candidateThread.OriginalExceptionStackIsRethrown = instance.IsRethrown;
                        }
                        if (!string.IsNullOrWhiteSpace(instance.Message))
                            candidateThread.SampleMessage = instance.Message;
                        candidateThread.SampleHResult = instance.HResult;
                        candidateThread.SampleInnerExceptionType ??= instance.InnerExceptionType;

                        await observationContext.Observations.EmitAsync(new Observation
                        {
                            Id = ObservationId.NewId(),
                            ObservationType = "runtime.active-exception",
                            Subjects = [candidate.Type, ThreadSubject(activeContext.OSThreadId)],
                            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                            Measures = new Dictionary<string, Measure>
                            {
                                ["generation.ordinal"] = new Measure(generation, MeasureUnit.Custom, MeasureSemantics.Absolute),
                                ["shallow.bytes"] = new Measure(candidate.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                            },
                            Provenance = new Provenance
                            {
                                Artifact = observationContext.ArtifactId!.Value,
                                AnalyzerKey = "Crash Analysis",
                                CapabilitiesUsed = observationCapabilities,
                                Fidelity = FidelityLevel.Full,
                            },
                            Confidence = 1.0,
                        }, cancellationToken);
                    }

                    list.Add(instance);
                }
            }

            var sortedExceptionsByType = new Dictionary<string, List<ExceptionInstance>>(exceptionsByType.Count, StringComparer.Ordinal);
            foreach (string typeName in exceptionTypeCounts.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key))
            {
                if (exceptionsByType.TryGetValue(typeName, out List<ExceptionInstance>? list))
                    sortedExceptionsByType[typeName] = list;
            }

            List<CrashThreadCandidate> topCrashThreadCandidates = crashThreadCandidates.Values
                .OrderByDescending(c => c.ActiveExceptionCount)
                .ToList();

            // Phase 5 step 2: one whole-run runtime.crash-population observation (exhaustive
            // scalars, emitted unconditionally including the zero-exception case).
            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.crash-population",
                Subjects = [],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(totalExceptions, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["active.count"] = new Measure(activeExceptionsCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["unique-types.count"] = new Measure(exceptionTypeCounts.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Crash Analysis",
                    CapabilitiesUsed = observationCapabilities,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);

            // Per-type ALL-exception population (phase 5 step 6.2, docs/refactor/modularity/
            // phase-5-step6-insight-engine-plan.md) — runtime.active-exception above only covers
            // currently-active exceptions; several InsightEngine correlation rules need the
            // all-exception ExceptionTypeCounts breakdown (fatal-type detection, timeout/
            // ObjectDisposed/TaskCanceled pattern matching), which had no observation of its own.
            // Bounded by distinct exception type count (unique-types.count above), not exception
            // instance count.
            foreach (KeyValuePair<string, int> kv in exceptionTypeCounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string canonicalName, MatchFidelity typeFidelity) = EntityCanonicalizer.CanonicalizeTypeName(kv.Key);

                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "runtime.exception-type",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = typeFidelity }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["instance.count"] = new Measure(kv.Value, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "Crash Analysis",
                        CapabilitiesUsed = observationCapabilities,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }

            return new ExceptionAnalysis
            {
                TotalExceptions = totalExceptions,
                ActiveExceptions = activeExceptionsCount,
                ExceptionTypeCounts = exceptionTypeCounts,
                ActiveExceptionTypeCounts = activeExceptionTypeCounts,
                ExceptionsByType = sortedExceptionsByType,
                CrashThreadCandidates = topCrashThreadCandidates,
                ExceptionGen0Counts = exceptionGen0Counts,
                ExceptionGen1Counts = exceptionGen1Counts,
                ExceptionGen2Counts = exceptionGen2Counts,
                ExceptionLohCounts = exceptionLohCounts,
                AggregateExceptionCount = aggregateExceptionCount,
                AggregateInnerExceptionTypeCounts = aggregateInnerExceptionTypeCounts,
                ExceptionHeapSizeByType = exceptionHeapSizeByType,
            };
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }

        private static void AddSize(Dictionary<string, ulong> sizes, string key, ulong size)
        {
            sizes.TryGetValue(key, out ulong total);
            sizes[key] = total + size;
        }

        // Per-*object* generation, mirroring the pre-retyping analyzer's own 0/1/2/default(LOH+)
        // bucketing exactly: Gen0/Gen1/Gen2 map directly, everything else (Loh/Poh/Frozen/Unknown)
        // falls into the same "LOH" bucket ExceptionLohCounts already used pre-retyping.
        private static int ToGenerationOrdinal(HeapGenerationTag tag) => tag switch
        {
            HeapGenerationTag.Gen0 => 0,
            HeapGenerationTag.Gen1 => 1,
            HeapGenerationTag.Gen2 => 2,
            HeapGenerationTag.Loh => 3,
            HeapGenerationTag.Poh => 4,
            HeapGenerationTag.Frozen => 5,
            _ => -1,
        };

        private static Dictionary<ulong, ActiveExceptionContext> BuildActiveExceptionLookup(IRuntimeThreadQuery threads)
        {
            var lookup = new Dictionary<ulong, ActiveExceptionContext>();

            foreach (RuntimeThreadRef thread in threads.EnumerateThreads())
            {
                if (!thread.HasCurrentException || thread.ExceptionAddress == 0)
                    continue;

                // Full stack — no artificial per-thread frame cap (§9.26). Only threads with an
                // active exception reach here, never heap-scale.
                var frames = new List<ThreadStackFrameRef>();
                foreach (ThreadStackFrameRef frame in threads.EnumerateStackFrames(thread))
                    frames.Add(frame);

                lookup[thread.ExceptionAddress] = new ActiveExceptionContext
                {
                    ThreadId = (uint)(thread.Thread.ManagedThreadId ?? 0),
                    OSThreadId = thread.Thread.OsThreadId,
                    CurrentThreadStack = frames,
                };
            }

            return lookup;
        }

        private static ExceptionInstance ExtractExceptionInfo(
            IDumpObjectFieldQuery fields, IHeapObjectLookup objectLookup, ulong exceptionAddress, string typeName, ActiveExceptionContext? activeContext)
        {
            var instance = new ExceptionInstance { Address = exceptionAddress, Type = typeName };

            if (fields.TryReadField(exceptionAddress, "_message", out FieldValue messageValue) && messageValue.Kind == FieldValueKind.String)
                instance.Message = messageValue.AsString();

            if (fields.TryReadField(exceptionAddress, "_HResult", out FieldValue hresultValue) && hresultValue.Kind == FieldValueKind.Int32)
                instance.HResult = hresultValue.AsInt32();

            if (fields.TryReadField(exceptionAddress, "_innerException", out FieldValue innerValue) && innerValue.Kind == FieldValueKind.ObjectReference)
            {
                ulong innerAddress = innerValue.AsObjectAddress();
                if (innerAddress != 0 && objectLookup.TryGetObject(innerAddress, out HeapObjectRef innerObj))
                    instance.InnerExceptionType = innerObj.TypeDisplayName;
            }

            instance.ChainDepth = ComputeExceptionChainDepth(fields, exceptionAddress);
            (instance.OriginalStackTrace, instance.IsRethrown) = ExtractExceptionStackTrace(fields, exceptionAddress);

            if (activeContext != null)
            {
                instance.ThreadId = activeContext.ThreadId;
                instance.OSThreadId = activeContext.OSThreadId;
                instance.CurrentThreadStack = activeContext.CurrentThreadStack;
            }

            return instance;
        }

        private static int ComputeExceptionChainDepth(IDumpObjectFieldQuery fields, ulong exceptionAddress)
        {
            const int MaxDepth = 16;
            int depth = 1;
            var seen = new HashSet<ulong>();
            ulong current = exceptionAddress;

            while (current != 0 && depth < MaxDepth && seen.Add(current))
            {
                if (!fields.TryReadField(current, "_innerException", out FieldValue innerValue) || innerValue.Kind != FieldValueKind.ObjectReference)
                    break;

                ulong innerAddress = innerValue.AsObjectAddress();
                if (innerAddress == 0)
                    break;

                depth++;
                current = innerAddress;
            }

            return depth;
        }

        // Three-tier fallback, exactly mirroring the pre-retyping analyzer: (1) _stackTraceString
        // text, (2) EnumerateExceptionStackTrace's binary trace-array unwind (covers an exception
        // that has been thrown/unwound but never had .ToString()/.StackTrace called to materialize
        // _stackTraceString as text), (3) the remote-stack text as a last resort.
        private static (List<string> Frames, bool IsRethrown) ExtractExceptionStackTrace(IDumpObjectFieldQuery fields, ulong exceptionAddress)
        {
            var stackFrames = new List<string>();
            bool isRethrown = false;
            string? remoteStack = null;

            // _remoteStackTraceString presence means the exception was rethrown via `throw;` or
            // ExceptionDispatchInfo.Throw() — checked independent of whether it ends up supplying the
            // frame text below, since _stackTraceString is often already populated even when the
            // exception has been rethrown.
            if (fields.TryReadField(exceptionAddress, "_remoteStackTraceString", out FieldValue remoteValue) && remoteValue.Kind == FieldValueKind.String)
            {
                remoteStack = remoteValue.AsString();
                isRethrown = !string.IsNullOrEmpty(remoteStack);
            }

            if (fields.TryReadField(exceptionAddress, "_stackTraceString", out FieldValue stackValue) && stackValue.Kind == FieldValueKind.String)
            {
                string? stackTraceStr = stackValue.AsString();
                if (!string.IsNullOrEmpty(stackTraceStr))
                {
                    var lines = stackTraceStr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                            stackFrames.Add(trimmed);
                    }
                    return (stackFrames, isRethrown);
                }
            }

            IReadOnlyList<ThreadStackFrameRef> unwoundFrames = fields.EnumerateExceptionStackTrace(exceptionAddress, maxFrames: 50);
            if (unwoundFrames.Count > 0)
            {
                foreach (ThreadStackFrameRef frame in unwoundFrames)
                {
                    if (frame.HasMethod)
                        stackFrames.Add($"   at {frame.RawMethodSignature ?? frame.MethodDisplayName}");
                }
                if (stackFrames.Count > 0)
                    return (stackFrames, isRethrown);
            }

            if (stackFrames.Count == 0 && !string.IsNullOrEmpty(remoteStack))
                stackFrames.Add(remoteStack);

            return (stackFrames, isRethrown);
        }

        // Unwraps AggregateException.InnerExceptions (backing field "_innerExceptions", a direct
        // Exception[] on .NET Core/5+). Returns the full, uncapped set of inner exception type
        // names — callers decide whether to cap for display; global tallies must stay exact.
        private static List<string>? ExtractAggregateInnerExceptionTypes(
            IDumpObjectFieldQuery fields, IDumpArrayShapeQuery arrayShapes, IHeapObjectLookup objectLookup, ulong exceptionAddress)
        {
            if (!fields.TryReadField(exceptionAddress, "_innerExceptions", out FieldValue value) || value.Kind != FieldValueKind.ObjectReference)
                return null;

            ulong arrayAddress = value.AsObjectAddress();
            if (arrayAddress == 0)
                return null;

            if (!arrayShapes.TryGetArrayShape(arrayAddress, out int length, out int rank) || rank != 1 || length <= 0)
                return null;

            IReadOnlyList<ulong> elements = arrayShapes.EnumerateObjectReferenceElements(arrayAddress, length);
            if (elements.Count == 0)
                return null;

            var types = new List<string>(elements.Count);
            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i] != 0 && objectLookup.TryGetObject(elements[i], out HeapObjectRef elemObj))
                    types.Add(elemObj.TypeDisplayName);
            }

            return types.Count > 0 ? types : null;
        }

        // Render-layer-only bound on the per-instance inner-type list embedded in one
        // ExceptionInstance (§9.26 D5 does not apply — this caps a single field's payload size for
        // report legibility, not the exact AggregateInnerExceptionTypeCounts totals).
        private const int MaxDisplayedAggregateInnerExceptionTypes = 25;

        private static List<string> CapForDisplay(List<string> types) =>
            types.Count > MaxDisplayedAggregateInnerExceptionTypes
                ? types.GetRange(0, MaxDisplayedAggregateInnerExceptionTypes)
                : types;

        private AnalyzerDomainResult BuildDomainResult(ExceptionAnalysis exceptionInfo, IHeapRootPathQuery? rootPaths, CrashAnalysisOptions options)
        {
            var candidateSnapshots = BuildCrashThreadSnapshotsImpl(exceptionInfo);

            // Complete type-count dictionaries — no report-width cap here (§9.26 D5); the render
            // layer slices for display.
            var payloadExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.ExceptionTypeCounts);
            var payloadActiveExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.ActiveExceptionTypeCounts);
            var payloadAggregateInnerExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.AggregateInnerExceptionTypeCounts);
            var payloadExceptionHeapSizeByType = new Dictionary<string, ulong>(exceptionInfo.ExceptionHeapSizeByType);

            return new CrashDomainResult(
                exceptionInfo.TotalExceptions,
                exceptionInfo.ActiveExceptions,
                payloadExceptionTypeCounts,
                payloadActiveExceptionTypeCounts,
                candidateSnapshots,
                BuildExceptionInstanceSnapshots(exceptionInfo),
                exceptionInfo.InferredTraceCount,
                exceptionInfo.AggregateExceptionCount,
                payloadAggregateInnerExceptionTypeCounts,
                BuildMessageDistributions(exceptionInfo),
                BuildCrashBuckets(exceptionInfo),
                payloadExceptionHeapSizeByType,
                BuildGen2RetentionPaths(rootPaths, exceptionInfo, options));
        }

        // Pure candidate selection — Generation >= 2 covers Gen2 plus LOH/Pinned/Frozen/Unknown, the
        // same bucket ExceptionLohCounts already uses. Extracted so the selection rule is testable
        // without a live heap/capability set.
        internal static List<ExceptionInstance> SelectGen2RetentionCandidates(ExceptionAnalysis analysis)
        {
            var candidates = new List<ExceptionInstance>();
            foreach (var kvp in analysis.ExceptionsByType)
            {
                List<ExceptionInstance> instances = kvp.Value;
                for (int i = 0; i < instances.Count; i++)
                {
                    if (instances[i].Generation >= 2)
                        candidates.Add(instances[i]);
                }
            }
            return candidates;
        }

        // E-1: retention paths for Gen2/LOH exception instances via the heap.root-paths capability
        // (IHeapRootPathQuery). Only runs when the capability is available; the numeric bounds match
        // the pre-retyping analyzer's own RootPathSearchLimits exactly. Bounded by
        // MaxRetentionPathEnrichmentMs — root-path search is real per-object work, unlike the
        // unconditional totals/counts elsewhere in this analyzer.
        internal static IReadOnlyList<ExceptionRetentionPath> BuildGen2RetentionPaths(IHeapRootPathQuery? rootPaths, ExceptionAnalysis analysis, CrashAnalysisOptions options)
        {
            if (rootPaths is null)
                return [];

            List<ExceptionInstance> candidates = SelectGen2RetentionCandidates(analysis);
            if (candidates.Count == 0)
                return [];

            var searchOptions = new RootPathSearchOptions(
                MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

            var budgetSw = System.Diagnostics.Stopwatch.StartNew();
            long maxMs = Math.Max(0, options.MaxRetentionPathEnrichmentMs);
            var results = new List<ExceptionRetentionPath>(Math.Min(candidates.Count, 64));

            for (int i = 0; i < candidates.Count; i++)
            {
                if (budgetSw.ElapsedMilliseconds > maxMs)
                    break;

                ExceptionInstance instance = candidates[i];
                if (!rootPaths.TryFindRootPath(instance.Address, searchOptions, out string? formattedPath, out bool truncated))
                    continue;

                (string rootKind, string path) = SplitRootKind(formattedPath!);
                results.Add(new ExceptionRetentionPath(instance.Type, instance.Address, rootKind, path, truncated));
            }

            return results;
        }

        // IHeapRootPathQuery.TryFindRootPath returns one already-formatted "RootKind: path" string
        // (see its own remarks) — this splits it back into the two columns the report already shows
        // separately, matching RootPathSearchSupport.FormatPath's exact "{rootKind}: {path}" shape.
        private static (string RootKind, string Path) SplitRootKind(string formattedPath)
        {
            int separator = formattedPath.IndexOf(": ", StringComparison.Ordinal);
            return separator >= 0
                ? (formattedPath[..separator], formattedPath[(separator + 2)..])
                : (string.Empty, formattedPath);
        }

        // Crash bucket / fault signature: (ExceptionType, TopUserFrame) dedup key, same sampled
        // scope as BuildMessageDistributions (ExceptionsByType — capped per type, plus all active
        // instances). Distinguishes a systemic single-site fault (one bucket, high InstanceCount)
        // from scattered independent failures (many buckets, low InstanceCount each) that would
        // otherwise look identical in the plain per-type counts.
        internal static IReadOnlyList<CrashBucket> BuildCrashBuckets(ExceptionAnalysis analysis)
        {
            var buckets = new Dictionary<(string Type, string TopUserFrame), CrashBucketAccumulator>();
            foreach (var kvp in analysis.ExceptionsByType)
            {
                string type = kvp.Key;
                List<ExceptionInstance> instances = kvp.Value;
                for (int i = 0; i < instances.Count; i++)
                {
                    ExceptionInstance instance = instances[i];
                    string topUserFrame = DetermineTopUserFrame(instance.OriginalStackTrace);
                    var key = (type, topUserFrame);
                    if (!buckets.TryGetValue(key, out var acc))
                    {
                        acc = new CrashBucketAccumulator(instance.Address);
                        buckets[key] = acc;
                    }
                    acc.InstanceCount++;
                    if (instance.ThreadId.HasValue)
                        acc.ActiveInstanceCount++;
                }
            }

            var result = new List<CrashBucket>(buckets.Count);
            foreach (var kvp in buckets)
                result.Add(new CrashBucket(kvp.Key.Type, kvp.Key.TopUserFrame, kvp.Value.InstanceCount, kvp.Value.ActiveInstanceCount, kvp.Value.SampleAddress));

            result.Sort(static (a, b) => b.InstanceCount.CompareTo(a.InstanceCount));
            return result;
        }

        // First normalized frame that isn't framework/runtime infrastructure — the real
        // originating call site. Falls back to the top frame (or a sentinel) when the whole
        // captured trace is framework code or there is no trace at all.
        private static string DetermineTopUserFrame(List<string> originalStackTrace)
        {
            for (int i = 0; i < originalStackTrace.Count; i++)
            {
                string normalized = NormalizeFrame(originalStackTrace[i]);
                if (!IsFrameworkFrame(normalized))
                    return normalized;
            }

            return originalStackTrace.Count > 0 ? NormalizeFrame(originalStackTrace[0]) : "(no stack trace)";
        }

        // Message distribution per type — derived from the per-type sampled instance set already
        // held in ExceptionsByType (capped at MaxExceptionsPerType, plus all active instances), not
        // a fresh unconditional per-object Message read across the full heap: distinct message
        // counting and stack-trace/inner-exception extraction are the same class of "genuinely
        // expensive per-object work" that cap exists to bound (see CrashAnalysisOptions.MaxExceptionsPerType).
        internal static IReadOnlyList<ExceptionMessageDistribution> BuildMessageDistributions(ExceptionAnalysis analysis)
        {
            var distributions = new List<ExceptionMessageDistribution>(analysis.ExceptionsByType.Count);
            foreach (var kvp in analysis.ExceptionsByType)
            {
                List<ExceptionInstance> instances = kvp.Value;
                var messageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                var activeMessageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                int sampledCount = 0;

                for (int i = 0; i < instances.Count; i++)
                {
                    ExceptionInstance instance = instances[i];
                    if (string.IsNullOrWhiteSpace(instance.Message))
                        continue;

                    sampledCount++;
                    messageCounts.TryGetValue(instance.Message, out int count);
                    messageCounts[instance.Message] = count + 1;

                    if (instance.ThreadId.HasValue)
                    {
                        activeMessageCounts.TryGetValue(instance.Message, out int activeCount);
                        activeMessageCounts[instance.Message] = activeCount + 1;
                    }
                }

                if (sampledCount == 0)
                    continue;

                string? mostCommonMessage = null;
                int mostCommonMessageCount = 0;
                foreach (var mc in messageCounts)
                {
                    if (mc.Value > mostCommonMessageCount)
                    {
                        mostCommonMessage = mc.Key;
                        mostCommonMessageCount = mc.Value;
                    }
                }

                string? mostCommonActiveMessage = null;
                int mostCommonActiveMessageCount = 0;
                foreach (var mc in activeMessageCounts)
                {
                    if (mc.Value > mostCommonActiveMessageCount)
                    {
                        mostCommonActiveMessage = mc.Key;
                        mostCommonActiveMessageCount = mc.Value;
                    }
                }

                distributions.Add(new ExceptionMessageDistribution(
                    kvp.Key,
                    sampledCount,
                    messageCounts.Count,
                    mostCommonMessage,
                    mostCommonMessageCount,
                    mostCommonActiveMessage,
                    mostCommonActiveMessageCount));
            }

            return distributions;
        }

        internal IReadOnlyList<CrashThreadCandidateSnapshot> BuildCrashThreadSnapshotsImpl(ExceptionAnalysis analysis)
        {
            // Flatten all exception instances once so inference loops are O(N) not O(N*K)
            var allInstances = new List<ExceptionInstance>(capacity: 64);
            foreach (var list in analysis.ExceptionsByType.Values)
                allInstances.AddRange(list);

            int inferredCount = 0;
            // Complete candidate list — bounded by distinct thread count, never heap-scale. No
            // report-width cap here (§9.26 D5); the render layer slices for display.
            int take = analysis.CrashThreadCandidates.Count;
            var snapshots = new List<CrashThreadCandidateSnapshot>(take);

            for (int ci = 0; ci < take; ci++)
            {
                var c = analysis.CrashThreadCandidates[ci];
                List<string>? original = null;
                bool inferred = false;
                string? inferredFrom = null;
                var confidence = InferenceConfidence.None;
                bool sourceIsRethrown = false;

                // Tier 1: candidate already has its own original stack (exact)
                if (c.OriginalExceptionStack != null && c.OriginalExceptionStack.Count > 0)
                {
                    original = NormalizeAll(c.OriginalExceptionStack);
                    confidence = InferenceConfidence.Exact;
                    sourceIsRethrown = c.OriginalExceptionStackIsRethrown;
                }

                // Tier 2: match by ThreadId
                if (original == null)
                {
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var e = allInstances[i];
                        if (e.ThreadId.HasValue && e.ThreadId.Value == c.ThreadId && e.OriginalStackTrace.Count > 0)
                        {
                            original = NormalizeAll(e.OriginalStackTrace);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.ThreadId;
                            sourceIsRethrown = e.IsRethrown;
                            break;
                        }
                    }
                }

                // Tier 3: match by Message + HResult
                if (original == null && !string.IsNullOrWhiteSpace(c.SampleMessage))
                {
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var e = allInstances[i];
                        if (!string.IsNullOrWhiteSpace(e.Message)
                            && e.Message == c.SampleMessage
                            && e.HResult == c.SampleHResult
                            && e.OriginalStackTrace.Count > 0)
                        {
                            original = NormalizeAll(e.OriginalStackTrace);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.MessageHResult;
                            sourceIsRethrown = e.IsRethrown;
                            break;
                        }
                    }
                }

                // Tier 4: match by PrimaryExceptionType + InnerExceptionType (last-resort)
                if (original == null)
                {
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var e = allInstances[i];
                        if (e.Type == c.PrimaryExceptionType
                            && e.OriginalStackTrace.Count > 0
                            && (!string.IsNullOrWhiteSpace(c.SampleInnerExceptionType)
                                ? e.InnerExceptionType == c.SampleInnerExceptionType
                                : e.InnerExceptionType == null))
                        {
                            original = NormalizeAll(e.OriginalStackTrace);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.TypeInnerType;
                            sourceIsRethrown = e.IsRethrown;
                            break;
                        }
                    }
                }

                if (inferred) inferredCount++;

                // A rethrown source's top frames are the rethrow site, not the original throw
                // site — lower confidence one tier to reflect that, regardless of which tier
                // matched (even an Exact match is less trustworthy if the trace itself is stale).
                if (sourceIsRethrown)
                    confidence = DowngradeForRethrow(confidence);

                // Build full frames list without LINQ — no report-width cap here (§9.26 D5).
                var topFrames = new List<string>(c.CurrentThreadStack.Count);
                string? topUserFrameModule = null;
                bool foundUserFrame = false;
                for (int f = 0; f < c.CurrentThreadStack.Count; f++)
                {
                    ThreadStackFrameRef frame = c.CurrentThreadStack[f];
                    string normalizedFrame = NormalizeFrame(FrameDisplayText(frame));
                    topFrames.Add(normalizedFrame);

                    if (!foundUserFrame && !IsFrameworkFrame(normalizedFrame))
                    {
                        topUserFrameModule = frame.HasMethod ? frame.ModuleName : null;
                        foundUserFrame = true;
                    }
                }

                snapshots.Add(new CrashThreadCandidateSnapshot(
                    c.ThreadId,
                    c.OSThreadId,
                    c.ActiveExceptionCount,
                    c.PrimaryExceptionType,
                    topFrames,
                    original,
                    inferred,
                    inferredFrom,
                    confidence,
                    sourceIsRethrown,
                    topUserFrameModule));
            }

            analysis.InferredTraceCount = inferredCount;
            return snapshots;
        }

        private static string FrameDisplayText(ThreadStackFrameRef frame) =>
            frame.HasMethod ? (frame.RawMethodSignature ?? frame.MethodDisplayName) : (frame.FrameName ?? StringConstants.UnknownType);

        // Normalize every frame (strips "at " prefix, simplifies async names) — no report-width
        // cap here (§9.26 D5); the render layer slices for display.
        // One tier worse, in the Exact→ThreadId→MessageHResult→TypeInnerType→None quality order.
        // TypeInnerType is already the lowest non-None tier, so it stays put rather than
        // collapsing all the way to None — the match itself is still valid, just the frame text
        // it points to is a rethrow site rather than the original throw site.
        private static InferenceConfidence DowngradeForRethrow(InferenceConfidence confidence) => confidence switch
        {
            InferenceConfidence.Exact => InferenceConfidence.ThreadId,
            InferenceConfidence.ThreadId => InferenceConfidence.MessageHResult,
            InferenceConfidence.MessageHResult => InferenceConfidence.TypeInnerType,
            _ => confidence,
        };

        private static List<string> NormalizeAll(List<string> frames)
        {
            var result = new List<string>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
                result.Add(NormalizeFrame(frames[i]));
            return result;
        }

        // Normalize a single raw stack frame string:
        //  - strips leading "   at " or "at " prefix (we re-add it during rendering)
        //  - simplifies async state-machine names: Foo+<Bar>d__N.MoveNext() → Foo.Bar() [async]
        //  - simplifies lambda names: Foo+<>c.<Bar>b__N_M() → Foo.Bar [lambda]
        internal static string NormalizeFrame(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            ReadOnlySpan<char> s = raw.AsSpan().TrimStart();

            // Strip "at " prefix (added by ToString() or _stackTraceString)
            if (s.Length > 3 && s[0] == 'a' && s[1] == 't' && s[2] == ' ')
                s = s[3..].TrimStart();

            string frame = s.ToString();

            // Async state machine: SomeNamespace.TypeName+<AsyncMethod>d__N.MoveNext()
            int plus = frame.IndexOf('+');
            if (plus > 0 && plus < frame.Length - 1)
            {
                ReadOnlySpan<char> rest = frame.AsSpan(plus + 1);
                if (rest.StartsWith("<", StringComparison.Ordinal))
                {
                    // Find the closing >
                    int close = frame.IndexOf('>', plus + 2);
                    if (close > plus + 2)
                    {
                        string methodName = frame[(plus + 2)..close];
                        ReadOnlySpan<char> suffix = frame.AsSpan(close + 1);

                        if (suffix.StartsWith("d__", StringComparison.Ordinal) || suffix.StartsWith(">d__", StringComparison.Ordinal))
                        {
                            // Async: TypeName.MethodName() [async]
                            string typePart = frame[..plus];
                            return $"{typePart}.{methodName}() [async]";
                        }

                        if (suffix.StartsWith(">c.<", StringComparison.Ordinal) || (rest.StartsWith("<>c.<", StringComparison.Ordinal)))
                        {
                            // Lambda: TypeName.MethodName [lambda]
                            string typePart = frame[..plus];
                            return $"{typePart}.{methodName} [lambda]";
                        }
                    }
                }
            }

            return frame;
        }

        // Returns true if a (normalized) frame belongs to a runtime/framework namespace
        internal static bool IsFrameworkFrame(string frame)
        {
            return frame.StartsWith("System.", StringComparison.Ordinal)
                || frame.StartsWith("Microsoft.", StringComparison.Ordinal)
                || frame.StartsWith("mscorlib.", StringComparison.Ordinal)
                || frame.StartsWith("Windows.", StringComparison.Ordinal)
                || frame.Contains("System.Runtime.", StringComparison.Ordinal)
                || frame.Contains("System.Threading.", StringComparison.Ordinal);
        }

        private IReadOnlyList<ExceptionInstanceSnapshot> BuildExceptionInstanceSnapshots(ExceptionAnalysis analysis)
        {
            // Collect and sort without LINQ SelectMany+OrderBy allocation chain
            var flat = new List<(string Type, ExceptionInstance Instance)>(capacity: 64);
            foreach (var kvp in analysis.ExceptionsByType)
                for (int i = 0; i < kvp.Value.Count; i++)
                    flat.Add((kvp.Key, kvp.Value[i]));

            flat.Sort(static (a, b) =>
            {
                // Active first, then most frames, then has message
                int cmp = (b.Instance.ThreadId.HasValue ? 1 : 0).CompareTo(a.Instance.ThreadId.HasValue ? 1 : 0);
                if (cmp != 0) return cmp;
                cmp = b.Instance.OriginalStackTrace.Count.CompareTo(a.Instance.OriginalStackTrace.Count);
                if (cmp != 0) return cmp;
                return (string.IsNullOrWhiteSpace(b.Instance.Message) ? 0 : 1)
                    .CompareTo(string.IsNullOrWhiteSpace(a.Instance.Message) ? 0 : 1);
            });

            // Complete list — already bounded by MaxExceptionsPerType upstream, never heap-scale.
            // No further report-width cap here (§9.26 D5); the render layer slices for display.
            int limit = flat.Count;
            var snapshots = new List<ExceptionInstanceSnapshot>(limit);

            for (int i = 0; i < limit; i++)
            {
                var (typeName, inst) = flat[i];

                List<string>? threadFrames = null;
                if (inst.CurrentThreadStack.Count > 0)
                {
                    threadFrames = new List<string>(inst.CurrentThreadStack.Count);
                    for (int f = 0; f < inst.CurrentThreadStack.Count; f++)
                        threadFrames.Add(NormalizeFrame(FrameDisplayText(inst.CurrentThreadStack[f])));
                }

                List<string>? origFrames = null;
                if (inst.OriginalStackTrace.Count > 0)
                    origFrames = NormalizeAll(inst.OriginalStackTrace);

                snapshots.Add(new ExceptionInstanceSnapshot(
                    typeName,
                    inst.Address,
                    string.IsNullOrWhiteSpace(inst.Message) ? null : inst.Message,
                    inst.HResult == 0 ? null : inst.HResult,
                    inst.InnerExceptionType,
                    inst.ChainDepth,
                    inst.ThreadId.HasValue,
                    inst.ThreadId,
                    inst.OSThreadId,
                    threadFrames,
                    origFrames,
                    inst.AggregateInnerExceptionTypes,
                    inst.IsRethrown));
            }

            return snapshots;
        }

        public void Dispose() { }
    }
}
