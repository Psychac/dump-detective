using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Threads;

/// <summary>
/// Phase 1 retyping batch, thread-domain quartet item 1
/// (docs/refactor/modularity/phase-1-thread-quartet-plan.md): retyped onto the SDK's
/// capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing sync blocks through
/// <see cref="IHeapSyncBlockQuery"/> (<c>runtime.locks</c>), object type names through
/// <see cref="IHeapObjectLookup"/>, and threads/stack frames through <see cref="IRuntimeThreadQuery"/>.
/// Runs through the existing pipeline via <see cref="LockGraphAnalyzerLegacyAdapter"/>, which also
/// carries the <c>IThreadStackScanParticipant</c> implementation this analyzer's stack-frame reads
/// rely on the pipeline sharing across the whole thread-domain quartet — see that adapter's own
/// remarks and the plan doc's § 3 for why that lives on the adapter, not here.
/// </summary>
/// <remarks>
/// Re-verified during this retyping (not just grepped, per this project's own convention): this
/// analyzer reads no live object field values anywhere — <c>heap.EnumerateSyncBlocks()</c>,
/// per-address type-name resolution, and thread/frame facts are all coarse/structural. It was
/// originally miscategorized as Tier 1 + Tier 2 in the plan's own grep-derived split; corrected
/// there once this was confirmed.
/// </remarks>
[AnalyzerModule(key: "lock-graph", displayName: "Lock Graph Analysis", order: 320, tags: ["threads", "locks"])]
[RequiresCapability(
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.RuntimeLocks,
    CapabilityVocabulary.RuntimeThreads)]
[OptionalCapability(CapabilityVocabulary.TraceContentionEvents)]
public sealed class LockGraphAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult, IThreadStackScanParticipant, ICustomizesAnalysisContext
{
    private readonly ThreadStackScanState _scanState = new();

    int IThreadStackScanParticipant.GetRequiredFrameCount(RuntimeAnalysisContext context) => FrameScanDepth;
    void IThreadStackScanParticipant.BeforeThreadStackScan(RuntimeAnalysisContext context) => _scanState.BeforeThreadStackScan();
    void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot) => _scanState.OnThreadStack(in snapshot, stackRootCount: 0);
    void IThreadStackScanParticipant.OnThreadStackScanCompleted(bool succeeded) => _scanState.OnThreadStackScanCompleted(succeeded);

    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
        AnalysisContextBuilder.Build(context, analyzerOptions: null, runtimeThreadsOverride: _scanState.BuildPrecomputedQuery());

    // A thread physically blocked inside Monitor.Enter/Wait has one or more native "Runtime"
    // transition frames (no Method, empty signature) ahead of the actual managed
    // Monitor.Enter_Slowpath/ObjWait frame — verified via a live self-attach snapshot of a real
    // blocked thread. Frame index 0 alone is almost always one of those native frames, so this
    // scans up to FrameScanDepth frames for the first one with a resolvable Method signature.
    internal const int FrameScanDepth = 8;

    // How many of a deadlock candidate's own resolvable frames to capture for the report. Reuses
    // the same up-to-FrameScanDepth frame set the pipeline's shared scan already captured rather
    // than a second independent walk (the pre-retyping analyzer's own CaptureOwnerThreadFrames did
    // walk again, unbounded) — accepted, documented narrowing: if fewer than 3 of a candidate's
    // first 8 frames resolve to a method signature, this returns fewer than 3 instead of continuing
    // to walk further down the stack. Deadlock candidates are inherently rare and FrameScanDepth
    // was already chosen as "enough to find one resolvable frame in virtually all real stacks", so
    // finding 3 within the same 8 is the same bet, not a new one.
    private const int OwnerFrameCaptureCount = 3;

    public string Name => "Lock Graph Analysis";
    public string Category => "Locks";

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

        IHeapSyncBlockQuery syncBlockQuery = context.HeapSyncBlocks
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeLocks}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
        IRuntimeThreadQuery threadQuery = context.RuntimeThreads
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreads}' capability.");

        LastResult = await Analyze(syncBlockQuery, objectLookup, threadQuery, context, cancellationToken);
    }

    private readonly record struct LockEntry(ulong ObjectAddress, string TypeName, RuntimeThreadRef? Owner, int RecursionCount, int WaitingThreadCount);

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): emits
    /// <c>lock.contested</c> (one per contested lock, inline where <c>cl.Owner</c>'s real
    /// <see cref="ThreadRef"/> is still in scope — a second pass over
    /// <see cref="LockGraphDomainResult.ContestedLockDetails"/> would only have the owner's managed
    /// id, not a full <see cref="ThreadRef"/>) and <c>thread.blocked</c> (one per deadlock candidate
    /// — named for the doc's own canonical example in
    /// observation-and-correlation-model.md § 4's correlation-recipes table, deliberately not
    /// <c>deadlock.candidate</c>: "deadlock" is itself the judgment a synthesis rule reaches, not a
    /// raw fact this analyzer observed).
    /// </summary>
    private static async ValueTask<LockGraphDomainResult> Analyze(
        IHeapSyncBlockQuery syncBlockQuery,
        IHeapObjectLookup objectLookup,
        IRuntimeThreadQuery threadQuery,
        AnalysisContext observationContext,
        CancellationToken cancellationToken)
    {
        var observationCapabilities = new HashSet<Capability>
        {
            CapabilityVocabulary.RuntimeLocks, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.RuntimeThreads,
        };
        var threadsByOsId = new Dictionary<uint, RuntimeThreadRef>();
        foreach (RuntimeThreadRef t in threadQuery.EnumerateThreads())
            threadsByOsId[t.Thread.OsThreadId] = t;

        var allHeldLocks = new List<LockEntry>();
        var contestedLocks = new List<LockEntry>();
        int unresolvedOwnerCount = 0;
        int locksWithOwnerAddress = 0;

        foreach (HeapSyncBlockRef sb in syncBlockQuery.EnumerateSyncBlocks())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!sb.IsMonitorHeld || sb.ObjectAddress == 0)
                continue;

            string typeName = objectLookup.TryGetObject(sb.ObjectAddress, out HeapObjectRef obj) ? obj.TypeDisplayName : "Unknown";

            if (sb.HasHoldingThread)
                locksWithOwnerAddress++;

            RuntimeThreadRef? owner = sb.HoldingOsThreadId is uint osThreadId && threadsByOsId.TryGetValue(osThreadId, out RuntimeThreadRef ownerRef)
                ? ownerRef
                : null;

            if (owner is null && sb.HasHoldingThread)
                unresolvedOwnerCount++;

            var entry = new LockEntry(sb.ObjectAddress, typeName, owner, sb.RecursionCount, sb.WaitingThreadCount);
            allHeldLocks.Add(entry);

            if (sb.WaitingThreadCount > 0)
                contestedLocks.Add(entry);
        }

        contestedLocks.Sort((a, b) => b.WaitingThreadCount.CompareTo(a.WaitingThreadCount));

        // Pre-build lock-by-owner-thread map to avoid O(M×N) lookup in the deadlock scan below.
        var locksByOwnerManagedId = new Dictionary<int, List<LockEntry>>();
        foreach (LockEntry lockEntry in allHeldLocks)
        {
            if (lockEntry.Owner is RuntimeThreadRef owner && owner.Thread.ManagedThreadId is int managedId)
            {
                if (!locksByOwnerManagedId.TryGetValue(managedId, out List<LockEntry>? lockList))
                {
                    lockList = new List<LockEntry>();
                    locksByOwnerManagedId[managedId] = lockList;
                }
                lockList.Add(lockEntry);
            }
        }

        // Deadlock candidates: threads that own at least one inflated lock AND are blocked on a monitor.
        var ownerManagedIds = new HashSet<int>(locksByOwnerManagedId.Keys);
        var deadlockCandidates = new List<DeadlockCandidateSnapshot>();

        foreach (RuntimeThreadRef thread in threadsByOsId.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!thread.IsAlive || thread.LockCount == 0)
                continue;
            if (thread.Thread.ManagedThreadId is not int managedThreadId || !ownerManagedIds.Contains(managedThreadId))
                continue;

            string? topFrameSignature = null;
            int scanned = 0;
            foreach (ThreadStackFrameRef frame in threadQuery.EnumerateStackFrames(thread))
            {
                if (frame.HasMethod && !string.IsNullOrEmpty(frame.MethodDisplayName))
                {
                    topFrameSignature = frame.MethodDisplayName;
                    break;
                }
                if (++scanned >= FrameScanDepth)
                    break;
            }

            if (topFrameSignature is null)
                continue;
            if (!topFrameSignature.Contains("monitor.wait", StringComparison.OrdinalIgnoreCase) &&
                !topFrameSignature.Contains("monitor.enter", StringComparison.OrdinalIgnoreCase))
                continue;

            List<LockEntry> locksHeld = locksByOwnerManagedId.TryGetValue(managedThreadId, out List<LockEntry>? locks) ? locks : [];
            var lockTypes = new List<string>(locksHeld.Count);
            var lockAddresses = new List<ulong>(locksHeld.Count);
            foreach (LockEntry lh in locksHeld)
            {
                lockTypes.Add(lh.TypeName);
                lockAddresses.Add(lh.ObjectAddress);
            }

            string summary = $"Thread {managedThreadId} (OS: {thread.Thread.OsThreadId}) holds {locksHeld.Count} lock(s), blocked at: {topFrameSignature}";
            List<string> ownerFrames = CaptureFrames(threadQuery, thread, OwnerFrameCaptureCount);

            deadlockCandidates.Add(new DeadlockCandidateSnapshot(
                (uint)managedThreadId, thread.Thread.OsThreadId, lockTypes, lockAddresses, summary, ownerFrames));

            // Phase 5 step 6.2 (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md):
            // ClusterHangCorrelationInsightRule needs the held lock type names for its narrative
            // ("holding lock(s) on: X, Y") — the original only ever joined them into prose, never
            // filtered/scored on the list, so Evidence (not a new Subjects/observation shape) is
            // enough, mirroring how thread.stack-cluster already carries its Signature.
            var lockTypeEvidence = new List<EvidenceRef>(lockTypes.Count == 0 ? 0 : 1);
            if (lockTypes.Count > 0)
            {
                lockTypeEvidence.Add(new EvidenceRef
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    Locator = "lock-object-types",
                    Description = string.Join(", ", new HashSet<string>(lockTypes, StringComparer.Ordinal)),
                });
            }

            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "thread.blocked",
                Subjects = [thread.Thread],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["locks-held.count"] = new Measure(locksHeld.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Evidence = lockTypeEvidence,
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Lock Graph Analysis",
                    CapabilitiesUsed = observationCapabilities,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        var typeWaiters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LockEntry cl in contestedLocks)
            typeWaiters[cl.TypeName] = typeWaiters.TryGetValue(cl.TypeName, out int existing) ? existing + cl.WaitingThreadCount : cl.WaitingThreadCount;
        var topContestedTypes = new List<NameCountEntry>(typeWaiters.Count);
        foreach (KeyValuePair<string, int> kvp in typeWaiters)
            topContestedTypes.Add(new NameCountEntry(kvp.Key, kvp.Value));
        topContestedTypes.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        var contestedLockDetails = new List<ContestedLockSnapshot>(contestedLocks.Count);
        foreach (LockEntry cl in contestedLocks)
        {
            uint? ownerManagedId = cl.Owner is RuntimeThreadRef owner && owner.Thread.ManagedThreadId is int mid ? (uint)mid : null;
            contestedLockDetails.Add(new ContestedLockSnapshot(cl.ObjectAddress, cl.TypeName, cl.WaitingThreadCount, ownerManagedId, cl.RecursionCount));

            (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(cl.TypeName);
            var subjects = new List<EntityRef> { new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity } };
            if (cl.Owner is RuntimeThreadRef ownerThread)
                subjects.Add(ownerThread.Thread);

            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "lock.contested",
                Subjects = subjects,
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["waiting.count"] = new Measure(cl.WaitingThreadCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["recursion.count"] = new Measure(cl.RecursionCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Lock Graph Analysis",
                    CapabilitiesUsed = observationCapabilities,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        // Phase 5 step 1: the only scalar HangFindingGenerator's replacement reads that isn't
        // already reconstructible by counting matched lock.contested/thread.blocked observations
        // (both fully emitted above, one per entry, never capped) — total held locks includes
        // uncontested ones, which get no per-lock observation at all.
        await observationContext.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "runtime.lock-pressure",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["held-lock.count"] = new Measure(allHeldLocks.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = observationContext.ArtifactId!.Value,
                AnalyzerKey = "Lock Graph Analysis",
                CapabilitiesUsed = observationCapabilities,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

        return new LockGraphDomainResult(
            allHeldLocks.Count,
            contestedLocks.Count,
            contestedLocks.Count > 0 ? contestedLocks[0].WaitingThreadCount : 0,
            deadlockCandidates.Count,
            unresolvedOwnerCount,
            locksWithOwnerAddress,
            topContestedTypes,
            deadlockCandidates,
            contestedLockDetails);
    }

    private static List<string> CaptureFrames(IRuntimeThreadQuery threadQuery, RuntimeThreadRef thread, int maxFrames)
    {
        var frames = new List<string>(maxFrames);
        if (!thread.IsAlive)
            return frames;

        foreach (ThreadStackFrameRef frame in threadQuery.EnumerateStackFrames(thread))
        {
            if (frames.Count >= maxFrames)
                break;
            if (frame.HasMethod && !string.IsNullOrEmpty(frame.MethodDisplayName))
                frames.Add(frame.MethodDisplayName);
        }
        return frames;
    }

    public void Dispose() { }
}
