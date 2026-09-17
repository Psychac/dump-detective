using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// For the top-N largest types on the heap, finds a sample instance's path back to a GC root (the
/// <c>!gcroot</c> workflow), reporting whether/how each type is retained, root-kind distribution,
/// shared-retention hubs, and (E-2) exact retained bytes when a dominator tree is available.
/// </summary>
/// <remarks>
/// Tier-1-only batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — every
/// ClrMD touch point here is coarse/structural (type statistics, root enumeration, reference
/// traversal), never a live field *value* read, same classification <c>DominatorAnalyzer</c>'s own
/// batch reached.
/// <para>
/// <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/> already carries <c>SampleAddress</c> per
/// type, replacing the pre-retyping analyzer's separate <c>cache.GetSampleInstanceAddress</c> call.
/// <see cref="IHeapDominatorQuery.TryGetRetainedSize"/> (already existed) replaces
/// <c>IDominatorTreeProvider.TryGetRetainedBytes</c> for E-2's per-sample exact retained bytes.
/// <see cref="IHeapRootQuery.EnumerateRoots"/> — already carrying resolved
/// <c>OwnerTypeName</c>/<c>FieldName</c>/<c>AppDomainId</c> for static/thread-static roots (built for
/// <c>GCRootAnalyzer</c>'s retyping) — replaces E-3's <c>cache.GetOrBuildRootTriples</c>/
/// <c>GetStaticFieldsByRootAddress</c> combination outright: no per-storage-address lookup needed,
/// just a filter over already-resolved roots. <see cref="IHeapObjectStream"/> (added for
/// <c>DominatorAnalyzer</c>'s retyping) is reused as-is for E-7's multi-sample streaming collection,
/// its second real consumer.
/// </para>
/// <para>
/// Two narrow SDK additions: <see cref="IHeapRootPathQuery.TryFindRootPathAddresses"/> (raw hop
/// addresses — this analyzer is the first consumer needing them for anything beyond formatting:
/// the shared-root grouping's <c>RootAddress</c>, and as input to the field-name lookup below) and
/// <see cref="IHeapReferenceQuery.TryGetReferenceFieldName"/> (E-3's "last hop field name", the
/// primary WinDbg/SOS parity field — a narrow single-pair lookup, not a general
/// enumerate-with-field-names surface, since resolving one already-found path's last edge is the
/// only real need so far).
/// </para>
/// <para>
/// <b>Dropped, not ported, both confirmed with the user before implementing</b>:
/// </para>
/// <list type="bullet">
/// <item>The force-expand ("known leak type") predicate (<c>ReferenceChainOptions.KnownLeakTypePatterns</c>,
/// pre-retyping <c>IsKnownLeakType</c>) — not a new decision, a re-confirmation of one this codebase
/// already made and documented on <see cref="IHeapRootPathQuery"/> itself during
/// <c>CollectionAnalyzer</c>'s retyping (2026-09-12): wiring this exact predicate through this exact
/// search machinery hung a real 3.35 GB dump's root-path search indefinitely, because
/// force-expanding every List/Dictionary-shaped node (this option's own defaults) defeats the
/// hub-fanout cap almost everywhere in a real heap. <see cref="IHeapRootPathQuery"/> has no
/// force-expand parameter for this reason; see its own remarks for the full trace.</item>
/// <item>Root-priority ordering (pre-retyping <c>SortRootsByPriority</c>/<c>GetRootSearchPriority</c>
/// — tried stack roots before static/handle roots, to report a "faster to resolve" root when an
/// object had several). Traced into <c>BidirectionalGraphSearch</c>/<c>IndexBackedBidirectionalSearch</c>
/// before dropping: on the common, reverse-index-backed path (nearly all real-dump analysis), every
/// root seeds the search simultaneously and the *shortest* path wins — list order has zero effect on
/// the outcome there. It only ever mattered on the uncommon no-reverse-index fallback, which tries
/// roots one at a time and returns the first hit — there, dropping the order means a different,
/// equally-valid root can be reported instead of the old "stack first" pick (itself a hand-picked
/// "fastest to resolve" guess, not a measured "most useful" one). See
/// <see cref="IHeapRootPathQuery"/>'s own remarks for the same trace.</item>
/// <item>Internal-only <c>TelemetryCounters</c> (pruned-node/large-fanout/candidate-set-size/reverse-
/// index-entry counts) — verified never surfaced in <see cref="ReferenceChainDomainResult"/> or any
/// report/section builder, so dropping them has zero observable-contract impact. Application-wide
/// telemetry is a real, separately-tracked future direction, not something this batch should
/// improvise a one-off replacement for.</item>
/// <item>Progress reporting — matches every prior Tier-1 batch (<c>GCRootAnalyzer</c>/
/// <c>MemoryAnalyzer</c>/<c>DominatorAnalyzer</c>), none of which wired <c>context.Progress</c>
/// either.</item>
/// </list>
/// </remarks>
[AnalyzerModule(key: "reference-chain", displayName: "Reference Chain Analysis", order: 260, tags: ["roots"])]
[RequiresCapability(
    CapabilityVocabulary.HeapDominators,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapReferences,
    CapabilityVocabulary.HeapRoots,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceAllocSamples)]
public sealed class ReferenceChainAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult, Pipeline.IRequiresReachableGraphIndex
{
    public string Name => "Reference Chain Analysis";
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

    private readonly record struct PendingTypeSample(
        string TypeName,
        int Count,
        ulong TotalSizeBytes,
        ulong? SampleAddress,
        string? SampleType,
        ulong SampleSize,
        bool HasGcRoot,
        string? RootKind,
        string? RootPath,
        IReadOnlyList<string>? PathHops,
        bool SearchTruncated,
        ulong? RetainedBytes,
        ulong? RootAddress,
        string? LastHopFieldName);

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapObjectStream heapObjects = context.HeapObjects
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapReferenceQuery references = context.HeapReferences
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapReferences}' capability.");
        IHeapRootQuery heapRoots = context.HeapRoots
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRoots}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");
        IHeapDominatorQuery? dominators = context.HeapDominators;

        var options = context.AnalyzerOptions as ReferenceChainOptions ?? new ReferenceChainOptions();

        LastResult = await Analyze(typeStatistics, objectLookup, heapObjects, references, heapRoots, rootPaths, dominators, options, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>type.root-reachability</c> observation per top-by-size type sampled — bounded to
    /// <c>ReferenceChainOptions.TopCount</c>, inline where <c>sample.Type</c> is still in scope.
    /// </summary>
    private static async ValueTask<ReferenceChainDomainResult> Analyze(
        IHeapTypeStatisticsQuery typeStatistics,
        IHeapObjectLookup objectLookup,
        IHeapObjectStream heapObjects,
        IHeapReferenceQuery references,
        IHeapRootQuery heapRoots,
        IHeapRootPathQuery rootPaths,
        IHeapDominatorQuery? dominators,
        ReferenceChainOptions options,
        AnalysisContext observationContext,
        CancellationToken cancellationToken)
    {
        var searchOptions = new RootPathSearchOptions(
            options.MaxCandidateNodes, options.MaxCandidateDepth, options.MaxRootExpansionDepth, options.LargeFanoutThreshold);

        var topTypes = typeStatistics.GetTypeStatistics()
            .OrderByDescending(kvp => kvp.Value.TotalSize)
            .Take(options.TopCount)
            .ToArray();

        int retainedSamples = 0;
        int analyzedSamples = 0;
        int traversalLimitedSamples = 0;
        int noSampleAddressCount = 0;
        var retainedTypeNames = new HashSet<string>(StringComparer.Ordinal);
        var rootKindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sampleReferenceChains = new List<string>(capacity: 5);
        var pendingTypes = new List<PendingTypeSample>(capacity: topTypes.Length);
        // Type display name -> primary sample address, for types with a valid primary sample. Feeds
        // the E-7 multi-sample streaming pass below (dedup key + target-type filter).
        var primaryAddressByTypeName = new Dictionary<string, ulong>(topTypes.Length, StringComparer.Ordinal);
        var observationCapabilities = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapTypes, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapReferences, CapabilityVocabulary.HeapRoots,
        };

        foreach (var typeKvp in topTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string typeName = typeKvp.Key;
            HeapTypeStatistics stats = typeKvp.Value;

            ulong? sampleAddress = stats.SampleAddress != 0 ? stats.SampleAddress : null;
            string? sampleType = null;
            ulong sampleSize = 0;
            bool hasGcRoot = false;
            string? rootKind = null;
            string? path = null;
            IReadOnlyList<string>? pathHops = null;
            bool searchTruncated = false;
            ulong? retainedBytes = null;
            ulong? rootAddress = null;
            string? lastHopFieldName = null;

            if (sampleAddress.HasValue && objectLookup.TryGetObject(sampleAddress.Value, out HeapObjectRef sample))
            {
                analyzedSamples++;
                sampleType = sample.TypeDisplayName;
                sampleSize = sample.Size;

                (hasGcRoot, rootKind, path, pathHops, searchTruncated, rootAddress, lastHopFieldName) =
                    TryFindRootPathDetails(rootPaths, references, objectLookup, sampleAddress.Value, searchOptions);

                if (hasGcRoot)
                {
                    retainedSamples++;
                    retainedTypeNames.Add(typeName);

                    if (rootKind is not null)
                        rootKindCounts[rootKind] = rootKindCounts.GetValueOrDefault(rootKind) + 1;

                    if (!string.IsNullOrWhiteSpace(path) && sampleReferenceChains.Count < 5)
                        sampleReferenceChains.Add($"{typeName}: {path}");
                }
                else if (searchTruncated)
                {
                    traversalLimitedSamples++;
                }

                primaryAddressByTypeName[typeName] = sampleAddress.Value;

                if (dominators is not null && dominators.TryGetRetainedSize(sampleAddress.Value, out ulong exactRetainedBytes))
                    retainedBytes = exactRetainedBytes;

                var measures = new Dictionary<string, Measure>
                {
                    ["sample.bytes"] = new Measure(sampleSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["has-gc-root"] = new Measure(hasGcRoot ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                };
                if (retainedBytes is ulong rb)
                    measures["retained.bytes"] = new Measure(rb, MeasureUnit.Bytes, MeasureSemantics.Absolute);

                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "type.root-reachability",
                    Subjects = [sample.Type],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = measures,
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "Reference Chain Analysis",
                        CapabilitiesUsed = observationCapabilities,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }
            else
            {
                noSampleAddressCount++;
            }

            pendingTypes.Add(new PendingTypeSample(
                typeName, (int)Math.Min(int.MaxValue, stats.InstanceCount), stats.TotalSize, sampleAddress, sampleType, sampleSize,
                hasGcRoot, rootKind, path, pathHops, searchTruncated, retainedBytes, rootAddress, lastHopFieldName));
        }

        Dictionary<string, List<ulong>> additionalSamplesByTypeName = CollectAdditionalSamples(
            heapObjects, primaryAddressByTypeName, options.MultiSampleCount, cancellationToken);

        Dictionary<string, string> rootFieldNamesByTypeName = ResolveRootFieldNames(heapRoots, pendingTypes);

        var topTypeSampleTraces = new List<ReferenceTypeSampleSnapshot>(capacity: pendingTypes.Count);
        foreach (PendingTypeSample pending in pendingTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int sampleCount = 0;
            Dictionary<string, int>? perTypeRootKindCounts = null;

            if (pending.SampleAddress.HasValue)
            {
                sampleCount = 1;
                if (pending.HasGcRoot && pending.RootKind is not null)
                    perTypeRootKindCounts = new Dictionary<string, int>(StringComparer.Ordinal) { [pending.RootKind] = 1 };
            }

            if (additionalSamplesByTypeName.TryGetValue(pending.TypeName, out List<ulong>? extraAddresses))
            {
                foreach (ulong extraAddress in extraAddresses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sampleCount++;

                    (bool extraHasGcRoot, string? extraRootKind, _, _, _, _, _) =
                        TryFindRootPathDetails(rootPaths, references, objectLookup, extraAddress, searchOptions);

                    if (extraHasGcRoot && extraRootKind is not null)
                    {
                        perTypeRootKindCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                        perTypeRootKindCounts[extraRootKind] = perTypeRootKindCounts.GetValueOrDefault(extraRootKind) + 1;
                    }
                }
            }

            string? dominantRootKind = null;
            int dominantRootKindCount = 0;
            int retainedSampleCount = 0;
            if (perTypeRootKindCounts is not null)
            {
                foreach (KeyValuePair<string, int> kv in perTypeRootKindCounts)
                {
                    retainedSampleCount += kv.Value;
                    if (kv.Value > dominantRootKindCount
                        || (kv.Value == dominantRootKindCount && string.CompareOrdinal(kv.Key, dominantRootKind) < 0))
                    {
                        dominantRootKind = kv.Key;
                        dominantRootKindCount = kv.Value;
                    }
                }
            }

            topTypeSampleTraces.Add(new ReferenceTypeSampleSnapshot(
                pending.TypeName,
                pending.Count,
                pending.TotalSizeBytes,
                pending.SampleAddress,
                pending.SampleType,
                pending.SampleSize,
                pending.HasGcRoot,
                pending.RootKind,
                pending.RootPath,
                pending.PathHops,
                pending.SearchTruncated,
                sampleCount,
                retainedSampleCount,
                dominantRootKind,
                dominantRootKindCount,
                pending.RetainedBytes,
                pending.RootAddress,
                rootFieldNamesByTypeName.TryGetValue(pending.TypeName, out string? rootFieldName) ? rootFieldName : null,
                pending.LastHopFieldName));
        }

        List<ReferenceChainSharedRootGroup> sharedRootGroups = BuildSharedRootGroups(topTypeSampleTraces);

        double retainedPct = analyzedSamples == 0 ? 0 : retainedSamples * 100.0 / analyzedSamples;
        var retainedTypeList = retainedTypeNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        var rootKindDistribution = new List<ReferenceChainRootKindCount>(rootKindCounts.Count);
        foreach (KeyValuePair<string, int> kv in rootKindCounts)
            rootKindDistribution.Add(new ReferenceChainRootKindCount(kv.Key, kv.Value));
        rootKindDistribution.Sort(static (a, b) =>
        {
            int byCount = b.RetainedTypeCount.CompareTo(a.RetainedTypeCount);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.RootKind, b.RootKind);
        });

        return new ReferenceChainDomainResult(
            analyzedSamples,
            retainedSamples,
            retainedPct,
            retainedTypeList,
            sampleReferenceChains,
            topTypeSampleTraces,
            traversalLimitedSamples,
            rootKindDistribution,
            noSampleAddressCount,
            sharedRootGroups);
    }

    /// <summary>
    /// Runs one root-path search and projects it into every shape this analyzer's callers need:
    /// the formatted display string and per-hop labels (built here from raw addresses via
    /// <paramref name="objectLookup"/>, the same job the pre-retyping analyzer's own
    /// <c>FormatPath</c>/<c>FormatNodeByAddress</c> did directly against <c>ClrHeap</c>), the root
    /// object's own address (the path's first hop), and the last-hop field name (E-3) resolved via
    /// <see cref="IHeapReferenceQuery.TryGetReferenceFieldName"/> once a path with at least two hops
    /// is found.
    /// </summary>
    private static (bool HasGcRoot, string? RootKind, string? Path, IReadOnlyList<string>? PathHops, bool SearchTruncated, ulong? RootAddress, string? LastHopFieldName)
        TryFindRootPathDetails(
            IHeapRootPathQuery rootPaths, IHeapReferenceQuery references, IHeapObjectLookup objectLookup, ulong objectAddress, RootPathSearchOptions options)
    {
        bool found = rootPaths.TryFindRootPathAddresses(objectAddress, options, out string? rootKind, out IReadOnlyList<ulong>? hopAddresses, out bool searchTruncated);
        if (!found || rootKind is null || hopAddresses is null || hopAddresses.Count == 0)
            return (false, null, null, null, searchTruncated, null, null);

        var pathHops = new List<string>(hopAddresses.Count);
        foreach (ulong address in hopAddresses)
            pathHops.Add(FormatNodeByAddress(objectLookup, address));

        string path = $"{rootKind}: {string.Join(" -> ", pathHops)}";
        ulong rootAddress = hopAddresses[0];

        string? lastHopFieldName = null;
        if (hopAddresses.Count >= 2)
            references.TryGetReferenceFieldName(hopAddresses[^2], hopAddresses[^1], out lastHopFieldName);

        return (true, rootKind, path, pathHops, searchTruncated, rootAddress, lastHopFieldName);
    }

    private static string FormatNodeByAddress(IHeapObjectLookup objectLookup, ulong address)
    {
        string typeName = objectLookup.TryGetObject(address, out HeapObjectRef obj) ? obj.TypeDisplayName : "<invalid>";
        return $"{typeName}@0x{address:X}";
    }

    /// <summary>
    /// E-6 (docs/analysis/phase1/reference-chain-analyzer-audit.md): groups top types whose
    /// representative sample resolved to the same root object address — a shared retention
    /// hub (e.g. a static cache or singleton) rather than independent leaks. Scoped to the
    /// representative sample only, since multi-sample extras (E-1/E-7) don't retain a path.
    /// </summary>
    private static List<ReferenceChainSharedRootGroup> BuildSharedRootGroups(
        IReadOnlyList<ReferenceTypeSampleSnapshot> traces)
    {
        var typeNamesByRootAddress = new Dictionary<ulong, List<string>>();
        var rootKindByRootAddress = new Dictionary<ulong, string>();

        for (int i = 0; i < traces.Count; i++)
        {
            ReferenceTypeSampleSnapshot trace = traces[i];
            if (!trace.RootAddress.HasValue || trace.RootKind is null)
                continue;

            ulong address = trace.RootAddress.Value;
            if (!typeNamesByRootAddress.TryGetValue(address, out List<string>? typeNames))
            {
                typeNames = new List<string>();
                typeNamesByRootAddress[address] = typeNames;
                rootKindByRootAddress[address] = trace.RootKind;
            }

            typeNames.Add(trace.TypeName);
        }

        var groups = new List<ReferenceChainSharedRootGroup>();
        foreach (KeyValuePair<ulong, List<string>> kv in typeNamesByRootAddress)
        {
            if (kv.Value.Count < 2)
                continue;

            groups.Add(new ReferenceChainSharedRootGroup(kv.Key, rootKindByRootAddress[kv.Key], kv.Value));
        }

        groups.Sort(static (a, b) =>
        {
            int byCount = b.TypeNames.Count.CompareTo(a.TypeNames.Count);
            return byCount != 0 ? byCount : a.RootAddress.CompareTo(b.RootAddress);
        });

        return groups;
    }

    /// <summary>
    /// E-3 (docs/analysis/phase1/reference-chain-analyzer-audit.md): resolves the static field
    /// or stack frame owner that holds each retained type's root reference, keyed by type name.
    /// Unlike the pre-retyping analyzer's own <c>cache.GetOrBuildRootTriples</c>-based matching,
    /// <see cref="IHeapRootQuery.EnumerateRoots"/> already carries resolved
    /// <c>OwnerTypeName</c>/<c>FieldName</c>/<c>AppDomainId</c> for static/thread-static roots (built
    /// for <c>GCRootAnalyzer</c>'s retyping) — this is a filter over that stream, not a separate
    /// per-storage-address lookup.
    /// </summary>
    private static Dictionary<string, string> ResolveRootFieldNames(
        IHeapRootQuery heapRoots, IReadOnlyList<PendingTypeSample> pendingTypes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var neededTargets = new HashSet<ulong>();
        foreach (PendingTypeSample pending in pendingTypes)
        {
            if (pending.HasGcRoot && pending.RootAddress.HasValue && pending.RootKind is not null)
                neededTargets.Add(pending.RootAddress.Value);
        }

        if (neededTargets.Count == 0)
            return result;

        var fieldNameByTarget = new Dictionary<ulong, string>();
        foreach (HeapRootRef root in heapRoots.EnumerateRoots())
        {
            if (!neededTargets.Contains(root.TargetAddress) || fieldNameByTarget.ContainsKey(root.TargetAddress))
                continue;

            string? fieldName = root.Kind switch
            {
                HeapRootKind.StaticVar or HeapRootKind.ThreadStaticVar when root.OwnerTypeName is not null && root.FieldName is not null =>
                    root.AppDomainId is int appDomainId && appDomainId != 1
                        ? $"{root.OwnerTypeName}.{root.FieldName} [AppDomain#{appDomainId}]"
                        : $"{root.OwnerTypeName}.{root.FieldName}",
                HeapRootKind.Stack when heapRoots.TryResolveStackFrameOwner(root.RootAddress, out string ownerType, out string methodName) =>
                    $"{ownerType}.{methodName}",
                _ => null,
            };

            if (fieldName is not null)
                fieldNameByTarget[root.TargetAddress] = fieldName;
        }

        if (fieldNameByTarget.Count == 0)
            return result;

        foreach (PendingTypeSample pending in pendingTypes)
        {
            if (pending.RootAddress.HasValue && fieldNameByTarget.TryGetValue(pending.RootAddress.Value, out string? fieldName))
                result[pending.TypeName] = fieldName;
        }

        return result;
    }

    /// <summary>
    /// E-7 (docs/analysis/phase1/reference-chain-analyzer-audit.md): one streaming pass over the
    /// whole heap (<see cref="IHeapObjectStream.EnumerateObjects"/>, which already prefers the
    /// disk-backed index internally when one exists — same single-pass-filtered-by-type idiom
    /// already used by <c>WeakReferenceAnalyzer</c>, <c>TimerLeakAnalyzer</c>,
    /// <c>AsyncStateMachineAnalyzer</c>, and <c>EventLeak/PublisherRegistry</c>) collecting up to
    /// <paramref name="multiSampleCount"/> - 1 additional distinct instances per target type, for
    /// E-1's root-consistency scoring. Deliberately not a per-type API: a per-type call in the main
    /// top-N loop would re-stream the whole heap once per type. Exits early once every target type
    /// has reached quota.
    /// </summary>
    private static Dictionary<string, List<ulong>> CollectAdditionalSamples(
        IHeapObjectStream heapObjects,
        Dictionary<string, ulong> primaryAddressByTypeName,
        int multiSampleCount,
        CancellationToken cancellationToken)
    {
        var additionalByTypeName = new Dictionary<string, List<ulong>>(primaryAddressByTypeName.Count, StringComparer.Ordinal);

        int perTypeQuota = multiSampleCount - 1;
        if (perTypeQuota <= 0 || primaryAddressByTypeName.Count == 0)
            return additionalByTypeName;

        int remainingTargets = primaryAddressByTypeName.Count;

        foreach (HeapObjectRef obj in heapObjects.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!primaryAddressByTypeName.TryGetValue(obj.TypeDisplayName, out ulong primaryAddress) || obj.Address == primaryAddress)
                continue;

            if (!additionalByTypeName.TryGetValue(obj.TypeDisplayName, out List<ulong>? list))
            {
                list = new List<ulong>(perTypeQuota);
                additionalByTypeName[obj.TypeDisplayName] = list;
            }

            if (list.Count >= perTypeQuota)
                continue;

            list.Add(obj.Address);
            if (list.Count == perTypeQuota)
            {
                remainingTargets--;
                if (remainingTargets <= 0)
                    break;
            }
        }

        return additionalByTypeName;
    }
}
