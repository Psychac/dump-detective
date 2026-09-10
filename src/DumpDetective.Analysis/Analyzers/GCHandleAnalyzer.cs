using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

// DumpDetective.Analysis.Models and DumpDetective.Sdk.Analysis both declare HeapSegmentKind
// (deliberately identical names, see SdkSegmentKindMapper) — alias the SDK one to disambiguate.
using SdkHeapSegmentKind = DumpDetective.Sdk.Analysis.HeapSegmentKind;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing handles
/// through <see cref="IHeapHandleQuery"/> (<c>heap.handles</c>), retained-byte estimates through
/// <see cref="IHeapDominatorQuery"/> (optional — null when Stage B wasn't built), per-address
/// SOH/generation classification through <see cref="IHeapSegmentQuery"/>, and object metadata
/// (dependent-handle source/target resolution, pinned-handle shallow size) through
/// <see cref="IHeapObjectLookup"/>. Runs through the existing pipeline via
/// <see cref="GCHandleAnalyzerLegacyAdapter"/>.
/// </summary>
public sealed class GCHandleAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "GC Handle Analysis";
    public string Category => "Handles";

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapHandleQuery handleQuery = context.HeapHandles
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapHandles}' capability.");
        IHeapSegmentQuery segmentQuery = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");

        GCHandleAnalysisOptions options = context.AnalyzerOptions as GCHandleAnalysisOptions ?? new GCHandleAnalysisOptions();

        // context.HeapDominators is deliberately not required — null means Stage B wasn't built for
        // this run, and every retained-bytes consumer below degrades to the target's own shallow
        // size in that case, exactly like the pre-retyping analyzer's own null-treeProvider path.
        LastResult = Analyze(handleQuery, context.HeapDominators, segmentQuery, objectLookup, options, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static GCHandleDomainResult Analyze(
        IHeapHandleQuery handleQuery,
        IHeapDominatorQuery? dominatorQuery,
        IHeapSegmentQuery segmentQuery,
        IHeapObjectLookup objectLookup,
        GCHandleAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        int pinnedExactCount = 0, pinnedFallbackCount = 0;
        int asyncPinnedExactCount = 0, asyncPinnedFallbackCount = 0;

        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var pinnedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        // P2-2: RefCounted handles back COM interop (RCW) lifetime; concentration by target type
        // surfaces COM object leaks that would otherwise be invisible in the handle count.
        var refCountedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        // P2-4: individual pinned-handle addresses for debugger follow-up. Bounded by pinned handle
        // count (already the scope of the existing per-handle byte resolution above), not heap
        // object count, so collecting the full set before ranking is cheap.
        var pinnedHandleAddresses = new List<PinnedHandleAddressEntry>();
        var pinnedBytesByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var asyncPinnedBytesByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var allTargetTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var nullTargetHandlesByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        ulong totalPinnedRetainedBytes = 0;
        ulong totalAsyncPinnedRetainedBytes = 0;
        // P2-1: SOH targets keep the GC from compacting around them; LOH/POH/Frozen targets don't
        // (LOH is never compacted, POH objects are already pinned by construction), so only the SOH
        // count signals an actionable compaction barrier.
        int pinnedSohObjectCount = 0;
        int pinnedNonSohObjectCount = 0;
        int asyncPinnedSohObjectCount = 0;
        int asyncPinnedNonSohObjectCount = 0;
        // P3-2: WeakShort clears when the target becomes unreachable, even mid-finalization;
        // WeakLong clears only after finalization completes. A WeakLong population concentrated in
        // Gen2/LOH can indicate a finalization backlog (targets lingering, weakly-referenced,
        // waiting for their finalizer to run).
        int weakShortGen0Count = 0, weakShortGen1Count = 0, weakShortGen2Count = 0, weakShortLohCount = 0;
        int weakLongGen0Count = 0, weakLongGen1Count = 0, weakLongGen2Count = 0, weakLongLohCount = 0;

        int totalHandles = 0;
        int strongLikeHandles = 0;
        int weakLikeHandles = 0;
        // Never increments — ResolveTypeNameFromRecord's contract (see IHeapHandleQuery.HeapHandleRef's
        // own remarks) guarantees a non-empty TargetTypeDisplayName for any non-zero TargetAddress,
        // so this branch is unreachable. Preserved as a pre-existing, always-zero metric rather than
        // "fixed" during a retyping-only change.
        int unknownTargetCount = 0;

        int dependentHandleCount = 0;
        int dependentResolvedEdgeCount = 0;
        int dependentUnresolvedTargetCount = 0;
        var dependentSourceTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependentTargetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependentSourceTargetPairCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        int handlesProcessed = 0;
        foreach (HeapHandleRef handle in handleQuery.EnumerateHandles())
        {
            handlesProcessed++;
            if ((handlesProcessed % 1000) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            totalHandles++;
            string kind = handle.Kind.ToString();
            Increment(byKind, kind);

            if (IsWeakLike(kind))
                weakLikeHandles++;
            else
                strongLikeHandles++;

            bool isDependent = kind == "Dependent";
            if (isDependent)
                dependentHandleCount++;

            // P1-3: Track null-target handles per kind
            if (handle.TargetAddress == 0)
            {
                Increment(nullTargetHandlesByKind, kind);
                if (isDependent)
                    dependentUnresolvedTargetCount++;
                continue;
            }

            string typeName = handle.TargetTypeDisplayName;
            Increment(allTargetTypes, typeName);

            // P1-2: Separate AsyncPinned vs Pinned byte accounting
            if (kind == "AsyncPinned")
            {
                if (TryIsSoh(segmentQuery, handle.TargetAddress, out bool isSohAsync))
                {
                    if (isSohAsync) asyncPinnedSohObjectCount++;
                    else asyncPinnedNonSohObjectCount++;
                }

                ulong resolvedSize;
                if (dominatorQuery is not null && dominatorQuery.TryGetRetainedSize(handle.TargetAddress, out ulong exactAsyncPinnedBytes))
                {
                    resolvedSize = exactAsyncPinnedBytes;
                    asyncPinnedExactCount++;
                }
                else
                {
                    resolvedSize = ResolveSize(objectLookup, handle.TargetAddress);
                    asyncPinnedFallbackCount++;
                }

                if (resolvedSize > 0)
                {
                    totalAsyncPinnedRetainedBytes += resolvedSize;
                    asyncPinnedBytesByType[typeName] = asyncPinnedBytesByType.TryGetValue(typeName, out ulong existingBytes) ? existingBytes + resolvedSize : resolvedSize;
                    pinnedHandleAddresses.Add(new PinnedHandleAddressEntry(handle.TargetAddress, typeName, resolvedSize, kind));
                }
            }
            else if (kind == "Pinned")
            {
                Increment(pinnedTypes, typeName);

                if (TryIsSoh(segmentQuery, handle.TargetAddress, out bool isSohPinned))
                {
                    if (isSohPinned) pinnedSohObjectCount++;
                    else pinnedNonSohObjectCount++;
                }

                ulong resolvedSize;
                if (dominatorQuery is not null && dominatorQuery.TryGetRetainedSize(handle.TargetAddress, out ulong exactPinnedBytes))
                {
                    resolvedSize = exactPinnedBytes;
                    pinnedExactCount++;
                }
                else
                {
                    resolvedSize = ResolveSize(objectLookup, handle.TargetAddress);
                    pinnedFallbackCount++;
                }

                if (resolvedSize > 0)
                {
                    totalPinnedRetainedBytes += resolvedSize;
                    pinnedBytesByType[typeName] = pinnedBytesByType.TryGetValue(typeName, out ulong existingBytes) ? existingBytes + resolvedSize : resolvedSize;
                    pinnedHandleAddresses.Add(new PinnedHandleAddressEntry(handle.TargetAddress, typeName, resolvedSize, kind));
                }
            }
            else if (kind == "RefCounted")
            {
                Increment(refCountedTypes, typeName);
            }
            else if (isDependent)
            {
                if (!TryResolveDependentTypeName(objectLookup, handle.TargetAddress, out string sourceType))
                {
                    dependentUnresolvedTargetCount++;
                }
                else
                {
                    Increment(dependentSourceTypeCounts, sourceType);

                    ulong dependentTarget = handle.DependentTargetAddress ?? 0;
                    if (dependentTarget == 0 || !TryResolveDependentTypeName(objectLookup, dependentTarget, out string dependentTargetType))
                    {
                        dependentUnresolvedTargetCount++;
                    }
                    else
                    {
                        dependentResolvedEdgeCount++;
                        Increment(dependentTargetTypeCounts, dependentTargetType);
                        Increment(dependentSourceTargetPairCounts, $"{sourceType} -> {dependentTargetType}");
                    }
                }
            }
            else if (kind == "WeakShort" || kind == "WeakLong")
            {
                // P3-2: generation breakdown for weak handle targets. Loh/Poh/Frozen all fold into
                // the single "Loh" catch-all bucket, matching the pre-retyping analyzer's own
                // ResolveGeneration-based "else" bucket.
                switch (segmentQuery.GetGeneration(handle.TargetAddress))
                {
                    case HeapGenerationTag.Unknown:
                        break; // Unresolvable segment — no bucket to attribute to.
                    case HeapGenerationTag.Gen0:
                        if (kind == "WeakShort") weakShortGen0Count++; else weakLongGen0Count++;
                        break;
                    case HeapGenerationTag.Gen1:
                        if (kind == "WeakShort") weakShortGen1Count++; else weakLongGen1Count++;
                        break;
                    case HeapGenerationTag.Gen2:
                        if (kind == "WeakShort") weakShortGen2Count++; else weakLongGen2Count++;
                        break;
                    default: // Loh, Poh, Frozen
                        if (kind == "WeakShort") weakShortLohCount++; else weakLongLohCount++;
                        break;
                }
            }
        }

        int pinnedHandleTargets = 0;
        foreach (int v in pinnedTypes.Values) pinnedHandleTargets += v;
        int refCountedHandleCount = 0;
        foreach (int v in refCountedTypes.Values) refCountedHandleCount += v;
        double dependentUnresolvedPercent = dependentHandleCount == 0 ? 0
            : dependentUnresolvedTargetCount * 100.0 / dependentHandleCount;

        // P2-4: rank the exact set of collected pinned-handle addresses by bytes, keep top N for display.
        pinnedHandleAddresses.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        int topPinnedAddressCount = Math.Min(options.TopPinnedHandleAddressesToShow, pinnedHandleAddresses.Count);
        var topPinnedHandleAddresses = pinnedHandleAddresses.GetRange(0, topPinnedAddressCount);

        return new GCHandleDomainResult(
            totalHandles,
            strongLikeHandles,
            weakLikeHandles,
            pinnedHandleTargets,
            ToRankedEntries(byKind),
            ToRankedEntries(allTargetTypes),
            ToRankedEntries(pinnedTypes),
            totalPinnedRetainedBytes,
            ToRankedByteEntries(pinnedBytesByType),
            totalAsyncPinnedRetainedBytes,
            ToRankedByteEntries(asyncPinnedBytesByType),
            ToRankedEntries(nullTargetHandlesByKind),
            unknownTargetCount,
            dependentHandleCount,
            dependentResolvedEdgeCount,
            dependentUnresolvedTargetCount,
            dependentUnresolvedPercent,
            ToRankedEntries(dependentSourceTypeCounts),
            ToRankedEntries(dependentTargetTypeCounts),
            ToRankedEntries(dependentSourceTargetPairCounts),
            PinnedRetainedBytesIsExact: pinnedExactCount > 0 && pinnedFallbackCount == 0,
            AsyncPinnedRetainedBytesIsExact: asyncPinnedExactCount > 0 && asyncPinnedFallbackCount == 0,
            PinnedSohObjectCount: pinnedSohObjectCount,
            PinnedNonSohObjectCount: pinnedNonSohObjectCount,
            AsyncPinnedSohObjectCount: asyncPinnedSohObjectCount,
            AsyncPinnedNonSohObjectCount: asyncPinnedNonSohObjectCount,
            RefCountedHandleCount: refCountedHandleCount,
            TopRefCountedTargetTypes: ToRankedEntries(refCountedTypes),
            TopPinnedHandleAddresses: topPinnedHandleAddresses,
            WeakShortGen0Count: weakShortGen0Count,
            WeakShortGen1Count: weakShortGen1Count,
            WeakShortGen2Count: weakShortGen2Count,
            WeakShortLohCount: weakShortLohCount,
            WeakLongGen0Count: weakLongGen0Count,
            WeakLongGen1Count: weakLongGen1Count,
            WeakLongGen2Count: weakLongGen2Count,
            WeakLongLohCount: weakLongLohCount,
            TotalHandlesWarningThreshold: options.TotalHandlesWarningThreshold,
            PinnedHandleTargetsWarningThreshold: options.PinnedHandleTargetsWarningThreshold,
            PinnedRetainedBytesWarningThreshold: options.PinnedRetainedBytesWarningThreshold,
            PinnedSohObjectCountWarningThreshold: options.PinnedSohObjectCountWarningThreshold,
            RefCountedHandleCountWarningThreshold: options.RefCountedHandleCountWarningThreshold,
            WeakLongGen2FractionWarningThreshold: options.WeakLongGen2FractionWarningThreshold,
            WeakLongGen2MinimumCountThreshold: options.WeakLongGen2MinimumCountThreshold,
            DependentUnresolvedPercentWarningThreshold: options.DependentUnresolvedPercentWarningThreshold);
    }

    private static bool IsWeakLike(string kind) => kind.Contains("Weak", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves whether <paramref name="address"/> lives on the small object heap via a
    /// single segment lookup (bounded by pinned-handle count, not heap size).</summary>
    private static bool TryIsSoh(IHeapSegmentQuery segmentQuery, ulong address, out bool isSoh)
    {
        if (!segmentQuery.TryGetSegment(address, out HeapSegmentRef segment))
        {
            isSoh = false;
            return false;
        }

        isSoh = segment.Kind == SdkHeapSegmentKind.SmallObjectHeap;
        return true;
    }

    private static ulong ResolveSize(IHeapObjectLookup objectLookup, ulong address) =>
        objectLookup.TryGetObject(address, out HeapObjectRef obj) ? obj.Size : 0;

    /// <summary>
    /// Dependent-handle source/target resolution — deliberately not a plain
    /// <see cref="IHeapObjectLookup.TryGetObject"/> passthrough: that method still returns
    /// <c>true</c> (with a placeholder name) when the resolved method table is 0, but the
    /// pre-retyping analyzer's own <c>TryResolveTypeNameStrict</c> treated a zero method table as
    /// unresolved — preserved here exactly, since it changes whether an edge counts toward
    /// <see cref="GCHandleDomainResult.DependentResolvedEdgeCount"/> or
    /// <see cref="GCHandleDomainResult.DependentUnresolvedTargetCount"/>.
    /// </summary>
    private static bool TryResolveDependentTypeName(IHeapObjectLookup objectLookup, ulong address, out string typeName)
    {
        typeName = "Unknown";
        if (address == 0)
            return false;

        if (!objectLookup.TryGetObject(address, out HeapObjectRef obj))
            return false;

        if (obj.Type.MethodTable is null or 0)
            return false;

        typeName = obj.TypeDisplayName;
        return true;
    }

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out int value) ? value + 1 : 1;

    private static List<NameCountEntry> ToRankedEntries(Dictionary<string, int> source)
    {
        var list = new List<NameCountEntry>(source.Count);
        foreach (var kvp in source)
            list.Add(new NameCountEntry(kvp.Key, kvp.Value));
        list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        return list;
    }

    private static List<NameBytesEntry> ToRankedByteEntries(Dictionary<string, ulong> source)
    {
        var list = new List<NameBytesEntry>(source.Count);
        foreach (var kvp in source)
            list.Add(new NameBytesEntry(kvp.Key, kvp.Value));
        list.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    public void Dispose() { }
}
