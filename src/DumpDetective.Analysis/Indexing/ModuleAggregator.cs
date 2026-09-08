using System;
using System.Collections.Generic;
using System.IO;
using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Indexing
{
    internal static class ModuleAggregator
    {
        // Only the heaviest modules (>= HeavyModuleWarningThresholdBytes) get a type breakdown —
        // keeps the bounded top-N insertion cost proportional to a small module subset, not all modules.
        private const int TopTypesPerHeavyModuleToShow = 10;

        public static (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies) Aggregate(
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates,
            IReadOnlyList<DumpDetective.Analysis.Indexing.ModuleInfo>? modules,
            DumpDetective.Core.Options.ModuleAnalysisOptions options,
            Func<ulong, string?>? resolveTypeName = null)
        {
            if (modules is null) return (Array.Empty<ModuleHeapStats>(), Array.Empty<ModuleTypeDensity>());
            var statsById = new Dictionary<int, (int uniqueTypes, long objectCount, ulong totalBytes, ulong lohBytes, long gen2ObjectCount)>();

            foreach (var kv in aggregates)
            {
                var agg = kv.Value;
                int id = agg.ModuleId;
                if (id < 0 || id >= modules.Count) continue;

                if (!statsById.TryGetValue(id, out var val))
                    val = (0, 0L, 0UL, 0UL, 0L);
                val.uniqueTypes++;
                val.objectCount += agg.Count;
                val.totalBytes += agg.TotalSize;
                val.lohBytes += agg.LohSize;
                val.gen2ObjectCount += agg.Gen2Count;
                statsById[id] = val;
            }

            var topByMemory = new List<(ulong Bytes, int Id)>(statsById.Count);
            var density = new List<ModuleTypeDensity>();
            var heavyModuleIds = new HashSet<int>();

            foreach (var kv in statsById)
            {
                int id = kv.Key;
                var s = kv.Value;
                topByMemory.Add((s.totalBytes, id));

                if (resolveTypeName is not null && s.totalBytes >= options.HeavyModuleWarningThresholdBytes)
                    heavyModuleIds.Add(id);

                if (s.uniqueTypes <= options.DensityAnomalyMaxTypes && s.totalBytes >= options.DensityAnomalyMinBytes)
                {
                    var mod = modules[id];
                    ulong bytesPerType = s.uniqueTypes > 0 ? s.totalBytes / (ulong)s.uniqueTypes : s.totalBytes;
                    density.Add(new ModuleTypeDensity(
                        System.IO.Path.GetFileName(mod.Name ?? string.Empty),
                        mod.AssemblyName ?? string.Empty,
                        s.uniqueTypes,
                        s.objectCount,
                        s.totalBytes,
                        bytesPerType));
                }
            }

            Dictionary<int, List<(ulong MethodTable, long Count, ulong TotalSize)>>? topTypesByModuleId = null;
            if (heavyModuleIds.Count > 0)
            {
                topTypesByModuleId = new Dictionary<int, List<(ulong, long, ulong)>>(heavyModuleIds.Count);
                foreach (var kv in aggregates)
                {
                    var agg = kv.Value;
                    if (!heavyModuleIds.Contains(agg.ModuleId)) continue;

                    if (!topTypesByModuleId.TryGetValue(agg.ModuleId, out var typeList))
                    {
                        typeList = new List<(ulong, long, ulong)>(TopTypesPerHeavyModuleToShow);
                        topTypesByModuleId[agg.ModuleId] = typeList;
                    }
                    InsertTopType(typeList, TopTypesPerHeavyModuleToShow, agg.MethodTable, agg.Count, agg.TotalSize);
                }
            }

            topByMemory.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            var topStats = new List<ModuleHeapStats>(topByMemory.Count);
            for (int i = 0; i < topByMemory.Count; i++)
            {
                int id = topByMemory[i].Id;
                var mod = modules[id];
                var s = statsById[id];

                IReadOnlyList<ModuleTypeUsage>? topTypes = null;
                if (topTypesByModuleId is not null && topTypesByModuleId.TryGetValue(id, out var typeList))
                {
                    var usages = new List<ModuleTypeUsage>(typeList.Count);
                    for (int t = 0; t < typeList.Count; t++)
                    {
                        var (mt, count, totalSize) = typeList[t];
                        usages.Add(new ModuleTypeUsage(resolveTypeName!(mt) ?? "Unknown", count, totalSize));
                    }
                    topTypes = usages;
                }

                topStats.Add(new ModuleHeapStats(
                    System.IO.Path.GetFileName(mod.Name ?? string.Empty),
                    mod.AssemblyName ?? string.Empty,
                    s.uniqueTypes,
                    s.objectCount,
                    s.totalBytes,
                    s.lohBytes,
                    s.gen2ObjectCount,
                    topTypes));
            }

            density.Sort((a, b) => b.BytesPerType.CompareTo(a.BytesPerType));

            return (topStats, density);
        }

        // Maintains a small (<= cap) descending-by-TotalSize list. Re-sorting on every insert is
        // fine at cap=10: O(cap log cap), and only heavy modules' types reach this path.
        private static void InsertTopType(List<(ulong MethodTable, long Count, ulong TotalSize)> list, int cap, ulong methodTable, long count, ulong totalSize)
        {
            if (list.Count < cap)
            {
                list.Add((methodTable, count, totalSize));
                list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
                return;
            }

            if (totalSize <= list[^1].TotalSize) return;
            list[^1] = (methodTable, count, totalSize);
            list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
        }
    }
}
