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

            var threads = runtime.Threads.ToList();

            _writer.WriteLine($"\nTotal Threads: {threads.Count}");

            PrintThreadStatistics(threads);
            PrintThreadsWithLocks(threads);
            PrintBlockedThreads(threads);

            _writer.WriteLine($"\nNote: Deadlock detection requires complex lock chain analysis.");
            _writer.WriteLine($"Review threads with high lock counts and waiting patterns manually.");

            _writer.WriteLine($"\n{new string('=', 80)}");
        }

        private void PrintThreadStatistics(List<ClrThread> threads)
        {
            int aliveThreads = threads.Count(t => t.IsAlive);
            int gcThreads = threads.Count(t => t.IsGc);
            int finalizerThreads = threads.Count(t => t.IsFinalizer);

            _writer.WriteLine($"Alive Threads: {aliveThreads}");
            _writer.WriteLine($"GC Threads: {gcThreads}");
            _writer.WriteLine($"Finalizer Threads: {finalizerThreads}");
        }

        private void PrintThreadsWithLocks(List<ClrThread> threads)
        {
            var threadsWithLocks = threads.Where(t => t.IsAlive && t.LockCount > 0).ToList();

            if (threadsWithLocks.Any())
            {
                _writer.WriteLine($"\nThreads Holding Locks: {threadsWithLocks.Count}");
                _writer.WriteLine("\nTHREADS WITH LOCKS:");
                _writer.WriteSeparator();

                foreach (var thread in threadsWithLocks.OrderByDescending(t => t.LockCount).Take(10))
                {
                    _writer.WriteLine($"\nThread {thread.OSThreadId} (Managed: {thread.ManagedThreadId}):");
                    _writer.WriteLine($"  Lock Count: {thread.LockCount}");

                    if (thread.EnumerateStackTrace().Any())
                    {
                        _writer.WriteLine($"  Stack Trace (top 5 frames):");
                        foreach (var frame in thread.EnumerateStackTrace().Take(5))
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

        private void PrintBlockedThreads(List<ClrThread> threads)
        {
            _writer.WriteLine("\n\nPOTENTIALLY BLOCKED THREADS:");
            _writer.WriteSeparator();
            _writer.WriteLine("Threads that appear to be waiting (based on stack traces):\n");

            int blockedCount = 0;
            foreach (var thread in threads.Where(t => t.IsAlive).Take(50))
            {
                var stack = thread.EnumerateStackTrace().ToList();
                if (stack.Any())
                {
                    var topFrames = stack.Take(3).ToList();
                    bool isWaiting = topFrames.Any(f =>
                    {
                        string sig = f.Method?.Signature?.ToLower() ?? "";
                        return sig.Contains("wait") ||
                               sig.Contains("sleep") ||
                               sig.Contains("lock") ||
                               sig.Contains("monitor.enter") ||
                               sig.Contains("semaphore") ||
                               sig.Contains("mutex");
                    });

                    if (isWaiting)
                    {
                        blockedCount++;
                        if (blockedCount <= 10)
                        {
                            _writer.WriteLine($"Thread {thread.OSThreadId} (Managed: {thread.ManagedThreadId}):");
                            _writer.WriteLine($"  Locks: {thread.LockCount}");
                            _writer.WriteLine($"  Top frames:");
                            foreach (var frame in topFrames)
                            {
                                string method = frame.Method?.Signature ?? frame.ToString() ?? "Unknown";
                                _writer.WriteLine($"    {FormatHelper.TruncateString(method, 70)}");
                            }
                            _writer.WriteLine(string.Empty);
                        }
                    }
                }
            }

            if (blockedCount == 0)
            {
                _writer.WriteLine("No obviously blocked threads detected (good!).");
            }
            else if (blockedCount > 10)
            {
                _writer.WriteLine($"... and {blockedCount - 10} more potentially blocked threads");
            }
        }
    }
}
