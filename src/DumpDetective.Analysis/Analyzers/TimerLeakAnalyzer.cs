using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
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
public sealed class TimerLeakAnalyzer : IAnalyzer, ITypedResourceCandidateSource, IRequiresReachableGraphIndex
{
    public string Name => "Timer Leak Analysis";
    public string Category => "Infrastructure";

    private static readonly string[] OtherTimerNamespacePrefixes = ["System.Threading.", "System.Timers."];
    private static readonly string[] OtherTimerTokens = ["Timer"];
    private static readonly string[] ClrInternalTimerTypes = [
        "System.Threading.TimerQueue",
        "System.Threading.TimerThread",
    ];

    /// <summary>
    /// Exact-match timer wrapper/queue type names classified with a dedicated
    /// <see cref="TimerObjectCategory"/> (excludes the generic "OtherTimer" namespace/token
    /// fallback). Exposed for <c>LeakCandidateAnalyzer</c> to correlate its per-type candidate
    /// rows with <see cref="TimerLeakDomainResult.LogicalTimerCount"/> without duplicating these
    /// literals.
    /// </summary>
    public static readonly IReadOnlySet<string> NamedTimerWrapperTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Threading.Timer",
        "System.Timers.Timer",
        "System.Threading.TimerQueueTimer",
        "System.Threading.TimerHolder",
        "System.Threading.PeriodicTimer",
    };

    public bool IsCandidateType(string typeName) => ClassifyType(typeName) != TimerObjectCategory.None;

    // Only System.Threading.TimerQueueTimer exposes the underlying `_period`; buckets are fixed
    // categories (not sorted by count) so the table reads left-to-right from tightest to loosest.
    private static readonly string[] IntervalBucketLabels = ["< 100 ms", "100 ms – 1 s", "> 1 s", "Infinite"];

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress, cancellationToken).Stamp(this));
    }

    private AnalyzerDomainResult Analyze(ClrHeap? heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
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

        IReadOnlyList<(string Bucket, int Count)> intervalHistogram =
            BuildIntervalHistogram(heap, cache, candidates, progress, cancellationToken);

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
            ByType: byType,
            IntervalHistogram: intervalHistogram);
    }

    // For each timer type, samples a bounded set of instances and asks RootPathFinder for a
    // GC root path to each — the resulting root kind/field feeds the leak narrative (finding
    // text, evidence) attached to that type's summary.
    private static void PopulateEvidence(ClrHeap heap, IHeapAnalysisCache? cache, List<TimerObjectTypeSummary> byType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cache is null || byType.Count == 0)
            return;

        IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);

        var provider = new ReferenceGraph(heap);

        // Still needed with a reverse index provider: IndexBackedBidirectionalSearch bounds its
        // own forward/backward expansion with these same limits (MaxCandidateNodes,
        // MaxRootExpansionDepth, LargeFanoutThreshold) — they aren't a legacy-only fallback.
        var limits = new RootPathSearchLimits
        {
            MaxCandidateNodes = 5_000,
            MaxCandidateDepth = 8,
            MaxRootExpansionDepth = 12,
            LargeFanoutThreshold = 100,
        };
        var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false, cache.TryGetReverseIndexProvider(), cache);

        for (int i = 0; i < byType.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimerObjectTypeSummary summary = byType[i];
            ulong? sampleAddress = cache.GetSampleInstanceAddress(summary.TypeName);
            if (sampleAddress is null)
                continue;

            TimerStateSnapshot? snapshot = TrySampleTimerState(heap, sampleAddress.Value, summary.TypeName);

            bool found = finder.TryFindAnyRootPath(sampleAddress.Value, roots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
            string? rootPath = found ? RootPathSearchSupport.FormatPath(heap, rootKind!, addresses, cache) : null;

            byType[i] = summary with
            {
                Evidence = new Evidence(
                    summary.TotalBytes,
                    rootPath,
                    searchTruncated,
                    [new EvidenceSignal("InstanceCount", "Instances of this timer type", summary.Count)]),
                Samples = snapshot != null ? [snapshot] : null
            };
        }
    }

    // Only System.Threading.TimerQueueTimer exposes a period/callback-owner worth reporting — the
    // other timer wrapper types don't hold that state directly on the object read here.
    private static TimerStateSnapshot? TrySampleTimerState(ClrHeap heap, ulong address, string typeName)
    {
        if (!typeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal))
            return null;

        long periodMs = TryReadPeriod(heap, address);
        string? callbackOwnerType = TryReadCallbackOwner(heap, address);
        GenerationTag generation = GenerationTagResolver.Resolve(heap, address);

        return new TimerStateSnapshot(address, generation, periodMs, callbackOwnerType);
    }

    private static long TryReadPeriod(ClrHeap heap, ulong address)
    {
        try
        {
            var obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return -1;

            var periodField = obj.Type.GetFieldByName("_period");
            if (periodField == null)
                return -1;

            return TryReadPeriodField(periodField, address);
        }
        catch
        {
            return -1;
        }
    }

    // `_period` is `uint` on some runtimes and `long` on others (TimerQueueTimer's backing field
    // shape changed across .NET versions); try the narrower read first since it's the common case.
    private static long TryReadPeriodField(ClrInstanceField periodField, ulong address)
    {
        try
        {
            int intVal = periodField.Read<int>(address, interior: false);
            return intVal;
        }
        catch
        {
            try
            {
                long longVal = periodField.Read<long>(address, interior: false);
                return longVal;
            }
            catch
            {
                return -1;
            }
        }
    }

    // TypeAggregates only retain one sample address per type, so an interval distribution needs a
    // second exact heap pass over every TimerQueueTimer instance — mirrors the pattern used by
    // AsyncStateMachineAnalyzer's suspend-state histogram.
    private static IReadOnlyList<(string Bucket, int Count)> BuildIntervalHistogram(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidates,
        IProgress<AnalyzerProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var periodFieldByMt = new Dictionary<ulong, ClrInstanceField>();
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidates)
        {
            if (!kv.Value.TypeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal))
                continue;

            ClrInstanceField? periodField = heap.GetTypeByMethodTable(kv.Key)?.GetFieldByName("_period");
            if (periodField != null)
                periodFieldByMt[kv.Key] = periodField;
        }

        int lessThan100Ms = 0, between100MsAnd1s = 0, moreThan1s = 0, infinite = 0;

        if (periodFieldByMt.Count > 0)
        {
            bool hasDiskIndex = cache != null && cache.EnumerateIndexedEntriesAsTuples().Any();
            IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> entries = hasDiskIndex
                ? cache!.EnumerateIndexedEntriesAsTuples()
                : LiveHeapEntries(heap);

            var scanCounter = new ObjectScanCounter(
                "scanning timer instances for interval histogram",
                progress, reportEveryObjects: 50_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

            foreach ((ulong address, ulong mt, ulong _) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanCounter.Tick();

                if (!periodFieldByMt.TryGetValue(mt, out ClrInstanceField? periodField))
                    continue;

                long periodMs = TryReadPeriodField(periodField, address);
                if (periodMs < 0)
                    infinite++;
                else if (periodMs < 100)
                    lessThan100Ms++;
                else if (periodMs < 1_000)
                    between100MsAnd1s++;
                else
                    moreThan1s++;
            }

            scanCounter.Complete();
        }

        return
        [
            (IntervalBucketLabels[0], lessThan100Ms),
            (IntervalBucketLabels[1], between100MsAnd1s),
            (IntervalBucketLabels[2], moreThan1s),
            (IntervalBucketLabels[3], infinite),
        ];
    }

    // Fallback for in-memory cache mode (no disk-backed object index available).
    private static IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> LiveHeapEntries(ClrHeap heap)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null) continue;
            yield return (obj.Address, obj.Type.MethodTable, obj.Size);
        }
    }

    private static string? TryReadCallbackOwner(ClrHeap heap, ulong address)
    {
        try
        {
            var obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return null;

            var callbackField = obj.Type.GetFieldByName("_timerCallback");
            if (callbackField == null)
                return null;

            var callbackObj = callbackField.ReadObject(address, interior: false);
            if (!callbackObj.IsValid || callbackObj.Type == null)
                return null;

            var targetField = callbackObj.Type.GetFieldByName("_target");
            if (targetField == null)
                return null;

            var targetObj = targetField.ReadObject(callbackObj.Address, interior: false);
            if (targetObj.IsValid && targetObj.Type != null)
                return targetObj.Type.Name;

            return null;
        }
        catch
        {
            return null;
        }
    }

    #region Helpers

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

        if (IsKnownClrInternalTimerType(typeName))
            return TimerObjectCategory.None;

        if (TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, OtherTimerNamespacePrefixes, null, OtherTimerTokens))
            return TimerObjectCategory.OtherTimer;

        return TimerObjectCategory.None;
    }

    private static bool IsKnownClrInternalTimerType(string typeName)
    {
        for (int i = 0; i < ClrInternalTimerTypes.Length; i++)
        {
            if (typeName.Equals(ClrInternalTimerTypes[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }


    #endregion

    private static TimerLeakDomainResult Empty() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], []);
}

internal enum TimerObjectCategory
{
    None,
    ThreadingTimer,
    TimersTimer,
    TimerQueueTimer,
    TimerHolder,
    PeriodicTimer,
    OtherTimer
}

