using System;
using System.Collections.Generic;
using System.IO;
using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Indexing
{
    internal static class ModuleAggregator
    {
        public static (IReadOnlyList<ModuleHeapStats> TopByMemory, IReadOnlyList<ModuleTypeDensity> DensityAnomalies) Aggregate(
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates,
            IReadOnlyList<DumpDetective.Analysis.Indexing.ModuleInfo>? modules,
            DumpDetective.Core.Options.ModuleAnalysisOptions options)
        {
            if (modules is null) return (Array.Empty<ModuleHeapStats>(), Array.Empty<ModuleTypeDensity>());
            var statsById = new Dictionary<int, (int uniqueTypes, long objectCount, ulong totalBytes)>();

            foreach (var kv in aggregates)
            {
                var agg = kv.Value;
                int id = agg.ModuleId;
                if (id < 0 || id >= modules.Count) continue;

                if (!statsById.TryGetValue(id, out var val))
                    val = (0, 0L, 0UL);
                val.uniqueTypes++;
                val.objectCount += agg.Count;
                val.totalBytes += agg.TotalSize;
                statsById[id] = val;
            }

            var topByMemory = new List<(ulong Bytes, int Id)>(statsById.Count);
            var density = new List<ModuleTypeDensity>();

            foreach (var kv in statsById)
            {
                int id = kv.Key;
                var s = kv.Value;
                topByMemory.Add((s.totalBytes, id));

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

            topByMemory.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            var topStats = new List<ModuleHeapStats>(Math.Min(options.TopModulesByHeapCount, topByMemory.Count));
            for (int i = 0; i < topByMemory.Count && i < options.TopModulesByHeapCount; i++)
            {
                int id = topByMemory[i].Id;
                var mod = modules[id];
                var s = statsById[id];
                topStats.Add(new ModuleHeapStats(
                    System.IO.Path.GetFileName(mod.Name ?? string.Empty),
                    mod.AssemblyName ?? string.Empty,
                    s.uniqueTypes,
                    s.objectCount,
                    s.totalBytes));
            }

            density.Sort((a, b) => b.BytesPerType.CompareTo(a.BytesPerType));

            return (topStats, density);
        }
    }
}
