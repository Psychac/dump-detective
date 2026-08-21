using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
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
public sealed class TimerLeakAnalyzer : IAnalyzer, ITypedResourceCandidateSource, ITypedResourceInstanceSampler<TimerStateSnapshot>, IRequiresReachableGraphIndex
{
    public string Name => "Timer Leak Analysis";
    public string Category => "Infrastructure";

    public int MaxStateSamplesPerType => 100;
    public int TopSampleCap => 20;

    private static readonly string[] OtherTimerNamespacePrefixes = ["System.Threading.", "System.Timers."];
    private static readonly string[] OtherTimerTokens = ["Timer"];
    private static readonly string[] ClrInternalTimerTypes = [
        "System.Threading.TimerQueue",
        "System.Threading.TimerThread",
    ];


    public bool IsCandidateType(string typeName) => ClassifyType(typeName) != TimerObjectCategory.None;

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

        var sampler = new TimerLeakAnalyzer();
        var samplesByType = new Dictionary<string, List<TimerStateSnapshot>>(byType.Count);

        for (int i = 0; i < byType.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimerObjectTypeSummary summary = byType[i];
            ulong? sampleAddress = cache.GetSampleInstanceAddress(summary.TypeName);
            if (sampleAddress is null)
                continue;

            var samples = new List<TimerStateSnapshot>(sampler.MaxStateSamplesPerType);
            var entry = new HeapEntry(sampleAddress.Value, 0, 0);
            var snapshot = (sampler as ITypedResourceInstanceSampler<TimerStateSnapshot>).TrySample(heap, entry, summary.TypeName);
            if (snapshot != null)
                samples.Add(snapshot);

            bool found = finder.TryFindAnyRootPath(sampleAddress.Value, roots, out string? rootKind, out List<ulong>? addresses, out bool searchTruncated, out _, out _);
            string? rootPath = found ? RootPathSearchSupport.FormatPath(heap, rootKind!, addresses, cache) : null;

            byType[i] = summary with
            {
                Evidence = new Evidence(
                    summary.TotalBytes,
                    rootPath,
                    searchTruncated,
                    [new EvidenceSignal("InstanceCount", "Instances of this timer type", summary.Count)]),
                Samples = samples.Count > 0 ? samples : null
            };
        }
    }

    TimerStateSnapshot? ITypedResourceInstanceSampler<TimerStateSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        if (!typeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal))
            return null;

        long periodMs = TryReadPeriod(heap, entry.Address);
        string? callbackOwnerType = TryReadCallbackOwner(heap, entry.Address);

        return new TimerStateSnapshot(entry.Address, (uint)entry.Generation, periodMs, callbackOwnerType);
    }

    private static long TryReadPeriod(ClrHeap heap, ulong address)
    {
        try
        {
            // TODO: Can extract the field extraction logic in a helper.
            var obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return -1;

            var periodField = obj.Type.GetFieldByName("_period");
            if (periodField == null)
                return -1;

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
        catch
        {
            return -1;
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
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
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

