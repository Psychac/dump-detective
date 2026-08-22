using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Pure Phase-2 type-metadata analyzer: no heap object enumeration.
    /// Reads <see cref="HeapIndexBuildResult.TypeShapeCache"/> (built during Phase 1)
    /// and joins with <see cref="HeapIndexBuildResult.TypeAggregates"/> for instance counts.
    /// Classifies types as ReferenceHeavy / ValueHeavy / Balanced / Scalar and ranks
    /// by (referenceFieldRatio × instanceCount) to surface GC-scan-cost hotspots.
    /// </summary>
    public sealed class ObjectShapeAnalyzer : IAnalyzer
    {
        public string Name => "Object Shape Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            if (cache is not HeapAnalysisCache heapCache
                || !heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx)
                || idx.TypeShapeCache is null)
            {
                return new ObjectShapeAnalyzerDomainResult(
                    TopReferenceHeavyTypes: [],
                    TopValueHeavyTypes: [],
                    TopBalancedTypes: [],
                    TotalTypesAnalyzed: 0,
                    AvgRefFieldsPerType: 0,
                    TotalGcScanWork: 0);
            }

            IReadOnlyDictionary<ulong, TypeShapeEntry> shapes = idx.TypeShapeCache;
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = idx.TypeAggregates;

            // Collect MTs present in both shape cache and aggregates.
            var candidates = new List<(ulong Mt, TypeShapeEntry Shape, long Count)>(shapes.Count);
            foreach (KeyValuePair<ulong, TypeShapeEntry> kv in shapes)
            {
                if (aggregates.TryGetValue(kv.Key, out TypeAggregateIndexEntry agg))
                    candidates.Add((kv.Key, kv.Value, agg.Count));
            }

            var refHeavyCandidates = new List<TypeShapeProfile>();
            var valHeavyCandidates = new List<TypeShapeProfile>();
            var balancedCandidates = new List<TypeShapeProfile>();

            long totalRefFields = 0;
            long totalGcScanWork = 0;
            int typesAnalyzed = 0;

            foreach ((ulong mt, TypeShapeEntry shape, long count) in candidates)
            {
                ClrType? type = heap.GetTypeByMethodTable(mt);
                if (type is null)
                    continue;

                if (!aggregates.TryGetValue(mt, out TypeAggregateIndexEntry agg))
                    continue;

                typesAnalyzed++;
                totalRefFields += shape.RefFields;
                totalGcScanWork += (long)(shape.RefFields * (double)Math.Max(0, count));

                double refRatio = shape.TotalFields > 0
                    ? shape.RefFields * 1.0 / shape.TotalFields
                    : 0.0;

                ObjectShapeCategory category = shape.TotalFields == 0
                    ? ObjectShapeCategory.Scalar
                    : refRatio > 0.6
                        ? ObjectShapeCategory.ReferenceHeavy
                        : refRatio < 0.2
                            ? ObjectShapeCategory.ValueHeavy
                            : ObjectShapeCategory.Balanced;

                int baseDepth = ComputeBaseTypeDepth(type);
                int ifaceCount;
                try { ifaceCount = type.EnumerateInterfaces().Count(); }
                catch { ifaceCount = 0; }

                var profile = new TypeShapeProfile(
                    TypeName: type.Name ?? $"MT:0x{mt:x}",
                    TotalFields: shape.TotalFields,
                    ReferenceFields: shape.RefFields,
                    ValueFields: shape.ValFields,
                    ReferenceFieldRatio: refRatio,
                    InstanceCount: (ulong)Math.Max(0, count),
                    TotalSize: agg.TotalSize,
                    IsFinalizable: type.IsFinalizable,
                    IsValueType: type.IsValueType,
                    IsArray: type.IsArray,
                    BaseTypeChainDepth: baseDepth,
                    InterfaceCount: ifaceCount,
                    Category: category);

                if (category == ObjectShapeCategory.ReferenceHeavy)
                    refHeavyCandidates.Add(profile);
                else if (category == ObjectShapeCategory.ValueHeavy)
                    valHeavyCandidates.Add(profile);
                else if (category == ObjectShapeCategory.Balanced)
                    balancedCandidates.Add(profile);
            }

            // Sort by GC scan cost score (refRatio × instanceCount) in descending order
            refHeavyCandidates.Sort(static (a, b) =>
                (b.ReferenceFieldRatio * (double)b.InstanceCount)
                    .CompareTo(a.ReferenceFieldRatio * (double)a.InstanceCount));

            // Sort value-heavy types by total heap size (more impactful types first)
            valHeavyCandidates.Sort(static (a, b) =>
                b.TotalSize.CompareTo(a.TotalSize));

            // Sort balanced types by instance count (most populous first)
            balancedCandidates.Sort(static (a, b) =>
                b.InstanceCount.CompareTo(a.InstanceCount));

            double avgRefFields = typesAnalyzed > 0 ? totalRefFields * 1.0 / typesAnalyzed : 0.0;

            return new ObjectShapeAnalyzerDomainResult(
                TopReferenceHeavyTypes: refHeavyCandidates,
                TopValueHeavyTypes: valHeavyCandidates,
                TopBalancedTypes: balancedCandidates,
                TotalTypesAnalyzed: typesAnalyzed,
                AvgRefFieldsPerType: avgRefFields,
                TotalGcScanWork: totalGcScanWork);
        }

        private static int ComputeBaseTypeDepth(ClrType type)
        {
            int depth = 0;
            ClrType? cur = type.BaseType;
            while (cur is not null && depth < 20)
            {
                depth++;
                cur = cur.BaseType;
            }
            return depth;
        }

        public void Dispose() { }
    }
}
