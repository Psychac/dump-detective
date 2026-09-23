using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Detects long-lived objects reachable only through a static field, ranked by exclusive retained
/// bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠ KNOWN, PRE-EXISTING ACCURACY BUG — this analyzer currently produces zero findings on every
/// real dump, and this retyping does not fix that.</b> The root cause lives upstream, in
/// <c>RootIndexWriter</c>/<c>IHeapAnalysisCache.GetStaticRootedAddresses</c> (which
/// <see cref="Sdk.Analysis.IHeapRootQuery"/>'s dump-side implementation still delegates to): it
/// filters for <c>ClrRootKind</c> values 9/10, which don't exist in ClrMD 4.0.722401 or 3.1.512801
/// — statics aren't reported as GC roots at all in ClrMD 3+, only reachable by walking
/// <c>ClrType.StaticFields</c> per AppDomain. A verified fix exists on branch
/// <c>fix/static-root-detection</c> (commit <c>4d7e267b</c>) but is deliberately unmerged: switching
/// this analyzer on for the first time costs ~453s / 63.7 GB allocated on a 3.3 GB dump (uncapped,
/// per this project's own "no sampling" convention), and that cost needs to be addressed before
/// merging, not after — see <c>docs/cache/backlog.md</c>'s first entry. This retyping mechanically
/// ports the analyzer's *consumption* logic onto the SDK capability surface, preserving today's real
/// (broken) behavior exactly, so it is unblocked and ready the moment the upstream fix lands — it
/// does not attempt the fix or its prerequisite perf work itself.
/// </para>
/// <para>
/// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). No
/// bespoke scan participant needed — like <c>ModuleAnalyzer</c>, this analyzer iterates a bounded
/// root population (<see cref="IHeapRootQuery.EnumerateRoots"/>), not a heap-index scan.
/// <see cref="IHeapRootQuery.EnumerateRoots"/>'s <c>HeapRootKind.StaticVar</c> entries, with their
/// already-resolved <c>OwnerTypeName</c>/<c>FieldName</c>/<c>AppDomainId</c> (built for
/// <c>GCRootAnalyzer</c>'s Batch 3 retyping), replace the pre-retyping analyzer's own
/// <c>GetOrBuildRootTriples</c>/<c>GetStaticRootedAddresses</c>/<c>GetStaticFieldsByRootAddress</c>
/// combined — this analyzer is that surface's second real consumer.
/// </para>
/// <para>
/// <see cref="IHeapDominatorQuery.EnumerateRetainedObjects"/> (new, uncapped — first real consumer)
/// replaces <c>IDominatorTreeProvider.EnumerateRetainedSet</c> directly for the per-type/namespace
/// breakdown; <see cref="IHeapDominatorQuery.TryGetRetainedSize"/> (already existed) replaces
/// <c>TryGetRetainedBytes</c> for the exact total. <see cref="IDumpObjectFieldQuery.HasDelegateField"/>
/// (new Tier-2 primitive) replaces the pre-retyping analyzer's own <c>HasDelegateFields</c> field
/// walk. <b>Dropped, not ported</b>: <c>RetainedSizeCandidateSelector.RequiresWalk</c>'s shape
/// pre-check — the same accepted simplification <c>GCRootAnalyzer</c>'s and <c>MemoryAnalyzer</c>'s
/// own batches already made for the identical <c>MethodTableHasOutgoingRefs</c> pre-filter: since
/// <see cref="IHeapDominatorQuery.EnumerateRetainedObjects"/> must stay uncapped anyway, skipping the
/// pre-check costs at most one trivial single-object iteration for a true leaf root and changes no
/// output value.
/// </para>
/// </remarks>
[AnalyzerModule(key: "static-root", displayName: "Static Root Leak Detection", order: 250, tags: ["roots", "leaks"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapDominators,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapRoots,
    CapabilityVocabulary.HeapSegments,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceAllocSamples)]
public sealed class StaticRootAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "Static Root Leak Detection";
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

    private static readonly RootPathSearchOptions RootPathOptions = new(
        MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapRootQuery heapRoots = context.HeapRoots
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRoots}' capability.");
        IHeapDominatorQuery? dominators = context.HeapDominators;
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");
        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");

        var options = context.AnalyzerOptions as StaticRootLeakAnalysisOptions ?? new StaticRootLeakAnalysisOptions();

        LastResult = await Analyze(heapRoots, dominators, objectLookup, segments, rootPaths, fields, typeStatistics, options, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>heap.static-root</c> observation per analyzed static root, inline in
    /// <see cref="AnalyzeStaticRoots"/> where <c>rootObj.Type</c> is still in scope. Deliberately
    /// does not fix this analyzer's own known, documented bug (its own class remarks) —
    /// <see cref="IHeapRootQuery.EnumerateRoots"/> never actually yields
    /// <see cref="HeapRootKind.StaticVar"/> entries on ClrMD 4, so this code path exists correctly
    /// for when the upstream fix lands, not to change today's (broken, zero-finding) behavior.
    /// </summary>
    private static async ValueTask<StaticRootDomainResult> Analyze(
        IHeapRootQuery heapRoots, IHeapDominatorQuery? dominators, IHeapObjectLookup objectLookup, IHeapSegmentQuery segments,
        IHeapRootPathQuery rootPaths, IDumpObjectFieldQuery fields, IHeapTypeStatisticsQuery typeStatistics,
        StaticRootLeakAnalysisOptions options, AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        List<StaticRootAnalysis> allStaticRootAnalysis = await AnalyzeStaticRoots(heapRoots, dominators, objectLookup, segments, fields, observationContext, cancellationToken);

        StaticRootAnalysis[] significantStaticRoots = allStaticRootAnalysis.Where(a => IsSignificant(a, options)).ToArray();

        StaticRootSnapshot[] topRoots = allStaticRootAnalysis
            .OrderByDescending(r => r.TotalMemoryImpact)
            .Select(r => BuildSnapshot(rootPaths, r, cancellationToken))
            .ToArray();

        ulong totalManagedHeapBytes = GetTotalManagedBytes(typeStatistics);

        ulong totalImpact = 0;
        foreach (StaticRootAnalysis item in significantStaticRoots)
            totalImpact += item.TotalMemoryImpact;

        // Phase 5 step 2: whole-run scalars over the *significant* subset — heap.static-root above
        // covers every analyzed root, significant or not, so RootCount/TotalRetainedBytes (scoped to
        // IsSignificant, a config-threshold filter no observation carries) can't be reconstructed by
        // counting/summing that stream. Emitted unconditionally, including the zero-significant-
        // roots case, since both scalars are exhaustively computed either way. The severity check's
        // topRoots[0].TotalMemoryImpact needs no new field: it's the max heap.static-root
        // retained.bytes across *all* analyzed roots by construction (a root large enough to be the
        // overall max is always >= SignificantMemoryThresholdBytes, hence always significant).
        await observationContext.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "static-root.population",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["root.count"] = new Measure(significantStaticRoots.Length, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["retained.bytes"] = new Measure(totalImpact, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = observationContext.ArtifactId!.Value,
                AnalyzerKey = "Static Root Leak Detection",
                CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapRoots, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments },
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

        if (significantStaticRoots.Length == 0)
            return new StaticRootDomainResult(0, 0, topRoots, totalManagedHeapBytes);

        return new StaticRootDomainResult(significantStaticRoots.Length, totalImpact, topRoots, totalManagedHeapBytes);
    }

    /// <summary>Sum of every type's <c>TotalSize</c> across the whole heap — does not depend on the
    /// broken static-root-detection path, so it stays a real, verifiable figure even while
    /// <see cref="RootCount"/>-adjacent fields read zero.</summary>
    private static ulong GetTotalManagedBytes(IHeapTypeStatisticsQuery typeStatistics)
    {
        ulong total = 0;
        foreach (HeapTypeStatistics entry in typeStatistics.GetTypeStatistics().Values)
            total += entry.TotalSize;
        return total;
    }

    private static StaticRootSnapshot BuildSnapshot(IHeapRootPathQuery rootPaths, StaticRootAnalysis analysis, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool found = rootPaths.TryFindRootPath(analysis.DirectObjectAddress, RootPathOptions, out string? formattedPath, out bool searchTruncated);
        var evidence = new Evidence(
            analysis.TotalMemoryImpact,
            found ? formattedPath : null,
            searchTruncated,
            [new EvidenceSignal("ObjectsKeptAlive", "Objects kept alive by this root", analysis.ObjectsKeptAlive)]);

        return new StaticRootSnapshot(
            FormatHelper.TruncateString(analysis.RootDescription, 90),
            analysis.TotalMemoryImpact,
            analysis.ObjectsKeptAlive,
            analysis.DirectObjectType,
            evidence,
            analysis.TopRetainedTypes,
            analysis.ScanWasCapped,
            analysis.ContainsCollections,
            analysis.ContainsEventHandlers,
            analysis.AssemblyLoadContextInfo,
            analysis.Gen2OrLohRetainedFraction,
            analysis.TopRetainedNamespaces,
            analysis.DirectObjectSize);
    }

    // ClrMD treats Large, Pinned, Frozen segments as gen2-equivalent for collection purposes;
    // anything at or above Gen2 counts as long-lived retention here.
    private static bool IsGen2OrLarger(HeapGenerationTag tag) =>
        tag is HeapGenerationTag.Gen2 or HeapGenerationTag.Loh or HeapGenerationTag.Poh or HeapGenerationTag.Frozen;

    private static bool IsSignificant(StaticRootAnalysis analysis, StaticRootLeakAnalysisOptions options) =>
        analysis.TotalMemoryImpact >= options.SignificantMemoryThresholdBytes
        || analysis.ObjectsKeptAlive >= options.SignificantObjectCountThreshold;

    private static async ValueTask<List<StaticRootAnalysis>> AnalyzeStaticRoots(
        IHeapRootQuery heapRoots, IHeapDominatorQuery? dominators, IHeapObjectLookup objectLookup, IHeapSegmentQuery segments,
        IDumpObjectFieldQuery fields, AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        var results = new List<StaticRootAnalysis>();
        var processedRoots = new HashSet<ulong>();
        var observationCapabilities = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapRoots, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments,
        };

        foreach (HeapRootRef root in heapRoots.EnumerateRoots())
        {
            if (root.Kind != HeapRootKind.StaticVar)
                continue;

            ulong rootAddress = root.TargetAddress;
            if (rootAddress == 0 || !processedRoots.Add(rootAddress))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            if (!objectLookup.TryGetObject(rootAddress, out HeapObjectRef rootObj))
                continue;

            int objectsKeptAlive;
            ulong totalSize;
            List<RetainedTypeInfo> topRetainedTypes;
            List<RetainedNamespaceInfo> topRetainedNamespaces;
            bool containsCollections = false;
            bool containsEventHandlers = false;
            double gen2OrLohRetainedFraction;

            if (dominators is null || !dominators.TryGetRetainedSize(rootAddress, out totalSize))
            {
                // No Stage B dominator tree for this session -- shallow size is the only figure
                // available, matching the pre-retyping analyzer's own no-tree-provider fallback.
                objectsKeptAlive = 1;
                totalSize = rootObj.Size;
                topRetainedTypes = [new RetainedTypeInfo { TypeName = rootObj.TypeDisplayName, Count = 1, TotalSize = rootObj.Size }];
                topRetainedNamespaces = [new RetainedNamespaceInfo { Namespace = TypeFilterHelper.GetNamespace(rootObj.TypeDisplayName), Count = 1, TotalSize = rootObj.Size }];
                gen2OrLohRetainedFraction = IsGen2OrLarger(segments.GetGeneration(rootAddress)) ? 1.0 : 0.0;
            }
            else
            {
                var typeStats = new Dictionary<string, RetainedTypeInfo>(StringComparer.Ordinal);
                var namespaceStats = new Dictionary<string, RetainedNamespaceInfo>(StringComparer.Ordinal);
                var namespaceByTypeName = new Dictionary<string, string>(StringComparer.Ordinal);

                int count = 0;
                ulong gen2OrLohBytes = 0;

                foreach (HeapObjectRef obj in dominators.EnumerateRetainedObjects(rootAddress))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;

                    if (!typeStats.TryGetValue(obj.TypeDisplayName, out RetainedTypeInfo? info))
                        typeStats[obj.TypeDisplayName] = info = new RetainedTypeInfo { TypeName = obj.TypeDisplayName };
                    info.Count++;
                    info.TotalSize += obj.Size;

                    if (!namespaceByTypeName.TryGetValue(obj.TypeDisplayName, out string? ns))
                        namespaceByTypeName[obj.TypeDisplayName] = ns = TypeFilterHelper.GetNamespace(obj.TypeDisplayName);

                    if (!namespaceStats.TryGetValue(ns, out RetainedNamespaceInfo? nsInfo))
                        namespaceStats[ns] = nsInfo = new RetainedNamespaceInfo { Namespace = ns };
                    nsInfo.Count++;
                    nsInfo.TotalSize += obj.Size;

                    if (!containsCollections && TypeFilterHelper.IsCollectionType(obj.TypeDisplayName))
                        containsCollections = true;

                    if (!containsEventHandlers && fields.HasDelegateField(obj.Address))
                        containsEventHandlers = true;

                    if (IsGen2OrLarger(segments.GetGeneration(obj.Address)))
                        gen2OrLohBytes += obj.Size;
                }

                objectsKeptAlive = count;
                topRetainedTypes = GetTopRetainedTypes(typeStats);
                topRetainedNamespaces = GetTopRetainedNamespaces(namespaceStats);
                gen2OrLohRetainedFraction = totalSize > 0 ? gen2OrLohBytes / (double)totalSize : 0.0;
            }

            string? alcInfo = null;
            string rootDescription;

            if (root.OwnerTypeName is not null && root.FieldName is not null)
            {
                rootDescription = $"{root.OwnerTypeName}.{root.FieldName}";
                if (root.AppDomainId is int domainId && domainId != 1)
                {
                    alcInfo = $"AppDomain#{domainId}";
                    rootDescription += $" [{alcInfo}]";
                }
            }
            else
            {
                rootDescription = $"StaticVar @ 0x{rootAddress:X}";
            }

            results.Add(new StaticRootAnalysis
            {
                RootDescription = rootDescription,
                DirectObjectAddress = rootAddress,
                DirectObjectType = rootObj.TypeDisplayName,
                DirectObjectSize = rootObj.Size,
                TotalMemoryImpact = totalSize,
                ObjectsKeptAlive = objectsKeptAlive,
                TopRetainedTypes = topRetainedTypes,
                TopRetainedNamespaces = topRetainedNamespaces,
                ContainsCollections = containsCollections,
                ContainsEventHandlers = containsEventHandlers,
                ScanWasCapped = false,
                AssemblyLoadContextInfo = alcInfo,
                Gen2OrLohRetainedFraction = gen2OrLohRetainedFraction,
            });

            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.static-root",
                Subjects = [rootObj.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["retained.bytes"] = new Measure(totalSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["objects-kept-alive.count"] = new Measure(objectsKeptAlive, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2-or-loh-retained.fraction"] = new Measure(gen2OrLohRetainedFraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Static Root Leak Detection",
                    CapabilitiesUsed = observationCapabilities,
                    Fidelity = dominators is not null ? FidelityLevel.Full : FidelityLevel.Degraded,
                },
                Confidence = dominators is not null ? 1.0 : 0.5,
            }, cancellationToken);

            // Phase 5 step 6.2 (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md):
            // StaticRootFinalizableCorrelationInsightRule needs, per root, which types it retains —
            // heap.static-root above subjects on the root *object's own* type, not the (bounded)
            // TopRetainedTypes breakdown, which had no observation of its own. No root EntityKind
            // exists in the SDK yet (ReportSystemVision.md §4.1's root entity is aspirational, not
            // shipped), so this subjects on the retained TypeRef (joinable against
            // finalizer.queue-entry by type name) and carries rootDescription as narrative-only
            // Evidence — the same "Evidence, not a new entity kind" call thread.stack-cluster's
            // Signature and thread.blocked's lock-object-types already made. Bounded by
            // TopRetainedTypes' own existing cap, not a new unbounded emission.
            foreach (RetainedTypeInfo typeInfo in topRetainedTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string canonicalName, MatchFidelity typeFidelity) = EntityCanonicalizer.CanonicalizeTypeName(typeInfo.TypeName);

                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "heap.static-root-retained-type",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = typeFidelity }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["instance.count"] = new Measure(typeInfo.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["shallow.bytes"] = new Measure(typeInfo.TotalSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    },
                    Evidence = [new EvidenceRef { Artifact = observationContext.ArtifactId!.Value, Locator = "root-description", Description = rootDescription }],
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "Static Root Leak Detection",
                        CapabilitiesUsed = observationCapabilities,
                        Fidelity = dominators is not null ? FidelityLevel.Full : FidelityLevel.Degraded,
                    },
                    Confidence = dominators is not null ? 1.0 : 0.5,
                }, cancellationToken);
            }
        }

        return results;
    }

    private static List<RetainedTypeInfo> GetTopRetainedTypes(Dictionary<string, RetainedTypeInfo> typeStats)
    {
        var list = new List<RetainedTypeInfo>(typeStats.Values);
        list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
        return list;
    }

    private static List<RetainedNamespaceInfo> GetTopRetainedNamespaces(Dictionary<string, RetainedNamespaceInfo> namespaceStats)
    {
        var list = new List<RetainedNamespaceInfo>(namespaceStats.Values);
        list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
        return list;
    }

    public void Dispose() { }
}

internal sealed class StaticRootAnalysis
{
    public string RootDescription { get; set; } = string.Empty;
    public ulong DirectObjectAddress { get; set; }
    public string DirectObjectType { get; set; } = string.Empty;
    public ulong DirectObjectSize { get; set; }
    public ulong TotalMemoryImpact { get; set; }
    public int ObjectsKeptAlive { get; set; }
    public List<RetainedTypeInfo> TopRetainedTypes { get; set; } = new();
    public List<RetainedNamespaceInfo> TopRetainedNamespaces { get; set; } = new();
    public bool ContainsCollections { get; set; }
    public bool ContainsEventHandlers { get; set; }
    public bool ScanWasCapped { get; set; }
    public string? AssemblyLoadContextInfo { get; set; }
    public double Gen2OrLohRetainedFraction { get; set; }
}
