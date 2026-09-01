using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public class ReferenceChainAnalyzer : IAnalyzer, IRequiresReachableGraphIndex
    {
        private readonly record struct ObjectMetadata(bool IsValid, string? TypeName, ulong Size, ulong MethodTable);

        /// <summary>Per-type state carried from the primary-sample pass to the multi-sample pass
        /// (E-1, docs/analysis/phase1/reference-chain-analyzer-audit.md).</summary>
        private readonly record struct PendingTypeSample(
            string TypeName,
            int Count,
            ulong TotalSizeBytes,
            ulong? SampleAddress,
            string? SampleType,
            ulong SampleSize,
            ulong MethodTable,
            bool HasGcRoot,
            string? RootKind,
            string? RootPath,
            IReadOnlyList<string>? PathHops,
            bool SearchTruncated,
            ulong? RetainedBytes,
            ulong? RootAddress,
            string? LastHopFieldName);

        public string Name => "Reference Chain Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReferenceChainOptions options = context.AnalysisOptions.ReferenceChain;

            return ValueTask.FromResult(AnalyzeTopTypes(context.Heap, context.Cache, options, context.Progress, cancellationToken).Stamp(this));
        }

        internal AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options)
        {
            return AnalyzeTopTypes(heap, cache, options, progress: null, CancellationToken.None);
        }

        private AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            int topCount = options.TopCount;

            // Use cached type statistics instead of re-enumerating
            progress?.Report(new(0, "building type index"));
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            var topTypes = typeStats
                .OrderByDescending(kvp => kvp.Value.TotalSize)
                .Take(topCount)
                .ToArray();

            int retainedSamples = 0;
            int analyzedSamples = 0;
            int traversalLimitedSamples = 0;
            int noSampleAddressCount = 0;
            var retainedTypeNames = new HashSet<string>(StringComparer.Ordinal);
            var rootKindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sampleReferenceChains = new List<string>(capacity: 5);
            var pendingTypes = new List<PendingTypeSample>(capacity: topTypes.Length);
            // MethodTable -> primary sample address, for types with a valid primary sample. Feeds
            // the E-7 multi-sample streaming pass below (dedup key + target-type filter).
            var primaryAddressByMt = new Dictionary<ulong, ulong>(topTypes.Length);
            progress?.Report(new(0, "loading root list"));
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            // Sort retaining roots by likelihood of early hit (Stack first). Sorted once, reused
            // for all top-N type samples.
            List<(string RootKind, ulong Address)> prioritizedRoots = SortRootsByPriority(roots);

            var telemetry = new TelemetryCounters();
            int typeIndex = 0;

            // Create ReferenceGraph once, shared across all top-N type iterations.
            // This preserves the edge cache across iterations, reducing redundant ClrMD calls
            // for objects referenced by multiple types.
            var provider = new ReferenceGraph(heap);

            // E-2 (docs/analysis/phase1/reference-chain-analyzer-audit.md): exact retained-subgraph
            // bytes per type's representative sample, from the disk-backed dominator tree when
            // available — a memory-mapped point lookup, not a new BFS. Null when unavailable (see
            // IDominatorTreeProvider docs); SampleObjectSize remains the shallow-size fallback.
            IDominatorTreeProvider? dominatorTreeProvider = cache.TryGetDominatorTreeProvider();

            foreach (var typeKvp in topTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                typeIndex++;
                progress?.Report(new(analyzedSamples, "tracing reference chains", $"{typeIndex}/{topTypes.Length} types"));
                string typeName = typeKvp.Key;
                var stats = typeKvp.Value;

                // Use cached sample instance instead of full heap walk
                ulong? sampleAddress = cache.GetSampleInstanceAddress(typeName);
                string? sampleType = null;
                ulong sampleSize = 0;
                ulong methodTable = 0;
                bool hasGcRoot = false;
                string? rootKind = null;
                string? path = null;
                IReadOnlyList<string>? pathHops = null;
                bool searchTruncated = false;
                ulong? retainedBytes = null;
                ulong? rootAddress = null;
                string? lastHopFieldName = null;

                if (sampleAddress.HasValue)
                {
                    ObjectMetadata sampleMetadata = GetObjectMetadata(heap, sampleAddress.Value);
                    if (sampleMetadata.IsValid)
                    {
                        analyzedSamples++;
                        sampleType = sampleMetadata.TypeName ?? StringConstants.UnknownType;
                        sampleSize = sampleMetadata.Size;
                        methodTable = sampleMetadata.MethodTable;

                        hasGcRoot = TryFindAnyRootPath(heap, provider, prioritizedRoots, sampleAddress.Value, options, telemetry, cache.TryGetReverseIndexProvider(), cache, cancellationToken, out rootKind, out path, out pathHops, out searchTruncated, out rootAddress, out lastHopFieldName);
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

                        if (methodTable != 0)
                            primaryAddressByMt[methodTable] = sampleAddress.Value;

                        if (dominatorTreeProvider is not null
                            && dominatorTreeProvider.TryGetRetainedBytes(sampleAddress.Value, out ulong exactRetainedBytes))
                        {
                            retainedBytes = exactRetainedBytes;
                        }
                    }
                }
                else
                {
                    noSampleAddressCount++;
                }

                pendingTypes.Add(new PendingTypeSample(
                    typeName, stats.Count, stats.TotalSize, sampleAddress, sampleType, sampleSize,
                    methodTable, hasGcRoot, rootKind, path, pathHops, searchTruncated, retainedBytes, rootAddress,
                    lastHopFieldName));
            }

            Dictionary<ulong, List<ulong>> additionalSamplesByMt = CollectAdditionalSamples(
                heap, cache, primaryAddressByMt, options.MultiSampleCount, progress, cancellationToken);

            Dictionary<string, string> rootFieldNamesByTypeName = ResolveRootFieldNames(heap, cache, pendingTypes);

            var topTypeSampleTraces = new List<ReferenceTypeSampleSnapshot>(capacity: pendingTypes.Count);
            foreach (PendingTypeSample pending in pendingTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int sampleCount = 0;
                var perTypeRootKindCounts = (Dictionary<string, int>?)null;

                if (pending.SampleAddress.HasValue)
                {
                    sampleCount = 1;
                    if (pending.HasGcRoot && pending.RootKind is not null)
                    {
                        perTypeRootKindCounts = new Dictionary<string, int>(StringComparer.Ordinal) { [pending.RootKind] = 1 };
                    }
                }

                if (pending.MethodTable != 0 && additionalSamplesByMt.TryGetValue(pending.MethodTable, out List<ulong>? extraAddresses))
                {
                    foreach (ulong extraAddress in extraAddresses)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sampleCount++;

                        bool extraHasGcRoot = TryFindAnyRootPath(
                            heap, provider, prioritizedRoots, extraAddress, options, telemetry,
                            cache.TryGetReverseIndexProvider(), cache, cancellationToken,
                            out string? extraRootKind, out _, out _, out _, out _, out _);

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
            var retainedTypeList = retainedTypeNames
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

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
        /// <see cref="RootPathFinder"/>/<see cref="Traversal.BidirectionalGraphSearch"/> only ever
        /// track the rooted <em>object's</em> address (<see cref="ReferenceTypeSampleSnapshot.RootAddress"/>),
        /// never which specific root (field/stack slot) — collapsed by design: <c>BidirectionalGraphSearch</c>'s
        /// forward frontier seeds one entry per target address via <c>TryAdd</c>, so two distinct
        /// roots pointing at the same object are already indistinguishable before this method ever
        /// runs. This does one filtered pass over <see cref="IHeapAnalysisCache.GetOrBuildRootTriples"/>
        /// (the same pattern <c>StaticRootLeakDetector</c> already uses) to correlate each retained
        /// type's root-target address back to a matching root's own storage address — filtered to
        /// just the target addresses this run actually needs, not sized to the full root population.
        /// </summary>
        private static Dictionary<string, string> ResolveRootFieldNames(
            ClrHeap heap, IHeapAnalysisCache cache, IReadOnlyList<PendingTypeSample> pendingTypes)
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

            var storageAddressByTarget = new Dictionary<ulong, ulong>(neededTargets.Count);
            foreach ((string _, ulong targetAddr, ulong rootAddr) in cache.GetOrBuildRootTriples(heap))
            {
                if (neededTargets.Contains(targetAddr))
                    storageAddressByTarget.TryAdd(targetAddr, rootAddr);
            }

            if (storageAddressByTarget.Count == 0)
                return result;

            foreach (PendingTypeSample pending in pendingTypes)
            {
                if (!pending.HasGcRoot || pending.RootKind is null || !pending.RootAddress.HasValue)
                    continue;

                if (!storageAddressByTarget.TryGetValue(pending.RootAddress.Value, out ulong storageAddress))
                    continue;

                string? fieldName = pending.RootKind switch
                {
                    "StaticVar" or "ThreadStaticVar" => ResolveStaticFieldName(cache, heap, storageAddress),
                    "Stack" => ResolveStackFrameOwner(cache, heap, storageAddress),
                    _ => null,
                };

                if (fieldName is not null)
                    result[pending.TypeName] = fieldName;
            }

            return result;
        }

        private static string? ResolveStaticFieldName(IHeapAnalysisCache cache, ClrHeap heap, ulong storageAddress)
        {
            var staticFieldsByRootAddress = cache.GetStaticFieldsByRootAddress(heap);
            if (!staticFieldsByRootAddress.TryGetValue(storageAddress, out (string TypeName, string FieldName, int AppDomainId) info))
                return null;

            return info.AppDomainId != 1
                ? $"{info.TypeName}.{info.FieldName} [AppDomain#{info.AppDomainId}]"
                : $"{info.TypeName}.{info.FieldName}";
        }

        private static string? ResolveStackFrameOwner(IHeapAnalysisCache cache, ClrHeap heap, ulong storageAddress)
        {
            return cache.TryResolveStackFrameOwner(heap, storageAddress, out string ownerType, out string methodName)
                ? $"{ownerType}.{methodName}"
                : null;
        }

        private bool TryFindAnyRootPath(
            ClrHeap heap,
            ReferenceGraph provider,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            TelemetryCounters telemetry,
            IBackwardReferenceProvider? reverseIndexProvider,
            IHeapAnalysisCache? cache,
            CancellationToken cancellationToken,
            out string? rootKind,
            out string? path,
            out IReadOnlyList<string>? pathHops,
            out bool searchTruncated,
            out ulong? rootAddress,
            out string? lastHopFieldName)
        {
            rootKind = null;
            path = null;
            pathHops = null;
            searchTruncated = false;
            rootAddress = null;
            lastHopFieldName = null;

            if (!TryGetValidObject(heap, objectAddress, out _))
                return false;

            // Single bounded bidirectional search strategy — the former SearchMode
            // Fast/Balanced/Deep parallel-profile enum was deleted (§9.20); a separate unbounded
            // per-root BFS used to back Fast mode; removed because it scaled with GC root count
            // instead of a shared bounded budget (see docs/analysis/root-path-search-blast-radius.md).
            return TryFindAnyRootPath_Bidirectional(heap, provider, roots, objectAddress, options, telemetry, reverseIndexProvider, cache, cancellationToken, out rootKind, out path, out pathHops, out searchTruncated, out rootAddress, out lastHopFieldName);
        }

        // ── Bidirectional bounded search ─────────────────────────────────────────
        private bool TryFindAnyRootPath_Bidirectional(
            ClrHeap heap,
            ReferenceGraph provider,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            TelemetryCounters telemetry,
            IBackwardReferenceProvider? reverseIndexProvider,
            IHeapAnalysisCache? cache,
            CancellationToken cancellationToken,
            out string? rootKind,
            out string? path,
            out IReadOnlyList<string>? pathHops,
            out bool searchTruncated,
            out ulong? rootAddress,
            out string? lastHopFieldName)
        {
            rootKind = null;
            path = null;
            pathHops = null;
            searchTruncated = false;
            rootAddress = null;
            lastHopFieldName = null;

            // Use shared ReferenceGraph as the reference provider — it caches edges across
            // all types, reducing redundant ClrMD calls for objects referenced by multiple types.
            var limits = new RootPathSearchLimits
            {
                MaxCandidateNodes = options.MaxCandidateNodes,
                MaxCandidateDepth = options.MaxCandidateDepth,
                MaxRootExpansionDepth = options.MaxRootExpansionDepth,
                LargeFanoutThreshold = options.LargeFanoutThreshold,
            };

            var finder = new RootPathFinder(
                heap,
                provider,
                limits,
                telemetry.AsProxy(),
                IsNoisyType,
                type => IsKnownLeakType(type, options.KnownLeakTypePatterns),
                reverseIndexProvider,
                cache);

            bool found = finder.TryFindAnyRootPath(
                objectAddress,
                roots,
                out string? foundRootKind,
                out List<ulong>? addresses,
                out searchTruncated,
                out int candidateSetSize,
                out int reverseIndexEntryCount,
                cancellationToken);

            telemetry.TotalCandidateSetSize += candidateSetSize;
            telemetry.ReverseIndexEntries += reverseIndexEntryCount;

            if (found)
            {
                rootKind = foundRootKind;
                path = FormatPath(heap, foundRootKind!, addresses, out pathHops);
                rootAddress = addresses is { Count: > 0 } ? addresses[0] : null;
                lastHopFieldName = ResolveLastHopFieldName(heap, addresses);
                return true;
            }

            return false;
        }

        /// <summary>
        /// E-3 (docs/analysis/phase1/reference-chain-analyzer-audit.md): the field on the
        /// second-to-last path object that holds the reference to the last hop, via
        /// <see cref="ClrObject.EnumerateReferencesWithFields"/> — the same ClrMD API SOS's
        /// <c>!gcroot</c> draws field names from — filtered to the one entry matching the actual
        /// last-hop address. Null when the path is too short to have a parent (the root points
        /// directly at the target), the parent is no longer a valid object, or the edge was via an
        /// array element/dependent handle rather than a named field.
        /// </summary>
        private static string? ResolveLastHopFieldName(ClrHeap heap, IReadOnlyList<ulong>? addresses)
        {
            if (addresses is not { Count: >= 2 })
                return null;

            ulong parentAddress = addresses[^2];
            ulong childAddress = addresses[^1];

            if (!TryGetValidObject(heap, parentAddress, out ClrObject parent))
                return null;

            foreach (ClrReference reference in parent.EnumerateReferencesWithFields(carefully: true))
            {
                if (reference.Object.Address == childAddress && reference.IsField && reference.Field is not null)
                    return reference.Field.Name;
            }

            return null;
        }

        private static string FormatPath(ClrHeap heap, string rootKind, IReadOnlyList<ulong>? addresses, out IReadOnlyList<string>? pathHops)
        {
            pathHops = null;

            if (addresses is null || addresses.Count == 0)
                return $"{rootKind}: <no path>";

            var parts = new List<string>(addresses.Count);
            for (int i = 0; i < addresses.Count; i++)
            {
                parts.Add(FormatNodeByAddress(heap, addresses[i]));
            }
            pathHops = parts;
            string chain = string.Join(" -> ", parts);
            return $"{rootKind}: {chain}";
        }

        private static ObjectMetadata GetObjectMetadata(ClrHeap heap, ulong address)
        {
            if (!TryGetValidObject(heap, address, out ClrObject obj))
                return new ObjectMetadata(false, null, 0, 0);

            return new ObjectMetadata(true, obj.Type?.Name, obj.Size, obj.Type?.MethodTable ?? 0);
        }

        /// <summary>
        /// E-7 (docs/analysis/phase1/reference-chain-analyzer-audit.md): one streaming pass over
        /// the disk-backed object index — same single-pass-filtered-by-MethodTable-set idiom
        /// already used by <c>WeakReferenceAnalyzer</c>, <c>TimerLeakAnalyzer</c>,
        /// <c>AsyncStateMachineAnalyzer</c>, and <c>EventLeak/PublisherRegistry</c> — collecting up
        /// to <paramref name="multiSampleCount"/> - 1 additional distinct instances per target
        /// MethodTable, for E-1's root-consistency scoring. Deliberately not a per-type API: a
        /// per-type call in <see cref="AnalyzeTopTypes"/>'s top-N loop would re-stream the entire
        /// object index once per type. Falls back to a live <see cref="ClrHeap.EnumerateObjects"/>
        /// walk when no disk index was built (in-memory mode), matching the same fallback those
        /// other analyzers use. Exits early once every target MethodTable has reached quota.
        /// </summary>
        private static Dictionary<ulong, List<ulong>> CollectAdditionalSamples(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            Dictionary<ulong, ulong> primaryAddressByMt,
            int multiSampleCount,
            IProgress<AnalyzerProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            var additionalByMt = new Dictionary<ulong, List<ulong>>(primaryAddressByMt.Count);

            int perTypeQuota = multiSampleCount - 1;
            if (perTypeQuota <= 0 || primaryAddressByMt.Count == 0)
                return additionalByMt;

            bool hasDiskIndex = cache.EnumerateIndexedEntriesAsTuples().Any();
            IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> entries = hasDiskIndex
                ? cache.EnumerateIndexedEntriesAsTuples()
                : LiveHeapEntries(heap);

            var scanCounter = new ObjectScanCounter(
                "collecting multi-sample instances", progress, reportEveryObjects: 250_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

            int remainingTargets = primaryAddressByMt.Count;

            foreach ((ulong address, ulong mt, ulong _) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanCounter.Tick();

                if (!primaryAddressByMt.TryGetValue(mt, out ulong primaryAddress) || address == primaryAddress)
                    continue;

                if (!additionalByMt.TryGetValue(mt, out List<ulong>? list))
                {
                    list = new List<ulong>(perTypeQuota);
                    additionalByMt[mt] = list;
                }

                if (list.Count >= perTypeQuota)
                    continue;

                list.Add(address);
                if (list.Count == perTypeQuota)
                {
                    remainingTargets--;
                    if (remainingTargets <= 0)
                        break;
                }
            }

            scanCounter.Complete();
            return additionalByMt;
        }

        private static IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> LiveHeapEntries(ClrHeap heap)
        {
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null)
                    continue;

                yield return (obj.Address, obj.Type.MethodTable, obj.Size);
            }
        }

        // §9.20 (docs/refactor/analysis-profile-removal-plan.md): arrays are never treated as
        // noise — confirmed by V3/§11.3 that skipping them was real traversal pruning, not a
        // presentation concern, so excluding them would risk missing genuine retention chains.
        internal static bool IsNoisyType(ClrType? type)
        {
            if (type is null)
                return false;

            string? name = type.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            return name == "System.String" || name == "System.Object";
        }

        internal static bool IsKnownLeakType(ClrType? type, IReadOnlyList<string> knownLeakPatterns)
        {
            if (type is null)
                return false;

            string? name = type.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (var pattern in knownLeakPatterns)
            {
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string FormatNodeByAddress(ClrHeap heap, ulong address)
        {
            ObjectMetadata metadata = GetObjectMetadata(heap, address);
            string typeName = metadata.IsValid ? (metadata.TypeName ?? StringConstants.UnknownType) : "<invalid>";
            return $"{typeName}@0x{address:X}";
        }

        // Simple telemetry counters used during a single AnalyzeTopTypes invocation
        internal sealed class TelemetryCounters
        {
            public long PrunedNodes { get; set; }
            public long LargeFanoutNodesSkipped { get; set; }
            public long TotalCandidateSetSize { get; set; }
            public long ReverseIndexEntries { get; set; }

            // Returns a lightweight proxy that the helper classes (outside this class) can write through.
            public TelemetryProxy AsProxy() => new(this);
        }

        /// <summary>
        /// Lightweight ref-struct-like proxy so nested helper classes (declared outside
        /// <see cref="ReferenceChainAnalyzer"/>) can update telemetry without exposing the full counter object.
        /// </summary>
        internal sealed class TelemetryProxy(TelemetryCounters inner) : IPathSearchTelemetry
        {
            public void IncrementPruned() => inner.PrunedNodes++;
            public void IncrementLargeFanout() => inner.LargeFanoutNodesSkipped++;
        }

        // No weak/dependent-handle filtering here: ClrHeap.EnumerateRoots() only yields handles
        // where handle.IsStrong, and ClrRootKind has no Weak/Dependent member, so a root reaching
        // this method can never represent one — there is nothing for this method to filter.
        private static List<(string RootKind, ulong Address)> SortRootsByPriority(
            IReadOnlyList<(string RootKind, ulong Address)> roots)
        {
            var result = new List<(string RootKind, ulong Address)>(roots);
            result.Sort(static (a, b) => GetRootSearchPriority(a.RootKind).CompareTo(GetRootSearchPriority(b.RootKind)));
            return result;
        }

        private static int GetRootSearchPriority(string rootKind) => rootKind switch
        {
            // Stack locals are the most direct retainers and resolve fastest.
            "Stack" => 0,
            // Thread-static and static variables are the next most likely long-lived retainers.
            "ThreadStaticVar" => 1,
            "StaticVar" => 2,
            // Strong and pinned GC handles are explicit long-term roots.
            "Strong" => 3,
            "Pinned" => 4,
            "AsyncPinnedHandle" => 4,
            "RefCountedHandle" => 5,
            // Finalizer roots are already reported by FinalizableObjectAnalyzer — deprioritize.
            "Finalizer" => 10,
            // Unknown kinds go last.
            _ => 6
        };

        private static bool TryGetValidObject(ClrHeap heap, ulong address, out ClrObject obj)
        {
            obj = default;

            if (address == 0)
                return false;

            obj = heap.GetObject(address);
            return obj.IsValid;
        }
        public void Dispose() { }

    }

}
