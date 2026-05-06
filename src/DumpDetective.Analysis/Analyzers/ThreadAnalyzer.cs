using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using System.Runtime.InteropServices;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public class ThreadAnalyzer : IAnalyzer
    {
        internal static int ComputeSamplerCapacity(int maxSampled, DumpDetective.Core.Models.DumpSizeTier? tier, int totalThreads)
        {
            int capacity = maxSampled;
            if (tier is not null)
            {
                switch (tier.Value)
                {
                    case DumpDetective.Core.Models.DumpSizeTier.Large:
                        capacity = Math.Max(1, capacity / 4);
                        break;
                    case DumpDetective.Core.Models.DumpSizeTier.Medium:
                        capacity = Math.Max(1, capacity / 2);
                        break;
                    default:
                        break;
                }
            }

            capacity = Math.Min(capacity, Math.Max(0, totalThreads / 10));
            return capacity;
        }
        private static readonly WaitPattern[] WaitPatterns =
        [
            new("MonitorWait", "monitor.wait", "Thread waiting on monitor pulse/event."),
            new("MonitorContention", "monitor.enter", "Thread contending for a lock (monitor)."),
            new("TaskBlocking", "task.wait", "Synchronous wait on task completion."),
            new("TaskBlocking", "task`1.get_result", "Blocking on Task.Result."),
            new("Sleep", "thread.sleep", "Thread is sleeping."),
            new("Semaphore", "semaphore", "Waiting on semaphore permit."),
            new("Mutex", "mutex", "Waiting on mutex ownership."),
            new("WaitHandle", "waithandle", "Waiting on synchronization handle."),
            new("WaitHandle", "manualresetevent", "Waiting on ManualResetEvent."),
            new("WaitHandle", "autoresetevent", "Waiting on AutoResetEvent."),
            new("ThreadJoin", "thread.join", "Waiting for another thread to complete."),
            new("BlockingIO", "socket.receive", "Potentially blocked waiting for network data."),
            new("BlockingIO", "socket.accept", "Potentially blocked accepting network connection."),
            new("BlockingIO", "filestream.read", "Potentially blocked on file I/O.")
        ];

        public string Name => "Thread Analysis";
        public string Category => "Threads";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThreadAnalysisOptions options = context.GetOption<ThreadAnalysisOptions>();
            return ValueTask.FromResult(Analyze(context.Runtime, options, context.Progress, context.Cache).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime)
        {
            return Analyze(runtime, new ThreadAnalysisOptions(), progress: null, cache: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ThreadAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, IHeapAnalysisCache? cache)
        {
            progress?.Report(new(0, "Starting thread analysis"));

            // Prewarm stack-root counts either synchronously or in background
            // depending on options. For Full preset we prefer background prewarm.
            if (cache is not null && options.MaxThreadsToCaptureSnapshots > 0)
            {
                int prewarm = options.MaxThreadsToCaptureSnapshots;
                if (options.PrewarmCacheInBackground)
                {
                    progress?.Report(new(0, "Starting background prewarm of thread stack-root counts"));
                    _ = Task.Run(() =>
                    {
                        int idx = 0;
                        foreach (var t in runtime.Threads)
                        {
                            cache.GetOrCountThreadStackRoots(t, options.MaxStackRootsToCount);
                            if (++idx >= prewarm)
                                break;
                            if ((idx & 0xF) == 0)
                                progress?.Report(new(idx, $"Background prewarm: {idx}/{prewarm}"));
                        }
                        progress?.Report(new(prewarm, $"Background prewarm complete: {Math.Min(prewarm, prewarm)} threads"));
                    });
                }
                else if (cache.SizeTier != DumpDetective.Core.Models.DumpSizeTier.Large)
                {
                    progress?.Report(new(0, "Prewarming thread stack-root counts"));
                    int idx = 0;
                    foreach (var t in runtime.Threads)
                    {
                        cache.GetOrCountThreadStackRoots(t, options.MaxStackRootsToCount);
                        if (++idx >= prewarm)
                            break;
                    }
                    progress?.Report(new(0, $"Prewarmed {Math.Min(prewarm, idx)} threads"));
                }
            }

            progress?.Report(new(0, "Starting thread sampling"));
            var threadInfo = CategorizeThreads(runtime.Threads, options, progress, cache);

            // Decide effective frame window for snapshots (expand when Full requested)
            int effectiveMaxFramesForSnapshot = ComputeEffectiveMaxFramesForSnapshot(options);

            // Materialize limited snapshots without LINQ to avoid iterator allocations in hot paths.
            var locksSnapshots = new List<ThreadStateSnapshot>(Math.Min(options.MaxThreadsToCaptureSnapshots, threadInfo.ThreadsWithLocks.Count));
            for (int i = 0; i < threadInfo.ThreadsWithLocks.Count && locksSnapshots.Count < options.MaxThreadsToCaptureSnapshots; i++)
                locksSnapshots.Add(ToThreadStateSnapshot(threadInfo.ThreadsWithLocks[i], effectiveMaxFramesForSnapshot));

            var blockedSnapshots = new List<ThreadStateSnapshot>(Math.Min(options.MaxThreadsToCaptureSnapshots, threadInfo.PotentiallyBlockedThreads.Count));
            for (int i = 0; i < threadInfo.PotentiallyBlockedThreads.Count && blockedSnapshots.Count < options.MaxThreadsToCaptureSnapshots; i++)
                blockedSnapshots.Add(ToThreadStateSnapshot(threadInfo.PotentiallyBlockedThreads[i], effectiveMaxFramesForSnapshot));

            var exceptionSnapshots = new List<ThreadExceptionSnapshot>(Math.Min(options.MaxThreadsToCaptureSnapshots, threadInfo.ThreadsWithExceptions.Count));
            for (int i = 0; i < threadInfo.ThreadsWithExceptions.Count && exceptionSnapshots.Count < options.MaxThreadsToCaptureSnapshots; i++)
                exceptionSnapshots.Add(ToThreadExceptionSnapshot(threadInfo.ThreadsWithExceptions[i], effectiveMaxFramesForSnapshot));

            var topFrameHotspots = new List<NameCountEntry>(Math.Min(options.MaxTopHotspots, threadInfo.TopFrameHotspots.Count));
            if (threadInfo.TopFrameHotspots.Count > 0)
            {
                var kvpList = new List<KeyValuePair<string, int>>(threadInfo.TopFrameHotspots);
                kvpList.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < kvpList.Count && topFrameHotspots.Count < options.MaxTopHotspots; i++)
                    topFrameHotspots.Add(new NameCountEntry(kvpList[i].Key, kvpList[i].Value));
            }

            var activeThreadHotspots = new List<NameCountEntry>(Math.Min(options.MaxTopHotspots, threadInfo.ActiveThreadHotspots.Count));
            if (threadInfo.ActiveThreadHotspots.Count > 0)
            {
                var kvpList = new List<KeyValuePair<string, int>>(threadInfo.ActiveThreadHotspots);
                kvpList.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < kvpList.Count && activeThreadHotspots.Count < options.MaxTopHotspots; i++)
                    activeThreadHotspots.Add(new NameCountEntry(kvpList[i].Key, kvpList[i].Value));
            }

            progress?.Report(new(threadInfo.TotalCount, "Materializing snapshots"));

            var sampledSnapshots = new List<ThreadStateSnapshot>(Math.Min(options.MaxSampledStackSnapshots, threadInfo.SampledThreads?.Count ?? 0));
            var sampledSource = threadInfo.SampledThreads ?? new List<ThreadWithStackTrace>();
            for (int i = 0; i < sampledSource.Count && sampledSnapshots.Count < options.MaxSampledStackSnapshots; i++)
                sampledSnapshots.Add(ToThreadStateSnapshot(sampledSource[i], effectiveMaxFramesForSnapshot));

            var finalizerFrameStrings = new List<string>(Math.Min(options.MaxFramesForThreadScan, threadInfo.FinalizerFrames?.Count ?? 0));
            if (threadInfo.FinalizerFrames != null)
            {
                for (int i = 0; i < threadInfo.FinalizerFrames.Count && finalizerFrameStrings.Count < options.MaxFramesForThreadScan; i++)
                {
                    var f = threadInfo.FinalizerFrames[i];
                    finalizerFrameStrings.Add(f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType);
                }
            }

            return new ThreadDomainResult(
                    threadInfo.TotalCount,
                    threadInfo.AliveCount,
                    Math.Max(0, threadInfo.TotalCount - threadInfo.AliveCount),
                    threadInfo.GcCount,
                    threadInfo.PotentiallyBlockedThreads.Count,
                    threadInfo.ThreadsWithLocks.Count,
                    threadInfo.ThreadsWithActiveExceptionsCount,
                    threadInfo.BackgroundCount,
                    new Dictionary<string, int>(threadInfo.WaitCategoryDistribution),
                    new Dictionary<string, int>(threadInfo.StateDistribution),
                    new Dictionary<string, int>(threadInfo.AppDomainDistribution),
                    new Dictionary<string, int>(threadInfo.GcModeDistribution),
                    locksSnapshots,
                    blockedSnapshots,
                    exceptionSnapshots,
                    topFrameHotspots,
                    activeThreadHotspots,
                    sampledSnapshots,
                    threadInfo.ThreadPoolCount,
                    threadInfo.FinalizerCount,
                    threadInfo.FinalizerIsBlocked,
                    threadInfo.FinalizerThread != null ? (uint?)threadInfo.FinalizerThread.ManagedThreadId : null,
                    threadInfo.FinalizerThread?.OSThreadId,
                    threadInfo.FinalizerThread != null ? (int)threadInfo.FinalizerThread.LockCount : 0,
                    finalizerFrameStrings,
                    threadInfo.AsyncChainThreadCount,
                    threadInfo.MaxAsyncChainDepth,
                    sampledSnapshots.Count,
                    (locksSnapshots.Count + blockedSnapshots.Count + exceptionSnapshots.Count),
                    options.MaxSampledStackSnapshots,
                    options.SamplingSeed);
        }

        private static ThreadStateSnapshot ToThreadStateSnapshot(ThreadWithStackTrace source, int maxFramesForThreadScan)
        {
            return new ThreadStateSnapshot(
                (uint)source.Thread.ManagedThreadId,
                source.Thread.OSThreadId,
                (int)source.Thread.LockCount,
                FormatThreadState(source.Thread.State),
                source.Thread.GCMode.ToString(),
                source.WaitCategory,
                source.WaitReason,
                source.TopFrames
                    .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                    .Take(maxFramesForThreadScan)
                    .ToList(),
                source.StackRootCount);
        }

        private static ThreadExceptionSnapshot ToThreadExceptionSnapshot(ThreadWithStackTrace source, int maxFramesForThreadScan)
        {
            return new ThreadExceptionSnapshot(
                (uint)source.Thread.ManagedThreadId,
                source.Thread.OSThreadId,
                source.ExceptionType ?? StringConstants.UnknownType,
                source.ExceptionMessage,
                FormatThreadState(source.Thread.State),
                source.Thread.GCMode.ToString(),
                (int)source.Thread.LockCount,
                source.TopFrames
                    .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                    .Take(maxFramesForThreadScan)
                    .ToList(),
                source.StackRootCount);
        }
        private ThreadCategorization CategorizeThreads(IEnumerable<ClrThread> threads, ThreadAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, IHeapAnalysisCache? cache)
        {
            var threadList = threads as IList<ClrThread> ?? threads.ToList();

            var result = new ThreadCategorization();
            var threadsWithLocks = new List<ThreadWithStackTrace>();
            var blockedThreads = new List<ThreadWithStackTrace>();
            var threadsWithExceptions = new List<ThreadWithStackTrace>();
            var stackRootCountByThreadAddress = new Dictionary<ulong, int>();
            var scanCounter = new ObjectScanCounter("Scanning threads", progress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            // Adaptive sampler capacity: reduce sampling on very large dumps to limit work.
            int samplerCapacity = ComputeSamplerCapacity(options.MaxSampledStackSnapshots, cache?.SizeTier, threadList.Count);

            // Reservoir sampler for non-top thread snapshots
            var sampler = new Utilities.ReservoirSampler<ThreadWithStackTrace>(samplerCapacity, options.SamplingSeed);

            foreach (var thread in threadList)
            {
                scanCounter.Tick();

                result.TotalCount++;
                IncrementCount(result.StateDistribution, FormatThreadState(thread.State));
                IncrementCount(result.GcModeDistribution, thread.GCMode.ToString());

                string appDomain = thread.CurrentAppDomain?.Name ?? "<No AppDomain>";
                IncrementCount(result.AppDomainDistribution, appDomain);

                // Cache the property â€” each access reads from CLRMD runtime structures
                var currentException = thread.CurrentException;
                if (currentException != null)
                {
                    result.ThreadsWithActiveExceptionsCount++;
                    string exceptionType = currentException.Type?.Name ?? StringConstants.UnknownType;
                    IncrementCount(result.ExceptionTypeDistribution, exceptionType);
                }

                if (thread.IsAlive)
                {
                    result.AliveCount++;
                    // Enumerate stack once and share the list across all categories for this thread
                    // Avoid LINQ Take in hot path; manually materialize up to max frames.
                    var stackFrames = new List<ClrStackFrame>(options.MaxFramesForThreadScan);
                    foreach (var f in thread.EnumerateStackTrace())
                    {
                        if (stackFrames.Count >= options.MaxFramesForThreadScan)
                            break;
                        stackFrames.Add(f);
                    }
                    TrackTopFrameHotspot(result.TopFrameHotspots, stackFrames);

                    if (currentException != null)
                    {
                        int exceptionStackRoots = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount);
                        threadsWithExceptions.Add(new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames,
                            ExceptionType = currentException.Type?.Name ?? StringConstants.UnknownType,
                            ExceptionMessage = currentException.Message,
                            StackRootCount = exceptionStackRoots
                        });
                    }

                    // Check for locks
                    if (thread.LockCount > 0)
                    {
                        int lockStackRoots = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount);
                        threadsWithLocks.Add(new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames,
                            ExceptionType = currentException?.Type?.Name,
                            StackRootCount = lockStackRoots
                        });
                    }

                    // Detect wait/block patterns across all alive threads — cheap since frames are already materialized
                    var waitDetection = options.DetectWaitPatterns ? DetectWaitPattern(stackFrames) : null;
                    if (waitDetection != null)
                    {
                        IncrementCount(result.WaitCategoryDistribution, waitDetection.Category);
                        int blockedStackRoots = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount);
                        blockedThreads.Add(new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames,
                            WaitCategory = waitDetection.Category,
                            WaitReason = waitDetection.Reason,
                            ExceptionType = currentException?.Type?.Name,
                            StackRootCount = blockedStackRoots
                        });
                    }
                    else if (!thread.IsGc && !thread.IsFinalizer)
                    {
                        // Non-blocked user thread â€” track top frame for the Active Processing group
                        if (options.IncludeStackSamples)
                            TrackTopFrameHotspot(result.ActiveThreadHotspots, stackFrames);
                    }

                    // ThreadPool worker threads surface a recognisable dispatch frame;
                    // TS_TPWorkerThread is the authoritative flag for this version of ClrMD.
                    if (thread.State.HasFlag(ClrThreadState.TS_TPWorkerThread) || IsThreadPoolWorker(stackFrames))
                        result.ThreadPoolCount++;

                    // Capture the finalizer thread's stack and blocked state once
                    if (thread.IsFinalizer)
                    {
                        result.FinalizerThread = thread;
                        result.FinalizerFrames = stackFrames;
                        result.FinalizerIsBlocked = options.DetectWaitPatterns ? DetectWaitPattern(stackFrames) != null : false;
                    }

                    // Count MoveNext frames to measure async state-machine chain depth
                    if (options.AsyncChainDetection != AsyncChainDetectionMode.Disabled)
                    {
                        int moveNextDepth = CountMoveNextDepth(stackFrames);
                        if (moveNextDepth > 0)
                        {
                            result.AsyncChainThreadCount++;
                            if (moveNextDepth > result.MaxAsyncChainDepth)
                                result.MaxAsyncChainDepth = moveNextDepth;

                                // If configured for Full, attempt to capture additional
                                // stack frames (async path) so reports can show representative
                                // async-chain frames. This re-enumerates the thread's stack and
                                // appends extra frames beyond the base `MaxFramesForThreadScan`.
                            if (options.AsyncChainDetection == AsyncChainDetectionMode.Full)
                            {
                                int extraToCapture = options.MaxFramesForThreadScan; // capture an extra window
                                int already = stackFrames.Count;
                                try
                                {
                                    int seen = 0;
                                    foreach (var f in thread.EnumerateStackTrace())
                                    {
                                        if (seen < already)
                                        {
                                            seen++;
                                            continue;
                                        }
                                        stackFrames.Add(f);
                                        if (stackFrames.Count >= already + extraToCapture)
                                            break;
                                    }
                                }
                                catch
                                {
                                    // If extra enumeration fails, silently continue with base frames
                                }
                            }
                        }
                    }

                    // Sample non-top threads when enabled. Use reservoir sampling to cap selection.
                    if (options.IncludeStackSamples && options.MaxSampledStackSnapshots > 0)
                    {
                        // Only consider threads not already recorded in locks/blocked/exceptions lists
                        bool isAlreadyCaptured = thread.LockCount > 0 || waitDetection != null || currentException != null;
                        if (!isAlreadyCaptured)
                        {
                            // candidate sample
                            var candidate = new ThreadWithStackTrace
                            {
                                Thread = thread,
                                TopFrames = stackFrames,
                                ExceptionType = currentException?.Type?.Name,
                                StackRootCount = GetOrCountStackRoots(thread, stackRootCountByThreadAddress, cache, options.MaxStackRootsToCount)
                            };

                            sampler.Add(candidate);
                        }
                    }
                }

                if (thread.IsGc)
                    result.GcCount++;

                if (thread.IsFinalizer)
                    result.FinalizerCount++;

                if (thread.State.HasFlag(ClrThreadState.TS_Background))
                    result.BackgroundCount++;
            }

            // Sort threads with locks by lock count (descending)
            result.ThreadsWithLocks = threadsWithLocks
                .OrderByDescending(t => t.Thread.LockCount)
                .ToList();

            result.PotentiallyBlockedThreads = blockedThreads
                .OrderByDescending(t => t.Thread.LockCount)
                .ToList();

            result.ThreadsWithExceptions = threadsWithExceptions
                .OrderByDescending(t => t.Thread.LockCount)
                .ToList();

            // materialize reservoir samples into the categorization result
            if (sampler.Capacity > 0)
            {
                result.SampledThreads = sampler.Samples().ToList();
                progress?.Report(new(scanCounter.Scanned, "Thread sampling complete"));
            }

            scanCounter.Complete();

            progress?.Report(new(scanCounter.Scanned, "Thread analysis complete"));

            return result;
        }

        private static int GetOrCountStackRoots(ClrThread thread, Dictionary<ulong, int> cache, IHeapAnalysisCache? sharedCache, int maxStackRootsToCount)
        {
            if (thread.Address == 0)
                return 0;

            if (cache.TryGetValue(thread.Address, out int existing))
                return existing;

            int count = sharedCache is not null
                ? sharedCache.GetOrCountThreadStackRoots(thread, maxStackRootsToCount)
                : CountStackRoots(thread, maxStackRootsToCount);

            cache[thread.Address] = count;
            return count;
        }

        private static int CountStackRoots(ClrThread thread, int maxStackRootsToCount)
        {
            int count = 0;
            foreach (var _ in thread.EnumerateStackRoots())
            {
                if (count >= maxStackRootsToCount)
                    break;
                count++;
            }

            return count;
        }

        private WaitDetection? DetectWaitPattern(List<ClrStackFrame> frames)
        {
            foreach (var frame in frames)
            {
                string signature = GetFrameSignature(frame);

                foreach (var pattern in WaitPatterns)
                {
                    if (signature.Contains(pattern.Token, StringComparison.OrdinalIgnoreCase))
                    {
                        return new WaitDetection(pattern.Category, pattern.Reason);
                    }
                }
            }

            return null;
        }

        private static string GetFrameSignature(ClrStackFrame frame)
        {
            // Intentionally avoid frame.ToString() â€” it can return raw hex addresses
            // which pollute hotspot keys and are useless as triage output.
            return frame.Method?.Signature
                ?? frame.FrameName
                ?? string.Empty;
        }

        // Testable helper — sample integer candidate indices deterministically.
        internal static IReadOnlyList<int> SampleCandidateIndices(int totalCandidates, int capacity, int seed)
        {
            var sampler = new Utilities.ReservoirSampler<int>(capacity, seed);
            for (int i = 0; i < totalCandidates; i++) sampler.Add(i);
            return sampler.Samples();
        }

        // Internal helper: compute effective frame window for snapshot materialization.
        internal static int ComputeEffectiveMaxFramesForSnapshot(ThreadAnalysisOptions options)
        {
            return (options.AsyncChainDetection == AsyncChainDetectionMode.Full)
                ? Math.Min(64, options.MaxFramesForThreadScan * 2)
                : options.MaxFramesForThreadScan;
        }

        // Internal test helper: count occurrences of MoveNext() in a list of frame signature strings.
        internal static int CountMoveNextDepthFromSignatures(IReadOnlyList<string> frameSignatures)
        {
            int depth = 0;
            foreach (var sig in frameSignatures)
            {
                if (!string.IsNullOrEmpty(sig) && sig.Contains(".MoveNext()", StringComparison.OrdinalIgnoreCase))
                    depth++;
            }
            return depth;
        }

        private static bool IsThreadPoolWorker(List<ClrStackFrame> frames)
        {
            foreach (var frame in frames)
            {
                string sig = GetFrameSignature(frame);
                if (sig.Contains("ThreadPoolWorkQueue", StringComparison.OrdinalIgnoreCase) ||
                    sig.Contains("ThreadPool.WorkQueue", StringComparison.OrdinalIgnoreCase) ||
                    sig.Contains("PortableThreadPool", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int CountMoveNextDepth(List<ClrStackFrame> frames)
        {
            int depth = 0;
            foreach (var frame in frames)
            {
                if (GetFrameSignature(frame).Contains(".MoveNext()", StringComparison.OrdinalIgnoreCase))
                    depth++;
            }
            return depth;
        }

        private static string FormatThreadState(ClrThreadState state)
        {
            ulong raw = Convert.ToUInt64(state);
            if (raw == 0)
                return "0";

            var parts = new List<string>(capacity: 4);
            ulong remaining = raw;

            foreach (ClrThreadState flag in Enum.GetValues<ClrThreadState>())
            {
                ulong flagValue = Convert.ToUInt64(flag);
                if (flagValue == 0)
                    continue;

                if ((remaining & flagValue) == flagValue)
                {
                    parts.Add(flag.ToString());
                    remaining &= ~flagValue;
                }
            }

            if (parts.Count == 0)
                return $"0x{raw:X}";

            if (remaining != 0)
                parts.Add($"0x{remaining:X}");

            return string.Join(" | ", parts);
        }

        private static void TrackTopFrameHotspot(Dictionary<string, int> hotspots, List<ClrStackFrame> frames)
        {
            if (frames.Count == 0)
                return;

            string top = GetFrameSignature(frames[0]);
            if (string.IsNullOrWhiteSpace(top))
                return;

            IncrementCount(hotspots, top);
        }

        private static void IncrementCount(Dictionary<string, int> map, string key)
        {
            // Single hash lookup vs two (TryGetValue + indexer)
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
            count++;
        }
        
        public void Dispose() { }
    }

    internal class ThreadCategorization
    {
        public int TotalCount { get; set; }
        public int AliveCount { get; set; }
        public int GcCount { get; set; }
        public int FinalizerCount { get; set; }
        public int BackgroundCount { get; set; }
        public int ThreadPoolCount { get; set; }
        public int ThreadsWithActiveExceptionsCount { get; set; }

        // Finalizer thread detail
        public ClrThread? FinalizerThread { get; set; }
        public bool FinalizerIsBlocked { get; set; }
        public List<ClrStackFrame> FinalizerFrames { get; set; } = new();

        // Async state-machine chain depth
        public int AsyncChainThreadCount { get; set; }
        public int MaxAsyncChainDepth { get; set; }

        // Non-blocked user thread top-frame hotspots (Active Processing group)
        public Dictionary<string, int> ActiveThreadHotspots { get; set; } = new(StringComparer.Ordinal);

        public List<ThreadWithStackTrace> ThreadsWithLocks { get; set; } = new();
        public List<ThreadWithStackTrace> PotentiallyBlockedThreads { get; set; } = new();
        public List<ThreadWithStackTrace> ThreadsWithExceptions { get; set; } = new();
        public List<ThreadWithStackTrace> SampledThreads { get; set; } = new();
        public Dictionary<string, int> StateDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> GcModeDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> AppDomainDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> WaitCategoryDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ExceptionTypeDistribution { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TopFrameHotspots { get; set; } = new(StringComparer.Ordinal);
    }

    internal class ThreadWithStackTrace
    {
        public required ClrThread Thread { get; set; }
        public List<ClrStackFrame> TopFrames { get; set; } = new();
        public int StackRootCount { get; set; }
        public string? WaitCategory { get; set; }
        public string? WaitReason { get; set; }
        public string? ExceptionType { get; set; }
        public string? ExceptionMessage { get; set; }
    }

    internal sealed class WaitPattern
    {
        public WaitPattern(string category, string token, string reason)
        {
            Category = category;
            Token = token;
            Reason = reason;
        }

        public string Category { get; }
        public string Token { get; }
        public string Reason { get; }
    }

    internal sealed class WaitDetection
    {
        public WaitDetection(string category, string reason)
        {
            Category = category;
            Reason = reason;
        }

        public string Category { get; }
        public string Reason { get; }
    }
}


