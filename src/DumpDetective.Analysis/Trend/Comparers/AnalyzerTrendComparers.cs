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
                MetricDeltaHelper.Compute("gc.gen2.bytes", null, b.Gen2Bytes, c.Gen2Bytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.bytes", null, b.LohBytes, c.LohBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.percent", null, b.LohPercent, c.LohPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gc.loh.objects", null, b.LohObjects, c.LohObjects, "objects", MetricTrendDirection.HigherIsWorse)
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
                new("leak.duplicate.strings", null, r.DuplicateStringPatternCount, "patterns", MetricTrendDirection.HigherIsWorse),
                new("leak.duplicate.string.bytes", null, r.DuplicateStringWastedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                new("leak.highly.referenced", null, r.HighlyReferencedObjectCount, "objects", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not MemoryLeakDomainResult b || current is not MemoryLeakDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("leak.finalizer.count", null, b.FinalizerQueueCount, c.FinalizerQueueCount, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("leak.duplicate.strings", null, b.DuplicateStringPatternCount, c.DuplicateStringPatternCount, "patterns", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("leak.duplicate.string.bytes", null, b.DuplicateStringWastedBytes, c.DuplicateStringWastedBytes, "bytes", MetricTrendDirection.HigherIsWorse),
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
                new("gchandle.total", null, r.TotalHandles, "handles", MetricTrendDirection.HigherIsWorse),
                new("gchandle.strong", null, r.StrongLikeHandles, "handles", MetricTrendDirection.HigherIsWorse),
                new("gchandle.pinned.targets", null, r.PinnedHandleTargets, "targets", MetricTrendDirection.HigherIsWorse)
            ];
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not GCHandleDomainResult b || current is not GCHandleDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("gchandle.total", null, b.TotalHandles, c.TotalHandles, "handles", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("gchandle.pinned.targets", null, b.PinnedHandleTargets, c.PinnedHandleTargets, "targets", MetricTrendDirection.HigherIsWorse)
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
                MetricDeltaHelper.Compute("loh.fragmentation.percent", null, b.FragmentationPercent, c.FragmentationPercent, "%", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.free.bytes", null, b.FreeBytes, c.FreeBytes, "bytes", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("loh.total.bytes", null, b.TotalBytes, c.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse)
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
}


