using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ThreadAnalyzer
    {
        private readonly OutputWriter _writer;

        public ThreadAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrRuntime runtime)
        {
            _writer.WriteHeader("THREAD ANALYSIS:");

            var threadInfo = CategorizeThreads(runtime.Threads);

            _writer.WriteLine($"\nTotal Threads: {threadInfo.TotalCount}");

            PrintThreadStatistics(threadInfo);
            PrintThreadsWithLocks(threadInfo.ThreadsWithLocks);
            PrintBlockedThreads(threadInfo.PotentiallyBlockedThreads);

            _writer.WriteLine($"\nNote: Deadlock detection requires complex lock chain analysis.");
            _writer.WriteLine($"Review threads with high lock counts and waiting patterns manually.");

            _writer.WriteLine($"\n{new string('=', 80)}");
        }

        private ThreadCategorization CategorizeThreads(IEnumerable<ClrThread> threads)
        {
            var result = new ThreadCategorization();
            var threadsWithLocks = new List<ThreadWithStackTrace>();
            var blockedThreads = new List<ThreadWithStackTrace>();

            foreach (var thread in threads)
            {
                result.TotalCount++;

                if (thread.IsAlive)
                {
                    result.AliveCount++;

                    // Check for locks
                    if (thread.LockCount > 0)
                    {
                        var stackFrames = thread.EnumerateStackTrace().Take(5).ToList();
                        threadsWithLocks.Add(new ThreadWithStackTrace
                        {
                            Thread = thread,
                            TopFrames = stackFrames
                        });
                    }

                    // Check for potentially blocked threads (only check first 50 alive threads)
                    if (blockedThreads.Count < 50)
                    {
                        var topFrames = thread.EnumerateStackTrace().Take(3).ToList();
                        if (topFrames.Count > 0 && IsThreadWaiting(topFrames))
                        {
                            blockedThreads.Add(new ThreadWithStackTrace
                            {
                                Thread = thread,
                                TopFrames = topFrames
                            });
                        }
                    }
                }

                if (thread.IsGc)
                    result.GcCount++;

                if (thread.IsFinalizer)
                    result.FinalizerCount++;
            }

            // Sort threads with locks by lock count (descending)
            result.ThreadsWithLocks = threadsWithLocks
                .OrderByDescending(t => t.Thread.LockCount)
                .ToList();

            result.PotentiallyBlockedThreads = blockedThreads;

            return result;
        }

        private bool IsThreadWaiting(List<ClrStackFrame> frames)
        {
            // Pre-defined patterns to avoid repeated allocations
            ReadOnlySpan<string> waitPatterns = new[]
            {
                "wait",
                "sleep",
                "lock",
                "monitor.enter",
                "semaphore",
                "mutex"
            };

            foreach (var frame in frames)
            {
                string signature = frame.Method?.Signature ?? frame.ToString() ?? "";

                foreach (var pattern in waitPatterns)
                {
                    if (signature.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private void PrintThreadStatistics(ThreadCategorization info)
        {
            _writer.WriteLine($"Alive Threads: {info.AliveCount}");
            _writer.WriteLine($"GC Threads: {info.GcCount}");
            _writer.WriteLine($"Finalizer Threads: {info.FinalizerCount}");
        }

        private void PrintThreadsWithLocks(List<ThreadWithStackTrace> threadsWithLocks)
        {
            if (threadsWithLocks.Count > 0)
            {
                _writer.WriteLine($"\nThreads Holding Locks: {threadsWithLocks.Count}");
                _writer.WriteLine("\nTHREADS WITH LOCKS:");
                _writer.WriteSeparator();

                int displayCount = Math.Min(10, threadsWithLocks.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    var threadInfo = threadsWithLocks[i];
                    var thread = threadInfo.Thread;

                    _writer.WriteLine($"\nThread {thread.OSThreadId} (Managed: {thread.ManagedThreadId}):");
                    _writer.WriteLine($"  Lock Count: {thread.LockCount}");

                    if (threadInfo.TopFrames.Count > 0)
                    {
                        _writer.WriteLine($"  Stack Trace (top {threadInfo.TopFrames.Count} frames):");
                        foreach (var frame in threadInfo.TopFrames)
                        {
                            string method = frame.Method?.Signature ?? frame.ToString() ?? "Unknown";
                            _writer.WriteLine($"    {FormatHelper.TruncateString(method, 70)}");
                        }
                    }
                }

                if (threadsWithLocks.Count > 10)
                {
                    _writer.WriteLine($"\n... and {threadsWithLocks.Count - 10} more threads with locks");
                }
            }
            else
            {
                _writer.WriteLine("\nNo threads currently holding locks.");
            }
        }

        private void PrintBlockedThreads(List<ThreadWithStackTrace> blockedThreads)
        {
            _writer.WriteLine("\n\nPOTENTIALLY BLOCKED THREADS:");
            _writer.WriteSeparator();
            _writer.WriteLine("Threads that appear to be waiting (based on stack traces):\n");

            if (blockedThreads.Count == 0)
            {
                _writer.WriteLine("No obviously blocked threads detected (good!).");
            }
            else
            {
                int displayCount = Math.Min(10, blockedThreads.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    var threadInfo = blockedThreads[i];
                    var thread = threadInfo.Thread;

                    _writer.WriteLine($"Thread {thread.OSThreadId} (Managed: {thread.ManagedThreadId}):");
                    _writer.WriteLine($"  Locks: {thread.LockCount}");
                    _writer.WriteLine($"  Top frames:");

                    foreach (var frame in threadInfo.TopFrames)
                    {
                        string method = frame.Method?.Signature ?? frame.ToString() ?? "Unknown";
                        _writer.WriteLine($"    {FormatHelper.TruncateString(method, 70)}");
                    }
                    _writer.WriteLine(string.Empty);
                }

                if (blockedThreads.Count > 10)
                {
                    _writer.WriteLine($"... and {blockedThreads.Count - 10} more potentially blocked threads");
                }
            }
        }
    }

    internal class ThreadCategorization
    {
        public int TotalCount { get; set; }
        public int AliveCount { get; set; }
        public int GcCount { get; set; }
        public int FinalizerCount { get; set; }
        public List<ThreadWithStackTrace> ThreadsWithLocks { get; set; } = new();
        public List<ThreadWithStackTrace> PotentiallyBlockedThreads { get; set; } = new();
    }

    internal class ThreadWithStackTrace
    {
        public ClrThread Thread { get; set; }
        public List<ClrStackFrame> TopFrames { get; set; } = new();
    }
}
