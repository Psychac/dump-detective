using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

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
        public string Name     => "Boxing Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoxingAnalysisOptions options = context.GetOption<BoxingAnalysisOptions>();
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
                return new BoxingDomainResult(0, 0, [], 0, 0, 0, [], false);
            }

            // ── Boxing inventory ──────────────────────────────────────────────
            var boxedByTypeName = new Dictionary<string, (int Count, ulong Bytes, bool IsEnum)>(
                StringComparer.Ordinal);

            int  totalBoxedObjects = 0;
            ulong totalBoxedBytes  = 0;
            int  boxedEnumCount    = 0;
            ulong boxedEnumBytes   = 0;
            int  oversizedCount    = 0;
            bool scanCapped        = false;

            // Struct padding candidates: collect during the same pass
            var paddingCandidates = new List<(string TypeName, int StructSize, int FieldBytes)>(64);

            int scanned = 0;
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > options.TypeScanCap) { scanCapped = true; break; }

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
                    || string.Equals(clrType.BaseType?.Name, "System.Enum",      StringComparison.Ordinal);

                if (!isBoxed) continue;

                string typeName = clrType.Name ?? $"MT:0x{kv.Key:x}";
                int    count    = (int)Math.Min(entry.Count, int.MaxValue);
                ulong  bytes    = entry.TotalSize;
                bool   isEnum   = clrType.IsEnum;

                totalBoxedObjects += count;
                totalBoxedBytes   += bytes;

                if (isEnum)
                {
                    boxedEnumCount += count;
                    boxedEnumBytes += bytes;
                }

                // Oversized value types — StaticSize reflects the value layout size
                if (clrType.StaticSize > options.OversizedThresholdBytes)
                    oversizedCount += count;

                if (boxedByTypeName.TryGetValue(typeName, out var existing))
                    boxedByTypeName[typeName] = (existing.Count + count, existing.Bytes + bytes, isEnum);
                else
                    boxedByTypeName[typeName] = (count, bytes, isEnum);

                // ── Struct padding ────────────────────────────────────────────
                // Compute per-type only (not per-instance). Skip enums and very small structs.
                if (!isEnum && clrType.StaticSize > 4)
                {
                    int fieldBytes = ComputeTotalFieldBytes(clrType);
                    int structSize = clrType.StaticSize;
                    if (fieldBytes > 0 && structSize > fieldBytes)
                        paddingCandidates.Add((typeName, structSize, fieldBytes));
                }
            }

            // ── Build top boxed types ─────────────────────────────────────────
            var typeList = new List<(string Name, int Count, ulong Bytes, bool IsEnum)>(boxedByTypeName.Count);
            foreach (KeyValuePair<string, (int Count, ulong Bytes, bool IsEnum)> kv in boxedByTypeName)
                typeList.Add((kv.Key, kv.Value.Count, kv.Value.Bytes, kv.Value.IsEnum));

            typeList.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

            int topLimit = Math.Min(typeList.Count, options.TopBoxedTypeLimit);
            var topBoxedTypes = new List<BoxedTypeEntry>(topLimit);
            for (int i = 0; i < topLimit; i++)
            {
                var t = typeList[i];
                topBoxedTypes.Add(new BoxedTypeEntry(t.Name, t.Count, t.Bytes, t.IsEnum));
            }

            // ── Build top padding waste types ─────────────────────────────────
            paddingCandidates.Sort(static (a, b) =>
            {
                int wastedA = a.StructSize - a.FieldBytes;
                int wastedB = b.StructSize - b.FieldBytes;
                return wastedB.CompareTo(wastedA);
            });

            int padLimit = Math.Min(paddingCandidates.Count, options.TopPaddingLimit);
            var topPaddingWaste = new List<StructPaddingEntry>(padLimit);
            for (int i = 0; i < padLimit; i++)
            {
                var c = paddingCandidates[i];
                int wasted = c.StructSize - c.FieldBytes;
                double ratio = c.StructSize > 0 ? (double)wasted / c.StructSize : 0.0;
                topPaddingWaste.Add(new StructPaddingEntry(
                    TypeName:            c.TypeName,
                    TotalFieldBytes:     c.FieldBytes,
                    StructSize:          c.StructSize,
                    WastedPaddingBytes:  wasted,
                    WasteRatio:          ratio));
            }

            return new BoxingDomainResult(
                TotalBoxedObjects:      totalBoxedObjects,
                TotalBoxedBytes:        totalBoxedBytes,
                TopBoxedTypes:          topBoxedTypes,
                BoxedEnumCount:         boxedEnumCount,
                BoxedEnumBytes:         boxedEnumBytes,
                OversizedValueTypeCount: oversizedCount,
                TopPaddingWasteTypes:   topPaddingWaste,
                TypeScanCapped:         scanCapped);
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
