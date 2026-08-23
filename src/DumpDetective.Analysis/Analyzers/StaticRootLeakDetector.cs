using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public class StaticRootLeakDetector : IAnalyzer, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
    {
        private readonly record struct ObjectMetadata(bool IsValid, string TypeName, ulong Size, ulong MethodTable);

        public string Name => "Static Root Leak Detection";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StaticRootLeakAnalysisOptions options = context.AnalysisOptions.StaticRootLeakAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress, cancellationToken).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, new StaticRootLeakAnalysisOptions(), progress: null, CancellationToken.None);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, StaticRootLeakAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            var allStaticRootAnalysis = AnalyzeStaticRoots(heap, cache, options, progress, cancellationToken);
            var significantStaticRoots = allStaticRootAnalysis
                .Where(a => IsSignificant(a, options))
                .ToArray();

            IReadOnlyList<(string RootKind, ulong Address)> validRoots = cache.GetOrBuildValidRoots(heap);

            var provider = new ReferenceGraph(heap);
            var limits = new RootPathSearchLimits
            {
                MaxCandidateNodes = 5_000,
                MaxCandidateDepth = 8,
                MaxRootExpansionDepth = 12,
                LargeFanoutThreshold = 100,
            };
            var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false, cache.TryGetReverseIndexProvider(), cache);

            var topRoots = allStaticRootAnalysis
                .OrderByDescending(r => r.TotalMemoryImpact)
                .Select(r => BuildSnapshot(heap, cache, validRoots, finder, r))
                .ToArray();

            if (significantStaticRoots.Length == 0)
            {
                return new StaticRootDomainResult(0, 0, topRoots);
            }

            ulong totalImpact = 0;
            foreach (var item in significantStaticRoots)
                totalImpact += item.TotalMemoryImpact;

            return new StaticRootDomainResult(significantStaticRoots.Length, totalImpact, topRoots);
        }

        private static StaticRootSnapshot BuildSnapshot(ClrHeap heap, IHeapAnalysisCache cache, IReadOnlyList<(string RootKind, ulong Address)> validRoots, RootPathFinder finder, StaticRootAnalysis analysis)
        {
            bool found = finder.TryFindAnyRootPath(analysis.DirectObjectAddress, validRoots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
            string? rootPath = found ? RootPathSearchSupport.FormatPath(heap, rootKind!, addresses, cache) : null;
            var evidence = new Evidence(
                analysis.TotalMemoryImpact,
                rootPath,
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
                analysis.AssemblyLoadContextInfo);
        }

        private static bool IsSignificant(StaticRootAnalysis analysis, StaticRootLeakAnalysisOptions options)
        {
            return analysis.TotalMemoryImpact > options.SignificantMemoryThresholdBytes
                || analysis.ObjectsKeptAlive > options.SignificantObjectCountThreshold;
        }

        private List<StaticRootAnalysis> AnalyzeStaticRoots(ClrHeap heap, IHeapAnalysisCache cache, StaticRootLeakAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            var results = new List<StaticRootAnalysis>();
            var processedRoots = new HashSet<ulong>();
            int rootsScanned = 0;

            // OPT-#2: Consume the cached root list instead of calling heap.EnumerateRoots() directly,
            // which would be a third independent full-dump root walk (cache already performs two:
            // GetStaticRootedAddresses and GetOrBuildValidRoots). Filter to static roots inline.
            progress?.Report(new(0, "resolving static roots"));
            IReadOnlyList<(string RootKind, ulong TargetAddr, ulong RootAddr)> allRoots = cache.GetOrBuildRootTriples(heap);
            HashSet<ulong> staticRootedAddresses = cache.GetStaticRootedAddresses(heap);
            var staticFieldsByRootAddress = cache.GetStaticFieldsByRootAddress(heap);

            // §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): null
            // when Stage B wasn't built for this run — retained-set analysis below degrades to
            // direct-object-only (ScanWasCapped = true) in that case.
            IDominatorTreeProvider? treeProvider = cache.TryGetDominatorTreeProvider();
            var typeNameByMethodTable = new Dictionary<ulong, string>(capacity: 64);
            var delegateFieldByMethodTable = new Dictionary<ulong, bool>(capacity: 64);

            foreach ((string rootKind, ulong rootAddress, ulong rootStorageAddress) in allRoots)
            {
                if (!staticRootedAddresses.Contains(rootAddress))
                    continue;

                if (rootAddress == 0 || !processedRoots.Add(rootAddress))
                    continue;

                rootsScanned++;
                if (rootsScanned % 50 == 0)
                    progress?.Report(new(rootsScanned, "scanning static roots", $"{results.Count} significant"));

                ObjectMetadata rootMetadata = GetObjectMetadata(heap, rootAddress);
                if (!rootMetadata.IsValid)
                    continue;

                int objectsKeptAlive;
                ulong totalSize;
                List<RetainedTypeInfo> topRetainedTypes;
                bool scanWasCapped;
                bool containsCollections;
                bool containsEventHandlers;

                // Shape pre-check (docs/analysis/retained-size-candidate-selection.md Phase 4):
                // a root whose direct object has no reference-typed field anywhere in its field
                // tree can't reach anything beyond itself, so walking its retained set would only
                // ever discover the root itself. Skip building it and synthesize the equivalent
                // single-entry result directly from already-resolved rootMetadata.
                if (!RetainedSizeCandidateSelector.RequiresWalk(cache, heap, rootMetadata.MethodTable))
                {
                    objectsKeptAlive = 1;
                    totalSize = rootMetadata.Size;
                    topRetainedTypes = new List<RetainedTypeInfo>(1)
                    {
                        new RetainedTypeInfo { TypeName = rootMetadata.TypeName, Count = 1, TotalSize = rootMetadata.Size }
                    };
                    scanWasCapped = false;
                    containsCollections = false;
                    containsEventHandlers = false;
                }
                else if (treeProvider is not null && treeProvider.TryGetRetainedBytes(rootAddress, out ulong exactTotalSize))
                {
                    // §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
                    // exact retained bytes in O(1); the per-type breakdown below streams the
                    // dominator subtree's member addresses (no resident Dictionary, unlike the old
                    // BoundedGraphWalk.CollectRetainedObjects) — every retained object is counted,
                    // not just the first MaxRetainedObjectsToScan of them.
                    var typeStats = new Dictionary<string, RetainedTypeInfo>();
                    totalSize = exactTotalSize;
                    containsCollections = false;
                    containsEventHandlers = false;
                    int count = 0;

                    foreach (ulong address in treeProvider.EnumerateRetainedSet(rootAddress))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        count++;

                        if (!cache.TryGetObjectMetadata(heap, address, out ulong methodTable, out ulong size) || methodTable == 0)
                            continue;

                        if (!typeNameByMethodTable.TryGetValue(methodTable, out string? typeName))
                        {
                            typeName = heap.GetTypeByMethodTable(methodTable)?.Name ?? StringConstants.UnknownType;
                            typeNameByMethodTable[methodTable] = typeName;
                        }

                        if (!typeStats.TryGetValue(typeName, out var info))
                        {
                            info = new RetainedTypeInfo { TypeName = typeName };
                            typeStats[typeName] = info;
                        }

                        info.Count++;
                        info.TotalSize += size;

                        if (!containsCollections && TypeFilterHelper.IsCollectionType(typeName))
                            containsCollections = true;

                        if (!containsEventHandlers)
                            containsEventHandlers = HasDelegateFields(heap, address, methodTable, delegateFieldByMethodTable);
                    }

                    objectsKeptAlive = count;
                    scanWasCapped = false;
                    topRetainedTypes = GetTopRetainedTypes(typeStats);
                }
                else
                {
                    // Dominator tree unavailable for this run (Stage B not built, or this root
                    // wasn't reachable when the tree was built) — no exact retained-set analysis
                    // possible; report the direct object only rather than guess.
                    objectsKeptAlive = 1;
                    totalSize = rootMetadata.Size;
                    topRetainedTypes = new List<RetainedTypeInfo>(1)
                    {
                        new RetainedTypeInfo { TypeName = rootMetadata.TypeName, Count = 1, TotalSize = rootMetadata.Size }
                    };
                    scanWasCapped = true;
                    containsCollections = false;
                    containsEventHandlers = false;
                }

                string? alcInfo = null;
                string rootDescription;

                if (staticFieldsByRootAddress.TryGetValue(rootStorageAddress, out (string FieldOwnerType, string FieldName, int AppDomainId) fieldInfo))
                {
                    rootDescription = $"{fieldInfo.FieldOwnerType}.{fieldInfo.FieldName}";
                    if (fieldInfo.AppDomainId != 1)
                    {
                        alcInfo = $"AppDomain#{fieldInfo.AppDomainId}";
                        rootDescription += $" [{alcInfo}]";
                    }
                }
                else
                {
                    rootDescription = $"{rootKind} @ 0x{rootAddress:X}";
                }

                var analysis = new StaticRootAnalysis
                {
                    RootDescription = rootDescription,
                    DirectObjectAddress = rootAddress,
                    DirectObjectType = rootMetadata.TypeName,
                    DirectObjectSize = rootMetadata.Size,
                    TotalMemoryImpact = totalSize,
                    ObjectsKeptAlive = objectsKeptAlive,
                    TopRetainedTypes = topRetainedTypes,
                    ContainsCollections = containsCollections,
                    ContainsEventHandlers = containsEventHandlers,
                    ScanWasCapped = scanWasCapped,
                    AssemblyLoadContextInfo = alcInfo
                };

                results.Add(analysis);
            }

            return results;
        }

        private List<RetainedTypeInfo> GetTopRetainedTypes(Dictionary<string, RetainedTypeInfo> typeStats)
        {
            // Manual sorting - no LINQ allocations. Full list — the section builder paginates.
            var result = new List<RetainedTypeInfo>(typeStats.Values);
            result.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            return result;
        }

        private static ObjectMetadata GetObjectMetadata(ClrHeap heap, ulong address)
        {
            if (address == 0)
                return new ObjectMetadata(false, StringConstants.UnknownType, 0, 0);

            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid)
                return new ObjectMetadata(false, StringConstants.UnknownType, 0, 0);

            return new ObjectMetadata(true, obj.Type?.Name ?? StringConstants.UnknownType, obj.Size, obj.Type?.MethodTable ?? 0);
        }

        private static bool HasDelegateFields(ClrHeap heap, ulong address, ulong methodTable, Dictionary<ulong, bool> delegateFieldByMethodTable)
        {
            if (address == 0)
                return false;

            if (methodTable != 0 && delegateFieldByMethodTable.TryGetValue(methodTable, out bool cached))
                return cached;

            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return false;

            foreach (var field in obj.Type.Fields)
            {
                if (TypeFilterHelper.IsDelegateType(field.Type))
                {
                    if (methodTable != 0)
                        delegateFieldByMethodTable[methodTable] = true;

                    return true;
                }
            }

            if (methodTable != 0)
                delegateFieldByMethodTable[methodTable] = false;

            return false;
        }

        public void Dispose() { }
    }

    internal class StaticRootAnalysis
    {
        public string RootDescription { get; set; } = string.Empty;
        public ulong DirectObjectAddress { get; set; }
        public string DirectObjectType { get; set; } = string.Empty;
        public ulong DirectObjectSize { get; set; }
        public ulong TotalMemoryImpact { get; set; }
        public int ObjectsKeptAlive { get; set; }
        public List<RetainedTypeInfo> TopRetainedTypes { get; set; } = new();
        public bool ContainsCollections { get; set; }
        public bool ContainsEventHandlers { get; set; }
        public bool ScanWasCapped { get; set; }
        public string? AssemblyLoadContextInfo { get; set; }
    }
}

// NOTE: analyzers implement IDisposable on IAnalyzer; add no-op Dispose to this analyzer as placeholder


