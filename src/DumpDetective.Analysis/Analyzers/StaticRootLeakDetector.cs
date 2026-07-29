using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public class StaticRootLeakDetector : IAnalyzer
    {
        private readonly record struct ObjectMetadata(bool IsValid, string TypeName, ulong Size, ulong MethodTable);

        public string Name => "Static Root Leak Detection";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StaticRootLeakAnalysisOptions options = context.AnalysisOptions.StaticRootLeakAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, new StaticRootLeakAnalysisOptions(), progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, StaticRootLeakAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            var allStaticRootAnalysis = AnalyzeStaticRoots(heap, cache, options, progress);
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
            var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false);

            var topRoots = allStaticRootAnalysis
                .OrderByDescending(r => r.TotalMemoryImpact)
                .Take(options.MaxRootsToReport)
                .Select(r => BuildSnapshot(heap, validRoots, finder, r))
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

        private static StaticRootSnapshot BuildSnapshot(ClrHeap heap, IReadOnlyList<(string RootKind, ulong Address)> validRoots, RootPathFinder finder, StaticRootAnalysis analysis)
        {
            bool found = finder.TryFindAnyRootPath(analysis.DirectObjectAddress, validRoots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
            string? rootPath = found ? RootPathSearchSupport.FormatPath(heap, rootKind!, addresses) : null;
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
                evidence);
        }

        private static bool IsSignificant(StaticRootAnalysis analysis, StaticRootLeakAnalysisOptions options)
        {
            return analysis.TotalMemoryImpact > options.SignificantMemoryThresholdBytes
                || analysis.ObjectsKeptAlive > options.SignificantObjectCountThreshold;
        }

        private List<StaticRootAnalysis> AnalyzeStaticRoots(ClrHeap heap, IHeapAnalysisCache cache, StaticRootLeakAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            var results = new List<StaticRootAnalysis>();
            var processedRoots = new HashSet<ulong>();
            int rootsScanned = 0;

            // OPT-#2: Consume the cached root list instead of calling heap.EnumerateRoots() directly,
            // which would be a third independent full-dump root walk (cache already performs two:
            // GetStaticRootedAddresses and GetOrBuildValidRoots). Filter to static roots inline.
            progress?.Report(new(0, "resolving static roots"));
            IReadOnlyList<(string RootKind, ulong Address)> allRoots = cache.GetOrBuildValidRoots(heap);
            HashSet<ulong> staticRootedAddresses = cache.GetStaticRootedAddresses(heap);

            foreach ((string rootKind, ulong rootAddress) in allRoots)
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

                var retainedObjects = BoundedGraphWalk.CollectRetainedObjects(heap, rootAddress, options.MaxRetainedObjectsToScan);

                var typeStats = new Dictionary<string, RetainedTypeInfo>();
                var delegateFieldByMethodTable = new Dictionary<ulong, bool>(capacity: 64);
                ulong totalSize = 0;
                bool containsCollections = false;
                bool containsEventHandlers = false;
                int sampledCount = 0;

                foreach (var address in retainedObjects)
                {
                    ObjectMetadata retainedMetadata = GetObjectMetadata(heap, address);
                    if (!retainedMetadata.IsValid)
                        continue;

                    totalSize += retainedMetadata.Size;

                    string typeName = retainedMetadata.TypeName;
                    if (!typeStats.TryGetValue(typeName, out var info))
                    {
                        info = new RetainedTypeInfo { TypeName = typeName };
                        typeStats[typeName] = info;
                    }

                    info.Count++;
                    info.TotalSize += retainedMetadata.Size;

                    if (sampledCount < options.SampleRetainedObjectsToInspect)
                    {
                        if (!containsCollections && TypeFilterHelper.IsCollectionType(typeName))
                        {
                            containsCollections = true;
                        }

                        if (!containsEventHandlers)
                        {
                            containsEventHandlers = HasDelegateFields(heap, address, retainedMetadata.MethodTable, delegateFieldByMethodTable);
                        }

                        sampledCount++;
                    }
                }

                var analysis = new StaticRootAnalysis
                {
                    RootDescription = $"{rootKind} @ 0x{rootAddress:X}",
                    DirectObjectAddress = rootAddress,
                    DirectObjectType = rootMetadata.TypeName,
                    DirectObjectSize = rootMetadata.Size,
                    TotalMemoryImpact = totalSize,
                    ObjectsKeptAlive = retainedObjects.Count,
                    TopRetainedTypes = GetTopRetainedTypes(typeStats, options.TopRetainedTypesToReport),
                    ContainsCollections = containsCollections,
                    ContainsEventHandlers = containsEventHandlers
                };

                results.Add(analysis);
            }

            return results;
        }

        private List<RetainedTypeInfo> GetTopRetainedTypes(Dictionary<string, RetainedTypeInfo> typeStats, int topRetainedTypesToReport)
        {
            // Manual sorting - no LINQ allocations
            var result = new List<RetainedTypeInfo>(typeStats.Values);
            result.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            if (result.Count > topRetainedTypesToReport)
                result.RemoveRange(topRetainedTypesToReport, result.Count - topRetainedTypesToReport);

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
    }

    internal class RetainedTypeInfo
    {
        public string TypeName { get; set; } = string.Empty;
        public int Count { get; set; }
        public ulong TotalSize { get; set; }
    }
}

// NOTE: analyzers implement IDisposable on IAnalyzer; add no-op Dispose to this analyzer as placeholder


