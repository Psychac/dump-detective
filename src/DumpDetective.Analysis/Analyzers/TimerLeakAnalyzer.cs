using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the heap for timer-related objects that commonly accumulate when timers are not disposed.
///
/// Covered framework types:
///   - System.Threading.Timer
///   - System.Timers.Timer
///   - System.Threading.TimerQueueTimer / TimerHolder
/// </summary>
public sealed class TimerLeakAnalyzer : IAnalyzer
{
    public string Name => "Timer Leak Analysis";
    public string Category => "Infrastructure";

    private enum TimerObjectCategory
    {
        None,
        ThreadingTimer,
        TimersTimer,
        TimerQueueTimer,
        TimerHolder,
        OtherTimer
    }

    private static TimerObjectCategory ClassifyType(string typeName)
    {
        if (typeName.Equals("System.Threading.Timer", StringComparison.Ordinal))
            return TimerObjectCategory.ThreadingTimer;
        if (typeName.Equals("System.Timers.Timer", StringComparison.Ordinal))
            return TimerObjectCategory.TimersTimer;
        if (typeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal))
            return TimerObjectCategory.TimerQueueTimer;
        if (typeName.Equals("System.Threading.TimerHolder", StringComparison.Ordinal))
            return TimerObjectCategory.TimerHolder;

        if (typeName.StartsWith("System.Threading.", StringComparison.Ordinal)
            && typeName.Contains("Timer", StringComparison.Ordinal))
            return TimerObjectCategory.OtherTimer;

        if (typeName.StartsWith("System.Timers.", StringComparison.Ordinal)
            && typeName.Contains("Timer", StringComparison.Ordinal))
            return TimerObjectCategory.OtherTimer;

        return TimerObjectCategory.None;
    }

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(ClrHeap? heap, IHeapAnalysisCache? cache, CancellationToken cancellationToken)
    {
        if (heap is null)
            return Empty();

        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        if (cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            typeAggregates = idx?.TypeAggregates;

        var candidateMts = new Dictionary<ulong, (string TypeName, TimerObjectCategory Category, long Count, ulong Bytes)>(16);

        if (typeAggregates is not null)
        {
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                if (clrType?.Name is not string fullName)
                    continue;

                TimerObjectCategory category = ClassifyType(fullName);
                if (category == TimerObjectCategory.None)
                    continue;

                candidateMts[kv.Key] = (fullName, category, kv.Value.Count, kv.Value.TotalSize);
            }
        }
        else
        {
            var seenMts = new HashSet<ulong>();
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!obj.IsValid || obj.Type is null)
                    continue;

                ulong mt = obj.Type.MethodTable;
                if (!seenMts.Add(mt))
                    continue;

                string typeName = obj.Type.Name ?? string.Empty;
                TimerObjectCategory category = ClassifyType(typeName);
                if (category == TimerObjectCategory.None)
                    continue;

                candidateMts[mt] = (typeName, category, 0, 0);
            }
        }

        if (candidateMts.Count == 0)
            return Empty();

        int threadingTimerCount = 0;
        int timersTimerCount = 0;
        int timerQueueTimerCount = 0;
        int timerHolderCount = 0;
        int otherTimerCount = 0;
        ulong totalBytes = 0;

        var byType = new List<TimerObjectTypeSummary>(candidateMts.Count);

        foreach (KeyValuePair<ulong, (string TypeName, TimerObjectCategory Category, long Count, ulong Bytes)> kv in candidateMts)
        {
            int count = (int)Math.Min(kv.Value.Count, int.MaxValue);
            ulong bytes = kv.Value.Bytes;

            switch (kv.Value.Category)
            {
                case TimerObjectCategory.ThreadingTimer:
                    threadingTimerCount += count;
                    break;
                case TimerObjectCategory.TimersTimer:
                    timersTimerCount += count;
                    break;
                case TimerObjectCategory.TimerQueueTimer:
                    timerQueueTimerCount += count;
                    break;
                case TimerObjectCategory.TimerHolder:
                    timerHolderCount += count;
                    break;
                case TimerObjectCategory.OtherTimer:
                    otherTimerCount += count;
                    break;
            }

            totalBytes += bytes;
            byType.Add(new TimerObjectTypeSummary(kv.Value.TypeName, count, bytes));
        }

        byType.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        PopulateEvidence(heap, cache, byType);

        int total = threadingTimerCount + timersTimerCount + timerQueueTimerCount + timerHolderCount + otherTimerCount;

        return new TimerLeakDomainResult(
            TimersFound: total > 0,
            TotalTimers: total,
            ThreadingTimerCount: threadingTimerCount,
            TimersTimerCount: timersTimerCount,
            TimerQueueTimerCount: timerQueueTimerCount,
            TimerHolderCount: timerHolderCount,
            OtherTimerCount: otherTimerCount,
            TotalBytes: totalBytes,
            ByType: byType);
    }

    private static void PopulateEvidence(ClrHeap heap, IHeapAnalysisCache? cache, List<TimerObjectTypeSummary> byType)
    {
        if (cache is null || byType.Count == 0)
            return;

        IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);

        var provider = new ReferenceGraph(heap);
        var limits = new RootPathSearchLimits
        {
            MaxCandidateNodes = 5_000,
            MaxCandidateDepth = 8,
            MaxRootExpansionDepth = 12,
            LargeFanoutThreshold = 100,
        };
        var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false);

        for (int i = 0; i < byType.Count; i++)
        {
            TimerObjectTypeSummary summary = byType[i];
            ulong? sampleAddress = cache.GetSampleInstanceAddress(summary.TypeName);
            if (sampleAddress is null)
                continue;

            bool found = finder.TryFindAnyRootPath(sampleAddress.Value, roots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
            string? rootPath = found ? RootPathSearchSupport.FormatPath(heap, rootKind!, addresses) : null;

            byType[i] = summary with
            {
                Evidence = new Evidence(
                    summary.TotalBytes,
                    rootPath,
                    searchTruncated,
                    [new EvidenceSignal("InstanceCount", "Instances of this timer type", summary.Count)])
            };
        }
    }

    private static TimerLeakDomainResult Empty() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, []);
}
