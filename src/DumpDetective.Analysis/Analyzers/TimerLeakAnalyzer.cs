using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the heap for timer-related objects that commonly accumulate when timers are not disposed.
///
/// Covered framework types:
///   - System.Threading.Timer
///   - System.Timers.Timer
///   - System.Threading.TimerQueueTimer / TimerHolder
/// </summary>
public sealed class TimerLeakAnalyzer : IAnalyzer, ITypedResourceCandidateSource
{
    public string Name => "Timer Leak Analysis";
    public string Category => "Infrastructure";

    public bool IsCandidateType(string typeName) => ClassifyType(typeName) != TimerObjectCategory.None;

    private enum TimerObjectCategory
    {
        None,
        ThreadingTimer,
        TimersTimer,
        TimerQueueTimer,
        TimerHolder,
        PeriodicTimer,
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
        if (typeName.Equals("System.Threading.PeriodicTimer", StringComparison.Ordinal))
            return TimerObjectCategory.PeriodicTimer;

        if (TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, OtherTimerNamespacePrefixes, null, OtherTimerTokens))
            return TimerObjectCategory.OtherTimer;

        return TimerObjectCategory.None;
    }

    private static readonly string[] OtherTimerNamespacePrefixes = ["System.Threading.", "System.Timers."];
    private static readonly string[] OtherTimerTokens = ["Timer"];

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
    }

    private AnalyzerDomainResult Analyze(ClrHeap? heap, IHeapAnalysisCache? cache, CancellationToken cancellationToken)
    {
        if (heap is null)
            return Empty();

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidates =
            TypedResourceScanDriver.DiscoverCandidates(this, heap, cache, cancellationToken);

        if (candidates.Count == 0)
            return Empty();

        int threadingTimerCount = 0;
        int timersTimerCount = 0;
        int timerQueueTimerCount = 0;
        int timerHolderCount = 0;
        int periodicTimerCount = 0;
        int otherTimerCount = 0;
        ulong totalBytes = 0;

        var byType = new List<TimerObjectTypeSummary>(candidates.Count);

        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(kv.Value.Count, int.MaxValue);
            ulong bytes = kv.Value.Bytes;

            switch (ClassifyType(kv.Value.TypeName))
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
                case TimerObjectCategory.PeriodicTimer:
                    periodicTimerCount += count;
                    break;
                case TimerObjectCategory.OtherTimer:
                    otherTimerCount += count;
                    break;
            }

            totalBytes += bytes;
            byType.Add(new TimerObjectTypeSummary(kv.Value.TypeName, count, bytes));
        }

        byType.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        PopulateEvidence(heap, cache, byType, cancellationToken);

        int total = threadingTimerCount + timersTimerCount + timerQueueTimerCount + timerHolderCount + periodicTimerCount + otherTimerCount;

        return new TimerLeakDomainResult(
            TimersFound: total > 0,
            TotalTimers: total,
            LogicalTimerCount: timerQueueTimerCount,
            ThreadingTimerCount: threadingTimerCount,
            TimersTimerCount: timersTimerCount,
            TimerQueueTimerCount: timerQueueTimerCount,
            TimerHolderCount: timerHolderCount,
            PeriodicTimerCount: periodicTimerCount,
            OtherTimerCount: otherTimerCount,
            TotalBytes: totalBytes,
            ByType: byType);
    }

    private static void PopulateEvidence(ClrHeap heap, IHeapAnalysisCache? cache, List<TimerObjectTypeSummary> byType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
}
