using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer covering §20.1 (boxed value type inventory) and §20.2
    /// (value type shape issues: struct padding waste, oversized value types).
    ///
    /// Boxing detection: scans <see cref="HeapIndexBuildResult.TypeAggregates"/> and resolves
    /// each MT via <c>ClrType</c>. Value types (<c>IsValueType == true</c>) that appear in the
    /// heap index are by definition boxed instances. Enums are classified separately.
    ///
    /// Struct padding: for each value type with a populated <see cref="TypeShapeCache"/> entry,
    /// computes <c>StaticSize – sum(field.Size)</c> to surface padding waste.
    ///
    /// Capped at <see cref="TypeScanCap"/> MT lookups to bound metadata overhead.
    /// </summary>
    public sealed class BoxingAnalyzer : IAnalyzer
    {
        public string Name => "Boxing Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoxingAnalysisOptions options = context.AnalysisOptions.BoxingAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            BoxingAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
            IReadOnlyDictionary<ulong, TypeShapeEntry>? typeShapeCache = null;

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            {
                typeAggregates = idx.TypeAggregates;
                typeShapeCache = idx.TypeShapeCache;
            }

            if (typeAggregates is null)
            {
                return new BoxingDomainResult(0, 0, [], 0, 0, 0, 0, 0, [], [], 0, 0.0, false, options.TypeScanCap);
            }

            // ── Boxing inventory ──────────────────────────────────────────────
            var boxedByTypeName = new Dictionary<string, (int Count, ulong Bytes, bool IsEnum, bool HasRefFields)>(
                StringComparer.Ordinal);

            long totalBoxedObjects = 0;
            ulong totalBoxedBytes = 0;
            long boxedEnumCount = 0;
            ulong boxedEnumBytes = 0;
            long nullableCount = 0;
            ulong nullableBytes = 0;
            long oversizedCount = 0;

            // Struct padding candidates: collect during the same pass
            var paddingCandidates = new List<(string TypeName, int StructSize, int FieldBytes, int Count, int WastedBytes)>(64);

            // Oversized value type candidates: aggregated by type name during the same pass
            var oversizedByTypeName = new Dictionary<string, (int StaticSize, int Count)>(StringComparer.Ordinal);

            // Dictionary enumeration order depends on parallel segment-merge order,
            // which differs between disk/memory index builders (and across runs of
            // the same builder). Capping on raw iteration order therefore truncated
            // to a different arbitrary subset of types each time once distinct-type
            // count exceeded TypeScanCap, making TotalBoxedObjects non-deterministic.
            // Only sort (by TotalSize desc, MethodTable as tiebreak) when a cap will
            // actually bite, so the common case (types <= cap) pays no extra cost.
            bool scanCapped = typeAggregates.Count > options.TypeScanCap;
            IEnumerable<KeyValuePair<ulong, TypeAggregateIndexEntry>> scanOrder = typeAggregates;
            if (scanCapped)
            {
                var ordered = new List<KeyValuePair<ulong, TypeAggregateIndexEntry>>(typeAggregates);
                ordered.Sort((a, b) =>
                {
                    int cmp = b.Value.TotalSize.CompareTo(a.Value.TotalSize);
                    return cmp != 0 ? cmp : a.Key.CompareTo(b.Key);
                });
                scanOrder = ordered;
            }

            int scanned = 0;
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in scanOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > options.TypeScanCap) break;

                TypeAggregateIndexEntry entry = kv.Value;

                ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                if (clrType is null) continue;

                // ── Boxing detection ──────────────────────────────────────────
                // Value types can only appear on the managed heap as boxed instances.
                // In ClrMD, IsValueType is true for both unboxed layout and heap-resident boxed forms.
                // Alternatively, the spec's condition: BaseType?.Name is "System.ValueType"/"System.Enum"
                // also catches the same set reliably.
                bool isBoxed = clrType.IsValueType
                    || string.Equals(clrType.BaseType?.Name, "System.ValueType", StringComparison.Ordinal)
                    || string.Equals(clrType.BaseType?.Name, "System.Enum", StringComparison.Ordinal);

                if (!isBoxed) continue;

                string typeName = clrType.Name ?? $"MT:0x{kv.Key:x}";
                int count = (int)Math.Min(entry.Count, int.MaxValue);
                ulong bytes = entry.TotalSize;
                bool isEnum = clrType.IsEnum;
                bool isNullable = typeName.StartsWith("System.Nullable<", StringComparison.Ordinal);

                totalBoxedObjects += count;
                totalBoxedBytes += bytes;

                if (isEnum)
                {
                    boxedEnumCount += count;
                    boxedEnumBytes += bytes;
                }

                if (isNullable)
                {
                    nullableCount += count;
                    nullableBytes += bytes;
                }

                // Oversized value types — StaticSize reflects the value layout size
                if (clrType.StaticSize > options.OversizedThresholdBytes)
                {
                    oversizedCount += count;
                    if (oversizedByTypeName.TryGetValue(typeName, out var existingOversized))
                        oversizedByTypeName[typeName] = (existingOversized.StaticSize, existingOversized.Count + count);
                    else
                        oversizedByTypeName[typeName] = (clrType.StaticSize, count);
                }

                // Check if type has reference fields using typeShapeCache
                bool hasRefFields = typeShapeCache?.TryGetValue(kv.Key, out var shapeEntry) == true && shapeEntry.RefFields > 0;

                if (boxedByTypeName.TryGetValue(typeName, out var existing))
                    boxedByTypeName[typeName] = (existing.Count + count, existing.Bytes + bytes, isEnum, existing.HasRefFields || hasRefFields);
                else
                    boxedByTypeName[typeName] = (count, bytes, isEnum, hasRefFields);

                // ── Struct padding ────────────────────────────────────────────
                // Compute per-type only (not per-instance). Skip enums and very small structs.
                if (!isEnum && clrType.StaticSize > 4)
                {
                    int fieldBytes = ComputeTotalFieldBytes(clrType);
                    int structSize = clrType.StaticSize;
                    if (fieldBytes > 0 && structSize > fieldBytes)
                    {
                        int wasted = structSize - fieldBytes;
                        paddingCandidates.Add((typeName, structSize, fieldBytes, count, wasted));
                    }
                }
            }

            // ── Build top boxed types ─────────────────────────────────────────
            var typeList = new List<(string Name, int Count, ulong Bytes, bool IsEnum, bool HasRefFields)>(boxedByTypeName.Count);
            foreach (KeyValuePair<string, (int Count, ulong Bytes, bool IsEnum, bool HasRefFields)> kv in boxedByTypeName)
                typeList.Add((kv.Key, kv.Value.Count, kv.Value.Bytes, kv.Value.IsEnum, kv.Value.HasRefFields));

            typeList.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

            int topLimit = Math.Min(typeList.Count, options.TopBoxedTypeLimit);
            var topBoxedTypes = new List<BoxedTypeEntry>(topLimit);
            for (int i = 0; i < topLimit; i++)
            {
                var t = typeList[i];
                topBoxedTypes.Add(new BoxedTypeEntry(t.Name, t.Count, t.Bytes, t.IsEnum, t.HasRefFields));
            }

            // ── Build top padding waste types ─────────────────────────────────
            paddingCandidates.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));

            int padLimit = Math.Min(paddingCandidates.Count, options.TopPaddingLimit);
            var topPaddingWaste = new List<StructPaddingEntry>(padLimit);
            for (int i = 0; i < padLimit; i++)
            {
                var c = paddingCandidates[i];
                double ratio = c.StructSize > 0 ? (double)c.WastedBytes / c.StructSize : 0.0;
                topPaddingWaste.Add(new StructPaddingEntry(
                    TypeName: c.TypeName,
                    TotalFieldBytes: c.FieldBytes,
                    StructSize: c.StructSize,
                    WastedPaddingBytes: c.WastedBytes,
                    WasteRatio: ratio));
            }

            // Compute aggregate padding waste across ALL padding candidates (not just top)
            ulong aggregatePaddingWaste = 0;
            foreach (var c in paddingCandidates)
            {
                aggregatePaddingWaste += (ulong)(c.WastedBytes * c.Count);
            }

            // ── Build top oversized types ─────────────────────────────────────
            var oversizedList = new List<OversizedTypeEntry>(oversizedByTypeName.Count);
            foreach (KeyValuePair<string, (int StaticSize, int Count)> kv2 in oversizedByTypeName)
                oversizedList.Add(new OversizedTypeEntry(kv2.Key, kv2.Value.StaticSize, kv2.Value.Count));

            oversizedList.Sort(static (a, b) => b.Count.CompareTo(a.Count));

            int oversizedLimit = Math.Min(oversizedList.Count, options.TopOversizedTypeLimit);
            var topOversizedTypes = oversizedLimit == oversizedList.Count
                ? oversizedList
                : oversizedList.GetRange(0, oversizedLimit);

            double avgBoxedInstanceBytes = totalBoxedObjects > 0 ? (double)totalBoxedBytes / totalBoxedObjects : 0.0;

            return new BoxingDomainResult(
                TotalBoxedObjects: totalBoxedObjects,
                TotalBoxedBytes: totalBoxedBytes,
                TopBoxedTypes: topBoxedTypes,
                BoxedEnumCount: boxedEnumCount,
                BoxedEnumBytes: boxedEnumBytes,
                NullableBoxedCount: nullableCount,
                NullableBoxedBytes: nullableBytes,
                OversizedValueTypeInstanceCount: oversizedCount,
                TopOversizedTypes: topOversizedTypes,
                TopPaddingWasteTypes: topPaddingWaste,
                AggregatePaddingWasteBytes: aggregatePaddingWaste,
                AvgBoxedInstanceBytes: avgBoxedInstanceBytes,
                TypeScanCapped: scanCapped,
                TypeScanCapUsed: options.TypeScanCap);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the sum of all instance field sizes for a value type.
        /// Returns 0 on any exception (defensive — ClrMD field APIs can fail on corrupt dumps).
        /// </summary>
        private static int ComputeTotalFieldBytes(ClrType clrType)
        {
            try
            {
                int total = 0;
                foreach (ClrInstanceField f in clrType.Fields)
                {
                    // Size gives the in-memory byte count for the field in its containing type
                    total += f.Size;
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }
        public void Dispose() { }
    }
}
