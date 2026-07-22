using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class GCHandleAnalyzer : IAnalyzer
    {
        public string Name => "GC Handle Analysis";
        public string Category => "Handles";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GCHandleAnalysisOptions options = context.AnalysisOptions.GCHandleAnalysis;
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap? heap = null, IHeapAnalysisCache? cache = null)
        {
            return Analyze(runtime, heap, cache, new GCHandleAnalysisOptions(), progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap? heap, IHeapAnalysisCache? cache, GCHandleAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            var scanCounter = new ObjectScanCounter("scanning GC handles", progress, reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var pinnedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var pinnedBytesByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var allTargetTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            ulong totalPinnedRetainedBytes = 0;
            // OPT-#9: Cache method-table -> type-name to avoid one heap.GetObject call per handle
            // for handles whose target type has already been resolved. Collapses N handles of
            // the same type into a single lookup. Also reused for dependent-handle target resolution.
            var methodTableNameCache = new Dictionary<ulong, string>(capacity: 128);

            int totalHandles = 0;
            int strongLikeHandles = 0;
            int weakLikeHandles = 0;

            int dependentHandleCount = 0;
            int dependentResolvedEdgeCount = 0;
            int dependentUnresolvedTargetCount = 0;
            var dependentSourceTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dependentTargetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dependentSourceTargetPairCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            // TODO: Prefer consuming a shared handle snapshot provider (HeapIndexBuildResult.InMemoryHandleSnapshot
            // or IHandleSnapshotReader) when available to avoid repeated calls to runtime.EnumerateHandles().
            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                scanCounter.Tick();
                totalHandles++;

                string kind = handle.HandleKind.ToString();
                Increment(byKind, kind);

                if (IsWeakLike(kind))
                    weakLikeHandles++;
                else
                    strongLikeHandles++;

                ulong targetAddress = GetTargetAddress(handle);
                string? typeName;
                ulong resolvedSize = 0;
                if (heap is not null && cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var build))
                {
                    // Fast-path: resolve type name via methodTableNameCache keyed on MT.
                    // heap.GetObject is still needed to get the address + size, but we avoid
                    // an extra sample-address lookup since the MT is already known.
                    ClrObject targetObject = heap.GetObject(targetAddress);
                    if (targetObject.IsValid)
                    {
                        resolvedSize = targetObject.Size;
                        typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                    }
                    else
                    {
                        typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                    }
                }
                else
                {
                    typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                }
                if (typeName == null)
                    continue;

                Increment(allTargetTypes, typeName);

                if (kind.Contains("Pinned", StringComparison.OrdinalIgnoreCase))
                {
                    Increment(pinnedTypes, typeName);
                    if (resolvedSize > 0)
                    {
                        totalPinnedRetainedBytes += resolvedSize;
                        if (pinnedBytesByType.TryGetValue(typeName, out ulong existingBytes))
                            pinnedBytesByType[typeName] = existingBytes + resolvedSize;
                        else
                            pinnedBytesByType[typeName] = resolvedSize;
                    }
                }

                if (kind.Contains("Dependent", StringComparison.OrdinalIgnoreCase) && heap is not null)
                {
                    dependentHandleCount++;

                    if (!TryGetHandleAddress(handle.Object, out ulong sourceAddress)
                        || !TryResolveTypeNameStrict(heap, sourceAddress, methodTableNameCache, out string sourceType))
                    {
                        dependentUnresolvedTargetCount++;
                        continue;
                    }

                    Increment(dependentSourceTypeCounts, sourceType);

                    if (!TryGetDependentTargetAddress(handle, out ulong dependentTargetAddress)
                        || !TryResolveTypeNameStrict(heap, dependentTargetAddress, methodTableNameCache, out string dependentTargetType))
                    {
                        dependentUnresolvedTargetCount++;
                        continue;
                    }

                    dependentResolvedEdgeCount++;
                    Increment(dependentTargetTypeCounts, dependentTargetType);
                    Increment(dependentSourceTargetPairCounts, $"{sourceType} -> {dependentTargetType}");
                }
            }

            scanCounter.Complete();

            int pinnedHandleTargets = pinnedTypes.Values.Sum();
            double dependentUnresolvedPercent = dependentHandleCount == 0 ? 0
                : dependentUnresolvedTargetCount * 100.0 / dependentHandleCount;

            static List<NameCountEntry> ToTopEntries(Dictionary<string, int> source, int take)
            {
                var list = new List<NameCountEntry>(Math.Min(source.Count, take));
                foreach (var kvp in source.OrderByDescending(k => k.Value).Take(take))
                    list.Add(new NameCountEntry(kvp.Key, kvp.Value));
                return list;
            }
            static List<NameBytesEntry> ToTopByteEntries(Dictionary<string, ulong> source, int take)
            {
                var list = new List<NameBytesEntry>(Math.Min(source.Count, take));
                foreach (var kvp in source.OrderByDescending(k => k.Value).Take(take))
                    list.Add(new NameBytesEntry(kvp.Key, kvp.Value));
                return list;
            }

            return new GCHandleDomainResult(
                totalHandles,
                strongLikeHandles,
                weakLikeHandles,
                pinnedHandleTargets,
                ToTopEntries(byKind, options.TopTypeCount),
                ToTopEntries(allTargetTypes, options.TopTypeCount),
                ToTopEntries(pinnedTypes, options.TopTypeCount),
                totalPinnedRetainedBytes,
                ToTopByteEntries(pinnedBytesByType, options.TopTypeCount),
                dependentHandleCount,
                dependentResolvedEdgeCount,
                dependentUnresolvedTargetCount,
                dependentUnresolvedPercent,
                ToTopEntries(dependentSourceTypeCounts, options.TopTypeCount),
                ToTopEntries(dependentTargetTypeCounts, options.TopTypeCount),
                ToTopEntries(dependentSourceTargetPairCounts, options.TopTypeCount));
        }

        public void Dispose() { }

        private static bool IsWeakLike(string kind)
        {
            return kind.Contains("Weak", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("Dependent", StringComparison.OrdinalIgnoreCase);
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out int value))
                counts[key] = value + 1;
            else
                counts[key] = 1;
        }

        private static ulong GetTargetAddress(ClrHandle handle)
        {
            object boxedTarget = handle.Object;

            if (boxedTarget is ClrObject clrObject)
            {
                return clrObject.IsValid ? clrObject.Address : 0;
            }

            if (boxedTarget is ulong address)
            {
                return address;
            }

            return 0;
        }

        private static bool TryGetHandleAddress(object value, out ulong address)
        {
            address = 0;

            if (value is ClrObject clrObject)
            {
                if (!clrObject.IsValid)
                    return false;

                address = clrObject.Address;
                return true;
            }

            if (value is ulong targetAddress && targetAddress != 0)
            {
                address = targetAddress;
                return true;
            }

            return false;
        }

        private static bool TryGetDependentTargetAddress(ClrHandle handle, out ulong targetAddress)
        {
            targetAddress = 0;

            string[] propertyCandidates =
            [
                "DependentTarget",
                "Target",
                "Secondary",
                "DependentObject",
                "Dependent"
            ];

            Type handleType = handle.GetType();
            foreach (string propertyName in propertyCandidates)
            {
                System.Reflection.PropertyInfo? property = handleType.GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (property == null)
                    continue;

                object? value = property.GetValue(handle);
                if (value == null)
                    continue;

                if (TryGetHandleAddress(value, out targetAddress))
                    return true;
            }

            return false;
        }

        private static bool TryResolveTypeNameStrict(ClrHeap heap, ulong address, Dictionary<ulong, string> methodTableNameCache, out string typeName)
        {
            typeName = StringConstants.UnknownType;

            if (address == 0)
                return false;

            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return false;

            ulong methodTable = obj.Type.MethodTable;
            if (methodTable != 0 && methodTableNameCache.TryGetValue(methodTable, out string? cached))
            {
                typeName = cached;
                return true;
            }

            typeName = obj.Type.Name ?? StringConstants.UnknownType;
            if (methodTable != 0)
                methodTableNameCache[methodTable] = typeName;

            return true;
        }

        private static string? ResolveTargetTypeName(ClrHeap? heap, ulong targetAddress, Dictionary<ulong, string> methodTableNameCache)
        {
            if (targetAddress == 0)
                return null;

            if (heap == null)
                return $"Object@0x{targetAddress:X}";

            ClrObject targetObject = heap.GetObject(targetAddress);
            if (!targetObject.IsValid)
                return $"Object@0x{targetAddress:X}";

            ulong methodTable = targetObject.Type?.MethodTable ?? 0;
            if (methodTable != 0 && methodTableNameCache.TryGetValue(methodTable, out string? cached))
                return cached;

            string name = targetObject.Type?.Name ?? StringConstants.UnknownType;
            if (methodTable != 0)
                methodTableNameCache[methodTable] = name;

            return name;
        }
    }
}
