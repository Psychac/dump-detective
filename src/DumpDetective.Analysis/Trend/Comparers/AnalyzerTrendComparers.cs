using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal static class MetricDeltaHelper
    {
        public static MetricDelta Compute(
            string key, string? scope,
            double baseline, double current,
            string unit, MetricTrendDirection direction)
        {
            double delta = current - baseline;
            double? deltaPercent = Math.Abs(baseline) > double.Epsilon
                ? delta * 100.0 / baseline
                : null;
            return new MetricDelta(key, scope, baseline, current, delta, deltaPercent, unit, direction);
        }
    }

    internal sealed class MemoryAnalyzerTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Memory Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not MemoryDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("memory.total.bytes", null, r.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("memory.loh.bytes", null, r.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("memory.loh.percent", null, r.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("memory.unique.types", null, r.UniqueTypes, "types", MetricTrendDirection.Neutral)
            };
            foreach (var t in r.TopTypesBySize.Take(10))
                metrics.Add(new("type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            foreach (var t in r.TopTypesByCount.Take(10))
                metrics.Add(new("type.count", t.TypeName, t.Count, "objects", MetricTrendDirection.HigherIsWorse));
            // Histogram bucket counts — useful for spotting shifts in allocation size profile
            if (r.SizeBucketHistogram is { Count: > 0 })
            {
                for (int i = 0; i < r.SizeBucketHistogram.Count; i++)
                {
                    var b = r.SizeBucketHistogram[i];
                    metrics.Add(new($"memory.bucket.{i}.count", b.RangeLabel, b.ObjectCount, "objects", MetricTrendDirection.Neutral));
                }
            }
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not MemoryDomainResult b || current is not MemoryDomainResult c) return [];
            var deltas = new List<MetricDelta>
            {
                MetricDeltaHelper.Compute("memory.total.bytes", null, b.TotalBytes, c.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("memory.loh.bytes", null, b.LohBytes, c.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("memory.loh.percent", null, b.LohPercent, c.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("memory.unique.types", null, b.UniqueTypes, c.UniqueTypes, "types", MetricTrendDirection.Neutral)
            };
            var baseTypeMap = b.TopTypesBySize.ToDictionary(t => t.TypeName, StringComparer.Ordinal);
            foreach (var t in c.TopTypesBySize)
            {
                if (baseTypeMap.TryGetValue(t.TypeName, out var bt))
                    deltas.Add(MetricDeltaHelper.Compute("type.bytes", t.TypeName, bt.TotalBytes, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }
            // Histogram bucket deltas
            if (b.SizeBucketHistogram is { Count: > 0 } && c.SizeBucketHistogram is { Count: > 0 })
            {
                int bucketCount = Math.Min(b.SizeBucketHistogram.Count, c.SizeBucketHistogram.Count);
                for (int i = 0; i < bucketCount; i++)
                {
                    deltas.Add(MetricDeltaHelper.Compute(
                        $"memory.bucket.{i}.count",
                        c.SizeBucketHistogram[i].RangeLabel,
                        b.SizeBucketHistogram[i].ObjectCount,
                        c.SizeBucketHistogram[i].ObjectCount,
                        "objects",
                        MetricTrendDirection.Neutral));
                }
            }
            return deltas;
        }
    }

    internal sealed class GCGenerationTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Generation Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCGenerationDomainResult r) return [];
            return
            [
                new("gc.gen2.bytes", null, r.Gen2Bytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("gc.loh.bytes", null, r.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("gc.loh.percent", null, r.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("gc.total.objects", null, r.TotalObjects, "objects", MetricTrendDirection.Neutral),
                new("gc.loh.objects", null, r.LohObjects, "objects", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not GCGenerationDomainResult b || current is not GCGenerationDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("gc.gen2.bytes",     null, b.Gen2Bytes,     c.Gen2Bytes,     "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.bytes",      null, b.LohBytes,      c.LohBytes,      "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.percent",    null, b.LohPercent,    c.LohPercent,    "%",       MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.total.objects",  null, b.TotalObjects,  c.TotalObjects,  "objects", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("gc.loh.objects",    null, b.LohObjects,    c.LohObjects,    "objects", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class ModuleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Module Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ModuleDomainResult r) return [];
            return
            [
                new("modules.total", null, r.TotalModules, "modules", MetricTrendDirection.Neutral),
                new("modules.dynamic", null, r.DynamicModules, "modules", MetricTrendDirection.Neutral),
                new("modules.conflicts", null, r.VersionConflictGroups, "conflicts", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ModuleDomainResult b || current is not ModuleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("modules.total", null, b.TotalModules, c.TotalModules, "modules", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("modules.conflicts", null, b.VersionConflictGroups, c.VersionConflictGroups, "conflicts", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class CrashTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Crash Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not CrashDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("crash.exceptions.total", null, r.TotalExceptions, "exceptions", MetricTrendDirection.HigherIsWorse),
                new("crash.exceptions.active", null, r.ActiveExceptions, "exceptions", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in r.ExceptionTypeCounts)
                metrics.Add(new("crash.exception.type", kv.Key, kv.Value, "count", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not CrashDomainResult b || current is not CrashDomainResult c) return [];
            var deltas = new List<MetricDelta>
            {
                MetricDeltaHelper.Compute("crash.exceptions.total", null, b.TotalExceptions, c.TotalExceptions, "exceptions", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("crash.exceptions.active", null, b.ActiveExceptions, c.ActiveExceptions, "exceptions", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in c.ExceptionTypeCounts)
            {
                b.ExceptionTypeCounts.TryGetValue(kv.Key, out int baseCount);
                deltas.Add(MetricDeltaHelper.Compute("crash.exception.type", kv.Key, baseCount, kv.Value, "count", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }

    internal sealed class HangTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Hang Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not HangDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("hang.alive.threads", null, r.TotalAliveThreads, "threads", MetricTrendDirection.Neutral),
                new("hang.waiting.threads", null, r.WaitingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                new("hang.waiting.percent", null, r.WaitingPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("hang.queued.work.items", null, r.QueuedWorkItems, "items", MetricTrendDirection.HigherIsWorse),
                new("hang.pending.tasks", null, r.PendingTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                new("hang.faulted.tasks", null, r.FaultedTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                new("hang.health.score", null, r.HealthScore, "score", MetricTrendDirection.LowerIsWorse),
            };
            foreach (var kv in r.WaitCategoryBreakdown)
                metrics.Add(new("hang.wait.category", kv.Key, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not HangDomainResult b || current is not HangDomainResult c) return [];
            var deltas = new List<MetricDelta>
            {
                MetricDeltaHelper.Compute("hang.waiting.percent", null, b.WaitingPercent, c.WaitingPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.waiting.threads", null, b.WaitingThreadCount, c.WaitingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.queued.work.items", null, b.QueuedWorkItems, c.QueuedWorkItems, "items", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.pending.tasks", null, b.PendingTasks, c.PendingTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.faulted.tasks", null, b.FaultedTasks, c.FaultedTasks, "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("hang.health.score", null, b.HealthScore, c.HealthScore, "score", MetricTrendDirection.LowerIsWorse),
            };
            foreach (var kv in c.WaitCategoryBreakdown)
            {
                b.WaitCategoryBreakdown.TryGetValue(kv.Key, out int baseCount);
                deltas.Add(MetricDeltaHelper.Compute("hang.wait.category", kv.Key, baseCount, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }

    internal sealed class MemoryLeakTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Memory Leak Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not MemoryLeakDomainResult r) return [];
            return
            [
                new("leak.finalizer.count", null, r.FinalizerQueueCount, "objects", MetricTrendDirection.HigherIsWorse),
                new("leak.highly.referenced", null, r.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not MemoryLeakDomainResult b || current is not MemoryLeakDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("leak.finalizer.count", null, b.FinalizerQueueCount, c.FinalizerQueueCount, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("leak.highly.referenced", null, b.HighlyReferencedObjectCount, c.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class CollectionTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Collection Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not CollectionDomainResult r) return [];
            return
            [
                new("collection.total", null, r.TotalCollections, "collections", MetricTrendDirection.Neutral),
                new("collection.wasted.bytes", null, r.TotalWastedMemory, "bytes", MetricTrendDirection.HigherIsWorse),
                new("collection.wasteful.count", null, r.WastefulCollectionCount, "collections", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not CollectionDomainResult b || current is not CollectionDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("collection.wasted.bytes", null, b.TotalWastedMemory, c.TotalWastedMemory, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("collection.wasteful.count", null, b.WastefulCollectionCount, c.WastefulCollectionCount, "collections", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class StaticRootTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Static Root Leak Detection";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not StaticRootDomainResult r) return [];
            return
            [
                new("static.root.count", null, r.RootCount, "roots", MetricTrendDirection.HigherIsWorse),
                new("static.root.retained.bytes", null, r.TotalRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not StaticRootDomainResult b || current is not StaticRootDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("static.root.count", null, b.RootCount, c.RootCount, "roots", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("static.root.retained.bytes", null, b.TotalRetainedBytes, c.TotalRetainedBytes, "bytes", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class ReferenceChainTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Reference Chain Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ReferenceChainDomainResult r) return [];
            return
            [
                new("refchain.retained.percent", null, r.RetainedPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("refchain.retained.samples", null, r.RetainedSamples, "types", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ReferenceChainDomainResult b || current is not ReferenceChainDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("refchain.retained.percent", null, b.RetainedPercent, c.RetainedPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("refchain.retained.samples", null, b.RetainedSamples, c.RetainedSamples, "types", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class ThreadTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Thread Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ThreadDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("thread.alive", null, r.AliveThreadCount, "threads", MetricTrendDirection.Neutral),
                new("thread.blocked", null, r.BlockedThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                new("thread.lock.holding", null, r.LockHoldingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                new("thread.exceptions", null, r.ThreadsWithActiveExceptionsCount, "threads", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in r.WaitPatternBreakdown)
                metrics.Add(new("thread.wait.category", kv.Key, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ThreadDomainResult b || current is not ThreadDomainResult c) return [];
            var deltas = new List<MetricDelta>
            {
                MetricDeltaHelper.Compute("thread.blocked", null, b.BlockedThreadCount, c.BlockedThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("thread.lock.holding", null, b.LockHoldingThreadCount, c.LockHoldingThreadCount, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("thread.exceptions", null, b.ThreadsWithActiveExceptionsCount, c.ThreadsWithActiveExceptionsCount, "threads", MetricTrendDirection.HigherIsWorse)
            };
            foreach (var kv in c.WaitPatternBreakdown)
            {
                b.WaitPatternBreakdown.TryGetValue(kv.Key, out int bCount);
                deltas.Add(MetricDeltaHelper.Compute("thread.wait.category", kv.Key, bCount, kv.Value, "threads", MetricTrendDirection.HigherIsWorse));
            }
            return deltas;
        }
    }

    internal sealed class GCHandleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Handle Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCHandleDomainResult r) return [];
            return
            [
                new("gchandle.total",              null, r.TotalHandles,          "handles", MetricTrendDirection.HigherIsWorse),
                new("gchandle.strong",             null, r.StrongLikeHandles,     "handles", MetricTrendDirection.HigherIsWorse),
                new("gchandle.pinned.targets",     null, r.PinnedHandleTargets,   "targets", MetricTrendDirection.HigherIsWorse),
                new("gchandle.pinned.bytes",       null, r.PinnedRetainedBytes,   "bytes",   MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not GCHandleDomainResult b || current is not GCHandleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("gchandle.total",          null, b.TotalHandles,        c.TotalHandles,        "handles", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gchandle.strong",         null, b.StrongLikeHandles,   c.StrongLikeHandles,   "handles", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gchandle.pinned.targets", null, b.PinnedHandleTargets, c.PinnedHandleTargets, "targets", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gchandle.pinned.bytes",   null, b.PinnedRetainedBytes, c.PinnedRetainedBytes, "bytes",   MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class LohFragmentationTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "LOH Fragmentation Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not LohFragmentationDomainResult r) return [];
            return
            [
                new("loh.fragmentation.percent", null, r.FragmentationPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("loh.free.bytes", null, r.FreeBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("loh.total.bytes", null, r.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("loh.largest.free.block", null, r.LargestFreeBlock, "bytes", MetricTrendDirection.Neutral),
                new("loh.segment.count", null, r.SegmentCount, "segments", MetricTrendDirection.Neutral)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not LohFragmentationDomainResult b || current is not LohFragmentationDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("loh.fragmentation.percent", null, b.FragmentationPercent, c.FragmentationPercent, "%",        MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.free.bytes",            null, b.FreeBytes,             c.FreeBytes,             "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.total.bytes",           null, b.TotalBytes,            c.TotalBytes,            "bytes",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.largest.free.block",    null, b.LargestFreeBlock,      c.LargestFreeBlock,      "bytes",   MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("loh.segment.count",         null, b.SegmentCount,          c.SegmentCount,          "segments",MetricTrendDirection.Neutral)
            ];
        }
    }

    internal sealed class DependentHandleTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Dependent Handle Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not DependentHandleDomainResult r) return [];
            return
            [
                new("dephandle.total", null, r.DependentHandleCount, "handles", MetricTrendDirection.Neutral),
                new("dephandle.unresolved.percent", null, r.UnresolvedPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("dephandle.unresolved.count", null, r.UnresolvedTargetCount, "targets", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not DependentHandleDomainResult b || current is not DependentHandleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("dephandle.total", null, b.DependentHandleCount, c.DependentHandleCount, "handles", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("dephandle.unresolved.percent", null, b.UnresolvedPercent, c.UnresolvedPercent, "%", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class ThreadStackClusterTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Thread Stack Cluster Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ThreadStackClusterDomainResult r) return [];
            return
            [
                new("cluster.alive.threads", null, r.AliveThreadCount, "threads", MetricTrendDirection.Neutral),
                new("cluster.unique", null, r.UniqueClusters, "clusters", MetricTrendDirection.Neutral),
                new("cluster.diversity.percent", null, r.DiversityPercent, "%", MetricTrendDirection.LowerIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ThreadStackClusterDomainResult b || current is not ThreadStackClusterDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("cluster.diversity.percent", null, b.DiversityPercent, c.DiversityPercent, "%", MetricTrendDirection.LowerIsWorse),
                MetricDeltaHelper.Compute("cluster.unique", null, b.UniqueClusters, c.UniqueClusters, "clusters", MetricTrendDirection.Neutral)
            ];
        }
    }

    internal sealed class EventLeakTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Event Leak Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not EventLeakDomainResult r) return [];
            return
            [
                new("event.leak.instances", null, r.TotalEventLeakInstances, "events", MetricTrendDirection.HigherIsWorse),
                new("event.total.subscribers", null, r.TotalSubscribers, "subscribers", MetricTrendDirection.HigherIsWorse),
                new("event.static.leaks", null, r.StaticEventLeakCount, "events", MetricTrendDirection.HigherIsWorse),
                new("event.instance.leaks", null, r.InstanceEventLeakCount, "events", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not EventLeakDomainResult b || current is not EventLeakDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("event.leak.instances", null, b.TotalEventLeakInstances, c.TotalEventLeakInstances, "events", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("event.total.subscribers", null, b.TotalSubscribers, c.TotalSubscribers, "subscribers", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("event.static.leaks", null, b.StaticEventLeakCount, c.StaticEventLeakCount, "events", MetricTrendDirection.HigherIsWorse)
            ];
        }
    }

    internal sealed class LockGraphTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Lock Graph Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not LockGraphDomainResult r) return [];
            return
            [
                new("lock.held", null, r.TotalHeldLocks, "locks", MetricTrendDirection.Neutral),
                new("lock.contested", null, r.ContestedLockCount, "locks", MetricTrendDirection.HigherIsWorse),
                new("lock.max.waiters", null, r.MaxWaitersOnSingleLock, "threads", MetricTrendDirection.HigherIsWorse),
                new("lock.deadlock.candidates", null, r.DeadlockCandidateCount, "threads", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not LockGraphDomainResult b || current is not LockGraphDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("lock.contested", null, b.ContestedLockCount, c.ContestedLockCount, "locks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("lock.max.waiters", null, b.MaxWaitersOnSingleLock, c.MaxWaitersOnSingleLock, "threads", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("lock.deadlock.candidates", null, b.DeadlockCandidateCount, c.DeadlockCandidateCount, "threads", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }

    internal sealed class SegmentTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Segment Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not SegmentAnalysisDomainResult r) return [];
            return
            [
                new("segment.total", null, r.TotalSegments, "segments", MetricTrendDirection.Neutral),
                new("segment.committed.bytes", null, r.TotalCommittedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("segment.loh.bytes", null, r.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("segment.loh.percent", null, r.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                new("segment.poh.bytes", null, r.PohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("segment.poh.percent", null, r.PohPercent, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not SegmentAnalysisDomainResult b || current is not SegmentAnalysisDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("segment.total", null, b.TotalSegments, c.TotalSegments, "segments", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("segment.committed.bytes", null, b.TotalCommittedBytes, c.TotalCommittedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.loh.bytes", null, b.LohBytes, c.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.loh.percent", null, b.LohPercent, c.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.poh.bytes", null, b.PohBytes, c.PohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("segment.poh.percent", null, b.PohPercent, c.PohPercent, "%", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }

    internal sealed class StringTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "String Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not StringDomainResult r) return [];
            return
            [
                new("string.total", null, r.TotalStrings, "objects", MetricTrendDirection.HigherIsWorse),
                new("string.total.bytes", null, r.TotalStringMemoryBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("string.unique", null, r.UniqueStrings, "objects", MetricTrendDirection.Neutral),
                new("string.duplicate.patterns", null, r.DuplicatePatternCount, "patterns", MetricTrendDirection.HigherIsWorse),
                new("string.duplicate.wasted.bytes", null, r.DuplicateWastedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("string.duplication.ratio", null, r.DuplicationRatio, "ratio", MetricTrendDirection.HigherIsWorse),
                new("string.loh.bytes", null, r.LohStringBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("string.pct.heap", null, r.PctOfManagedHeap, "%", MetricTrendDirection.HigherIsWorse),
                new("string.gen2.count", null, r.Gen2StringCount, "objects", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not StringDomainResult b || current is not StringDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("string.total", null, b.TotalStrings, c.TotalStrings, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.total.bytes", null, b.TotalStringMemoryBytes, c.TotalStringMemoryBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.unique", null, b.UniqueStrings, c.UniqueStrings, "objects", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("string.duplicate.patterns", null, b.DuplicatePatternCount, c.DuplicatePatternCount, "patterns", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.duplicate.wasted.bytes", null, b.DuplicateWastedBytes, c.DuplicateWastedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.duplication.ratio", null, b.DuplicationRatio, c.DuplicationRatio, "ratio", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.loh.bytes", null, b.LohStringBytes, c.LohStringBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.pct.heap", null, b.PctOfManagedHeap, c.PctOfManagedHeap, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("string.gen2.count", null, b.Gen2StringCount, c.Gen2StringCount, "objects", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }

    internal sealed class AsyncTaskTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Async Task Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AsyncTaskDomainResult r) return [];
            return
            [
                new("task.total",             null, r.TotalTasks,            "tasks",   MetricTrendDirection.Neutral),
                new("task.pending",           null, r.PendingTasks,          "tasks",   MetricTrendDirection.HigherIsWorse),
                new("task.faulted",           null, r.FaultedTasks,          "tasks",   MetricTrendDirection.HigherIsWorse),
                new("task.canceled",          null, r.CanceledTasks,         "tasks",   MetricTrendDirection.Neutral),
                new("task.orphaned",          null, r.OrphanedTasks,         "tasks",   MetricTrendDirection.HigherIsWorse),
                new("task.chain.depth.max",   null, r.MaxContinuationDepth,  "depth",   MetricTrendDirection.HigherIsWorse),
                new("task.chain.depth.avg",   null, r.AvgContinuationDepth,  "depth",   MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AsyncTaskDomainResult b || current is not AsyncTaskDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("task.total",            null, b.TotalTasks,           c.TotalTasks,           "tasks", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("task.pending",          null, b.PendingTasks,          c.PendingTasks,         "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.faulted",          null, b.FaultedTasks,          c.FaultedTasks,         "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.canceled",         null, b.CanceledTasks,         c.CanceledTasks,        "tasks", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("task.orphaned",         null, b.OrphanedTasks,         c.OrphanedTasks,        "tasks", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.chain.depth.max",  null, b.MaxContinuationDepth,  c.MaxContinuationDepth, "depth", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("task.chain.depth.avg",  null, b.AvgContinuationDepth,  c.AvgContinuationDepth, "depth", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }

    internal sealed class AllocationPatternTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Allocation Pattern Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not AllocationPatternDomainResult r) return [];
            return
            [
                new("alloc.gen0.count.pct",         null, r.Gen0CountPct,          "%",     MetricTrendDirection.Neutral),
                new("alloc.gen1.count.pct",         null, r.Gen1CountPct,          "%",     MetricTrendDirection.Neutral),
                new("alloc.gen2.count.pct",         null, r.Gen2CountPct,          "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.loh.count.pct",          null, r.LohCountPct,           "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.gen0.size.pct",          null, r.Gen0SizePct,           "%",     MetricTrendDirection.Neutral),
                new("alloc.gen1.size.pct",          null, r.Gen1SizePct,           "%",     MetricTrendDirection.Neutral),
                new("alloc.gen2.size.pct",          null, r.Gen2SizePct,           "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.loh.size.pct",           null, r.LohSizePct,            "%",     MetricTrendDirection.HigherIsWorse),
                new("alloc.gc.pressure",            null, (double)r.GCPressure,    "level", MetricTrendDirection.HigherIsWorse),
                new("alloc.promotion.pressure",     null, r.PromotionPressureScore,"score", MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not AllocationPatternDomainResult b || current is not AllocationPatternDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("alloc.gen0.count.pct",     null, b.Gen0CountPct,          c.Gen0CountPct,          "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen1.count.pct",     null, b.Gen1CountPct,          c.Gen1CountPct,          "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen2.count.pct",     null, b.Gen2CountPct,          c.Gen2CountPct,          "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.loh.count.pct",      null, b.LohCountPct,           c.LohCountPct,           "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.gen0.size.pct",      null, b.Gen0SizePct,           c.Gen0SizePct,           "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen1.size.pct",      null, b.Gen1SizePct,           c.Gen1SizePct,           "%",     MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("alloc.gen2.size.pct",      null, b.Gen2SizePct,           c.Gen2SizePct,           "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.loh.size.pct",       null, b.LohSizePct,            c.LohSizePct,            "%",     MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.gc.pressure",        null, (double)b.GCPressure,    (double)c.GCPressure,    "level", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("alloc.promotion.pressure", null, b.PromotionPressureScore,c.PromotionPressureScore,"score", MetricTrendDirection.HigherIsWorse),
            ];
        }
    }

    internal sealed class ObjectShapeTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "Object Shape Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not ObjectShapeAnalyzerDomainResult r) return [];
            return
            [
                new("shape.types.analyzed",    null, r.TotalTypesAnalyzed,   "types", MetricTrendDirection.Neutral),
                new("shape.avg.ref.fields",    null, r.AvgRefFieldsPerType,  "fields", MetricTrendDirection.HigherIsWorse),
                new("shape.ref.heavy.count",   null, r.TopReferenceHeavyTypes.Count, "types", MetricTrendDirection.HigherIsWorse),
                new("shape.val.heavy.count",   null, r.TopValueHeavyTypes.Count,     "types", MetricTrendDirection.Neutral),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not ObjectShapeAnalyzerDomainResult b || current is not ObjectShapeAnalyzerDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("shape.types.analyzed",  null, b.TotalTypesAnalyzed,            c.TotalTypesAnalyzed,            "types",  MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("shape.avg.ref.fields",  null, b.AvgRefFieldsPerType,           c.AvgRefFieldsPerType,           "fields", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("shape.ref.heavy.count", null, (double)b.TopReferenceHeavyTypes.Count, (double)c.TopReferenceHeavyTypes.Count, "types", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("shape.val.heavy.count", null, (double)b.TopValueHeavyTypes.Count,     (double)c.TopValueHeavyTypes.Count,     "types", MetricTrendDirection.Neutral),
            ];
        }
    }

    internal sealed class GCRootTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "GC Root Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not GCRootDomainResult r) return [];
            return
            [
                new("gcroot.total.roots",           null, r.TotalRoots,                     "roots",  MetricTrendDirection.Neutral),
                new("gcroot.top.severity.score",    null, r.TopRootsBySeverity.Count > 0 ? r.TopRootsBySeverity[0].SeverityScore : 0, "score", MetricTrendDirection.HigherIsWorse),
                new("gcroot.path.capped.count",     null, r.PathSearchCappedCount,           "paths",  MetricTrendDirection.Neutral),
                new("gcroot.strong.handle.count",   null, GetKindCount(r, "StrongHandle"),   "roots",  MetricTrendDirection.HigherIsWorse),
                new("gcroot.finalizer.count",       null, GetKindCount(r, "FinalizerQueue"), "roots",  MetricTrendDirection.HigherIsWorse),
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not GCRootDomainResult b || current is not GCRootDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("gcroot.total.roots",         null, b.TotalRoots,                                                                   c.TotalRoots,                                                                   "roots",  MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("gcroot.strong.handle.count", null, (double)GetKindCount(b, "StrongHandle"),   (double)GetKindCount(c, "StrongHandle"),   "roots",  MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gcroot.finalizer.count",     null, (double)GetKindCount(b, "FinalizerQueue"), (double)GetKindCount(c, "FinalizerQueue"), "roots",  MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gcroot.path.capped.count",   null, (double)b.PathSearchCappedCount,           (double)c.PathSearchCappedCount,           "paths",  MetricTrendDirection.Neutral),
            ];
        }

        private static int GetKindCount(GCRootDomainResult r, string kind)
        {
            foreach (RootKindSummary ks in r.ByKind)
                if (ks.Kind == kind) return ks.Count;
            return 0;
        }
    }
}


